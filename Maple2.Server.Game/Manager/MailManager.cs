using System.Collections.Immutable;
using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Error;
using Maple2.Model.Game;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Session;
using Maple2.Tools.Extensions;
using Serilog;

namespace Maple2.Server.Game.Manager;

public sealed class MailManager {
    private const int BATCH_SIZE = 5;
    private const int FETCH_DELAY = 10;

    private readonly GameSession session;
    private DateTime lastFetch;
    private long lastReceivedMail;
    private int unreadMail;

    private readonly SortedList<long, Mail> inbox;

    private readonly ILogger logger = Log.ForContext<MailManager>();

    public MailManager(GameSession session) {
        this.session = session;

        inbox = new SortedList<long, Mail>();
        Fetch();
    }

    public void Notify(bool force = false) {
        if (Fetch(force) || force) {
            session.Send(MailPacket.Notify(unreadMail, unreadMail > 0));
        }
    }

    public void Load() {
        lock (inbox) {
            session.Send(MailPacket.StartList());
            foreach (ImmutableList<Mail> batch in inbox.Values.Batch(BATCH_SIZE)) {
                session.Send(MailPacket.Load(batch));
            }
            session.Send(MailPacket.EndList());
        }
    }

    public bool Delete(long mailId) {
        if (!inbox.ContainsKey(mailId)) {
            return false;
        }

        using GameStorage.Request db = session.GameStorage.Context();
        if (!db.DeleteMail(mailId, session.CharacterId)) {
            return false;
        }

        inbox.Remove(mailId);
        session.Send(MailPacket.Deleted(mailId));
        return true;
    }

    public MailError Read(long mailId) {
        if (!inbox.TryGetValue(mailId, out Mail? inboxMail)) {
            return MailError.mail_not_found;
        }

        using GameStorage.Request db = session.GameStorage.Context();
        Mail? mail = db.GetMail(mailId, session.CharacterId);
        if (mail == null) {
            return MailError.mail_not_found;
        }

        MailError error = ReadInternal(db, mail);
        if (error != MailError.none) {
            return error;
        }

        inboxMail.Update(mail);
        return MailError.none;
    }

    public MailError Collect(long mailId) {
        lock (session.Item)
            lock (inbox) {
                if (session.PersistenceAborted) return MailError.s_mail_error;
                if (!inbox.TryGetValue(mailId, out Mail? inboxMail)) {
                    return MailError.mail_not_found;
                }
                using GameStorage.Request db = session.GameStorage.Context();
                Mail? mail = db.GetMail(mailId, session.CharacterId);
                if (mail == null) {
                    return MailError.mail_not_found;
                }
                try {
                    MailError error = CollectInternal(db, mail);
                    if (error != MailError.none) {
                        return error;
                    }

                    inboxMail.Update(mail);
                    inboxMail.Items.Clear();
                    return MailError.none;
                } catch {
                    session.Item.AbortPersistence("Mail collection commitment or receipt application could not be confirmed.");
                    throw;
                }
            }
    }

    private bool Fetch(bool force = false) {
        long accountId = session.AccountId;
        long characterId = session.CharacterId;

        lock (inbox) {
            if (!force && lastFetch.AddSeconds(FETCH_DELAY) > DateTime.Now) {
                return false;
            }

            bool newMail = false;
            using GameStorage.Request db = session.GameStorage.Context();
            db.BindAccountMailsToCharacter(accountId, characterId);
            foreach (Mail mail in db.GetAllMail(characterId, lastReceivedMail)) {
                if (mail.ReadTime == 0) {
                    unreadMail++;
                }

                newMail = true;
                inbox.Add(mail.Id, mail);
            }

            lastFetch = DateTime.Now;
            if (inbox.Count <= 0) {
                return false;
            }

            lastReceivedMail = inbox.Keys[inbox.Count - 1];
            return newMail;
        }
    }

    #region Internal (No locks)
    // ReadTime on input mail will be modified.
    private MailError ReadInternal(GameStorage.Request db, Mail mail) {
        if (mail.ReadTime > 0) {
            return MailError.s_mail_error_alreadyread;
        }

        Mail? readMail = db.MarkMailRead(mail.Id, session.CharacterId);
        if (readMail == null) {
            return MailError.s_mail_error;
        }

        mail.ReadTime = readMail.ReadTime;
        session.Send(MailPacket.Read(mail));
        return MailError.none;
    }

    // Items on input mail will be cleared.
    private MailError CollectInternal(GameStorage.Request db, Mail mail) {
        if (mail.MesoCollected() && mail.MeretCollected() && mail.GameMeretCollected() && mail.Items.Count == 0) {
            return MailError.s_mail_error_already_receive;
        }

        if (mail.Items.Any(item => item.Amount <= 0)) {
            return MailError.s_mail_error_attachcount;
        }
        Item[]? plan = session.Item.PlanAdd(mail.Items.ToArray());
        if (plan == null) {
            return MailError.s_mail_error_receiveitem_to_inven;
        }
        bool collectMeso = !mail.MesoCollected() && session.Currency.CanAddMeso(mail.Meso) == mail.Meso;
        bool collectMeret = !mail.MeretCollected() && session.Currency.CanAddMeret(mail.Meret) == mail.Meret;
        bool collectGameMeret = !mail.GameMeretCollected() && session.Currency.CanAddGameMeret(mail.GameMeret) == mail.GameMeret;
        if (!collectMeso && !collectMeret && !collectGameMeret && mail.Items.Count == 0) {
            return MailError.s_mail_error_receiveitem_to_inven;
        }
        if ((collectMeso || collectMeret || collectGameMeret) && !session.SessionSave()) {
            return MailError.s_mail_error;
        }

        var result = db.CollectMail(mail, session.Player.Value, plan,
            collectMeso, collectMeret, collectGameMeret, session.Item.Save);
        if (result == null) {
            logger.Error("Mail {MailId} could not be collected; attachments and currency were preserved", mail.Id);
            return MailError.s_mail_error;
        }
        mail.Update(result.Value.Mail);
        mail.Items.Clear();
        var applied = session.Item.ApplyAddedState(result.Value.Items);
        if (result.Value.CurrencyVersion is { } version) {
            session.Player.Value.Account.LastModified = version.AccountLastModified;
            session.Player.Value.Character.LastModified = version.CharacterLastModified;
        }
        if (collectMeso) {
            session.Player.Value.Currency.Meso += mail.Meso;
        }
        if (collectMeret) {
            session.Player.Value.Currency.Meret += mail.Meret;
        }
        if (collectGameMeret) {
            session.Player.Value.Currency.GameMeret += mail.GameMeret;
        }
        session.Currency.NotifyChanges(collectMeso ? mail.Meso : null,
            collectMeret ? mail.Meret : null, collectGameMeret ? mail.GameMeret : null);
        session.Item.NotifyAdded(applied, notifyNew: true);

        session.Send(MailPacket.Collect(mail.Id));
        session.Send(MailPacket.CollectRead(mail));
        return MailError.none;
    }
    #endregion
}
