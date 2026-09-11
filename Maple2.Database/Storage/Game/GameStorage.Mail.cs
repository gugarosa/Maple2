using System.Data.Common;
using Maple2.Model.Enum;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Mail = Maple2.Model.Game.Mail;
using Item = Maple2.Model.Game.Item;
using Player = Maple2.Model.Game.Player;

namespace Maple2.Database.Storage;

public partial class GameStorage {
    public partial class Request {
        public Mail? GetMail(long mailId, long characterId) {
            Model.Mail? model = Context.Mail.Find(characterId, mailId);
            if (model == null) {
                return null;
            }

            Mail mail = model;
            foreach (Item item in GetAllItems(mailId)) {
                mail.Items.Add(item);
            }

            return mail;
        }

        public ICollection<Mail> GetSentMail(long characterId) {
            Mail[] mails = Context.Mail.Where(mail => mail.SenderId == characterId)
                .AsEnumerable()
                .Select<Model.Mail, Mail>(mail => mail)
                .ToArray();

            foreach (Mail mail in mails) {
                foreach (Item item in GetAllItems(mail.Id)) {
                    mail.Items.Add(item);
                }
            }

            return mails;
        }

        // Binds all mails from an account to the first character that access them
        public void BindAccountMailsToCharacter(long accountId, long characterId) {
            if (HasFailed) {
                throw new InvalidOperationException("Cannot bind mail using a failed request.");
            }
            using IDbContextTransaction? transaction = Context.Database.CurrentTransaction == null
                ? Context.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted) : null;

            List<Model.Mail> mails = Context.Mail
                .FromSqlInterpolated($"SELECT * FROM `mail` WHERE `ReceiverId` = {accountId} ORDER BY `Id` FOR UPDATE")
                .AsTracking().ToList();
            if (mails.Count == 0) {
                transaction?.Commit();
                return;
            }

            foreach (Model.Mail mail in mails) {
                Context.Mail.Remove(mail);
            }

            if (!SaveChanges()) {
                throw new InvalidOperationException("Failed to detach account mail for binding.");
            }

            foreach (Model.Mail mail in mails) {
                mail.ReceiverId = characterId;
                Context.Mail.Add(mail);
            }

            if (!SaveChanges()) {
                throw new InvalidOperationException("Failed to bind account mail to character.");
            }
            transaction?.Commit();
        }

        public ICollection<Mail> GetAllMail(long characterId, long minId = 0) {
            Mail[] mails = Context.Mail.Where(mail => mail.ReceiverId == characterId)
                .Where(mail => mail.Id > minId)
                .AsEnumerable()
                .Select<Model.Mail, Mail>(mail => mail)
                .ToArray();

            foreach (Mail mail in mails) {
                foreach (Item item in GetAllItems(mail.Id)) {
                    mail.Items.Add(item);
                }
            }

            return mails;
        }

        public Mail? CreateMail(Mail mail) {
            if (HasFailed || mail.ReceiverId <= 0 || mail.Meso < 0 || mail.Meret < 0 || mail.GameMeret < 0 ||
                mail.Items.Any(item => item.Amount <= 0)) {
                return null;
            }

            bool committing = false;
            try {
                using IDbContextTransaction? transaction = Context.Database.CurrentTransaction == null
                    ? Context.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted) : null;
                Model.Mail model = mail;
                model.Id = 0;
                Context.Mail.Add(model);
                Context.SaveChanges();

                var sources = new List<(long OwnerId, Item Item)>();
                var changes = new List<(long OwnerId, Item Item)>();
                foreach (Item item in mail.Items) {
                    if (item.Uid != 0) {
                        long? ownerId = GetItemOwner(item.Uid);
                        if (!ownerId.HasValue || ownerId != 0 && ownerId != item.OwnerId) {
                            return null;
                        }
                        sources.Add((ownerId.Value, item));
                    }
                    Item attachment = item.Clone();
                    attachment.Slot = -1;
                    attachment.Group = ItemGroup.Default;
                    changes.Add((model.Id, attachment));
                }
                Item[]? saved = TransferItems(sources, changes);
                if (saved == null) {
                    return null;
                }

                Mail updatedMail = model;
                foreach (Item item in saved) {
                    updatedMail.Items.Add(item);
                }
                committing = transaction != null;
                transaction?.Commit();
                return updatedMail;
            } catch (Exception ex) when (!committing && (ex is DbUpdateException || ex.GetBaseException() is DbException)) {
                Logger.LogError(ex, "Failed to create mail for {ReceiverId}", mail.ReceiverId);
                return null;
            }
        }

        public Mail? MarkMailRead(long mailId, long characterId) {
            Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;

            Model.Mail? mail = Context.Mail.Find(characterId, mailId);
            if (mail == null || mail.ReadTime > DateTime.Now) {
                return null;
            }

            mail.ReadTime = DateTime.Now;
            Context.Mail.Update(mail);
            return SaveChanges() ? mail : null;
        }

        public (Mail Mail, Item[] Items, (DateTime AccountLastModified, DateTime CharacterLastModified)? CurrencyVersion)?
            CollectMail(Mail expected, Player player, IReadOnlyList<Item> additions,
                bool collectMeso, bool collectMeret, bool collectGameMeret, Func<Request, bool> saveItems) {
            if (HasFailed || expected.ReceiverId != player.Character.Id ||
                expected.Meso < 0 || expected.Meret < 0 || expected.GameMeret < 0) {
                return null;
            }
            bool committing = false;
            try {
                using IDbContextTransaction? transaction = Context.Database.CurrentTransaction == null
                    ? Context.Database.BeginTransaction(System.Data.IsolationLevel.ReadCommitted) : null;
                (DateTime AccountLastModified, DateTime CharacterLastModified)? currencyVersion = null;
                if (collectMeso || collectMeret || collectGameMeret) {
                    currencyVersion = SaveCurrency(player, collectMeso ? expected.Meso : 0,
                        collectMeret ? expected.Meret : 0, collectGameMeret ? expected.GameMeret : 0);
                    if (currencyVersion == null) return null;
                }
                if (!saveItems(this)) {
                    return null;
                }

                Model.Mail? stored = Context.Mail
                    .FromSqlInterpolated($"SELECT * FROM `mail` WHERE `ReceiverId` = {expected.ReceiverId} AND `Id` = {expected.Id} FOR UPDATE")
                    .AsTracking().SingleOrDefault();
                if (stored != null) Context.Entry(stored).Reload();
                if (stored == null ||
                    stored.Currency.Meso != expected.Meso || stored.Currency.Meret != expected.Meret ||
                    stored.Currency.GameMeret != expected.GameMeret ||
                    stored.Currency.MesoCollectTime != expected.MesoCollectTime ||
                    stored.Currency.MeretCollectTime != expected.MeretCollectTime ||
                    stored.Currency.GameMeretCollectTime != expected.GameMeretCollectTime ||
                    collectMeso && expected.MesoCollected() || collectMeret && expected.MeretCollected() ||
                    collectGameMeret && expected.GameMeretCollected()) {
                    return null;
                }
                long[] attachments = Context.Item.Where(item => item.OwnerId == expected.Id).Select(item => item.Id)
                    .OrderBy(uid => uid).ToArray();
                if (!attachments.SequenceEqual(expected.Items.Select(item => item.Uid).Order())) {
                    return null;
                }

                Mail updated = stored;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (collectMeso) updated.MesoCollectTime = now;
                if (collectMeret) updated.MeretCollectTime = now;
                if (collectGameMeret) updated.GameMeretCollectTime = now;
                Model.Mail model = updated;
                Context.Entry(stored).CurrentValues.SetValues(model);
                Item[]? saved = TransferItems(expected.Items.Select(item => (expected.Id, item)).ToArray(),
                    additions.Select(item => (item.Group == ItemGroup.Furnishing ? player.Account.Id : expected.ReceiverId, item)).ToArray());
                if (saved == null) {
                    return null;
                }
                committing = transaction != null;
                transaction?.Commit();
                return (updated, saved, currencyVersion);
            } catch (Exception ex) when (!committing && (ex is DbUpdateException || ex.GetBaseException() is DbException)) {
                Logger.LogError(ex, "Failed to collect mail {MailId}", expected.Id);
                return null;
            }
        }

        public bool DeleteMail(long mailId, long characterId) {
            Context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;

            Model.Mail? mail = Context.Mail.Find(characterId, mailId);
            if (mail == null) {
                return false;
            }
            Mail value = mail;
            if (!value.MesoCollected() || !value.MeretCollected() || !value.GameMeretCollected() ||
                Context.Item.Any(item => item.OwnerId == mailId)) {
                return false;
            }

            Context.Mail.Remove(mail);
            return SaveChanges();
        }

        public Mail? UpdateMail(Mail mail) {
            Model.Mail model = mail;
            Model.Mail? tracked = Context.Mail.Local.FirstOrDefault(value =>
                value.ReceiverId == mail.ReceiverId && value.Id == mail.Id);
            if (tracked == null) {
                Context.Mail.Update(model);
            } else {
                Context.Entry(tracked).CurrentValues.SetValues(model);
            }
            return SaveChanges() ? model : null;
        }
    }
}
