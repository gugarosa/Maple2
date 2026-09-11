using Maple2.Database.Storage;
using Maple2.Model;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Manager.Items;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Session;
using Serilog;
using static Maple2.Model.Error.TradeError;

namespace Maple2.Server.Game.Manager;

public class TradeManager : IDisposable {
    private static readonly ILogger Logger = Log.Logger.ForContext<TradeManager>();

    private enum TradeState {
        Completed = 0,
        Requested = 1,
        Acknowledged = 2,
        Started = 3,
        Finalized = 4,
    }

    private readonly Trader sender;
    private readonly Trader receiver;
    private TradeState state;
    private ConstantsTable Constants => sender.Session.ServerTableMetadata.ConstantsTable;

    private readonly object mutex = new();

    public TradeManager(GameSession sender, GameSession receiver) {
        this.sender = new Trader(sender);
        this.receiver = new Trader(receiver);
        state = TradeState.Requested;

        // Send a trade request
        receiver.Send(TradePacket.Request(sender.Player));

        // End the trade if not accepted before |TradeRequestDuration|.
        string receiverName = receiver.Player.Value.Character.Name;
        Task.Factory.StartNew(() => {
            Thread.Sleep(TimeSpan.FromSeconds(Constants.TradeRequestDuration));
            lock (mutex) {
                if (state is not (TradeState.Requested or TradeState.Acknowledged)) {
                    return;
                }

                sender.Send(TradePacket.Error(s_trade_error_timeout, name: receiverName));
                Dispose();
            }
        });
    }

    public void Dispose() {
        // Quarantine may be reached while Item is held; do not wait on the trade mutex in that case.
        if (ReleaseAborted()) return;
        lock (mutex) {
            if (ReleaseAborted()) return;
            if (state < TradeState.Started) {
                state = TradeState.Completed;
                ClearSessions();
                return;
            }
            if (!EndTrade(false)) {
                throw new InvalidOperationException("Failed to return staged trade items; original ownership was preserved.");
            }
        }
    }

    // Called by |receiver| to acknowledge trade request.
    public void Acknowledge(GameSession caller) {
        if (state != TradeState.Requested || !IsReceiver(caller)) {
            return;
        }

        lock (mutex) {
            state = TradeState.Acknowledged;
        }
        sender.Session.Send(TradePacket.Acknowledge());
    }

    // Called by |receiver| to accept trade request.
    public void Accept(GameSession caller) {
        if (state != TradeState.Acknowledged || !IsReceiver(caller)) {
            return;
        }

        lock (mutex) {
            state = TradeState.Started;
        }
        sender.Session.Send(TradePacket.StartTrade(receiver.Session.CharacterId));
        receiver.Session.Send(TradePacket.StartTrade(sender.Session.CharacterId));
    }

    public void Decline(GameSession caller) {
        if (state != TradeState.Acknowledged || !IsReceiver(caller)) {
            return;
        }

        lock (mutex) {
            state = TradeState.Completed;
        }
        sender.Session.Send(TradePacket.Decline(receiver.Session.PlayerName));
        Dispose();
    }

    public void AddItem(GameSession caller, long itemUid, int amount, int tradeSlot) {
        if (!IsTrader(caller)) {
            return;
        }

        (Trader self, Trader other) = GetTraders(caller);
        lock (mutex)
            lock (caller.Item) {
                if (state != TradeState.Started || self.Finalized) {
                    caller.Send(TradePacket.Error(s_trade_error_latched));
                    return;
                }
                Item? item = caller.Item.Inventory.Get(itemUid);
                if (tradeSlot < 0 || tradeSlot >= self.Items.Size || item == null ||
                    amount <= 0 || amount > item.Amount || item.IsLocked || item.IsExpired()) {
                    return;
                }
                if (item.Transfer?.Flag.HasFlag(TransferFlag.LimitTrade) == true && item.Transfer.RemainTrades < 1) {
                    return;
                }

                // Staged rows retain character ownership, so a reconnect recovers an interrupted trade.
                IReadOnlyList<(Item Item, int Added, bool New)>? results = caller.Item.Inventory.TransferTo(
                    itemUid, amount, self.Items, caller.CharacterId, (short) tradeSlot);
                if (results == null) {
                    caller.Send(TradePacket.Error(s_trade_error_itemcount));
                    return;
                }

                foreach (var result in results) {
                    self.Session.Send(TradePacket.AddItem(true, result.Item));
                    other.Session.Send(TradePacket.AddItem(false, result.Item));
                }

                OnTradeModified();
            }
    }

    public void RemoveItem(GameSession caller, long itemUid, int tradeSlot) {
        if (!IsTrader(caller)) {
            return;
        }

        (Trader self, Trader other) = GetTraders(caller);
        lock (mutex)
            lock (caller.Item) {
                if (state != TradeState.Started || self.Finalized) {
                    caller.Send(TradePacket.Error(s_trade_error_latched));
                    return;
                }
                Item? item = tradeSlot >= 0 && tradeSlot < self.Items.Size ? self.Items[(short) tradeSlot] : null;
                if (item == null || item.Uid != itemUid ||
                    !caller.Item.Inventory.TransferFrom(self.Items, caller.CharacterId, itemUid, item.Amount)) {
                    return;
                }

                self.Session.Send(TradePacket.RemoveItem(true, tradeSlot, itemUid));
                other.Session.Send(TradePacket.RemoveItem(false, tradeSlot, itemUid));
                OnTradeModified();
            }
    }

    public void SetMesos(GameSession caller, long amount) {
        // Can never set amount to a negative number!
        if (amount < 0 || state != TradeState.Started || !IsTrader(caller)) {
            return;
        }

        if (amount > Constants.TradeMaxMeso) {
            caller.Send(TradePacket.Error(s_trade_error_invalid_meso));
            return;
        }

        (Trader self, Trader other) = GetTraders(caller);
        if (self.Finalized) {
            caller.Send(TradePacket.Error(s_trade_error_latched));
            return;
        }

        lock (mutex)
            lock (caller.Item) {
                if (state != TradeState.Started || self.Finalized || self.Session.Currency.Meso < amount) {
                    caller.Send(TradePacket.Error(s_trade_error_meso));
                    return;
                }

                // Reserve only in the offer; completion revalidates the balance before debiting it.
                self.Mesos = amount;

                self.Session.Send(TradePacket.SetMesos(true, amount));
                other.Session.Send(TradePacket.SetMesos(false, amount));

                OnTradeModified();
            }
    }

    public void Finalize(GameSession caller) {
        if (state != TradeState.Started || !IsTrader(caller)) {
            return;
        }

        (Trader self, Trader other) = GetTraders(caller);
        lock (mutex) {
            if (!self.Finalized) {
                self.Session.Send(TradePacket.Finalize(true));
                other.Session.Send(TradePacket.Finalize(false));
                self.Finalized = true;
            }

            if (self.Finalized && other.Finalized) {
                state = TradeState.Finalized;
            }
        }
    }

    public void Complete(GameSession caller) {
        if (state != TradeState.Finalized || !IsTrader(caller)) {
            return;
        }

        (Trader self, Trader other) = GetTraders(caller);
        lock (mutex) {
            if (!self.Completed) {
                self.Session.Send(TradePacket.Complete(true));
                other.Session.Send(TradePacket.Complete(false));
                self.Completed = true;
            }

            if (self.Completed && other.Completed) {
                if (!EndTrade(true)) {
                    self.Completed = other.Completed = false;
                    state = TradeState.Started;
                    OnTradeModified();
                }
            }
        }
    }

    // |mutex| Locking is done externally.
    private bool EndTrade(bool success) {
        if (sender.Session.PersistenceAborted || receiver.Session.PersistenceAborted) {
            return false;
        }
        try {
            return EndTradeInternal(success);
        } catch {
            sender.Session.Item.AbortPersistence("Trade commitment or receipt application could not be confirmed.");
            receiver.Session.Item.AbortPersistence("Trade commitment or receipt application could not be confirmed.");
            throw;
        }
    }

    private bool EndTradeInternal(bool success) {
        if (state == TradeState.Completed) {
            ClearSessions();
            return true;
        }

        GameSession first = sender.Session.CharacterId < receiver.Session.CharacterId ? sender.Session : receiver.Session;
        GameSession second = first == sender.Session ? receiver.Session : sender.Session;
        lock (first.Item)
            lock (second.Item) {
                GameSession senderRecipient = success ? receiver.Session : sender.Session;
                GameSession receiverRecipient = success ? sender.Session : receiver.Session;
                Item[] senderItems = PrepareItems(sender, senderRecipient);
                Item[] receiverItems = PrepareItems(receiver, receiverRecipient);
                Item[]? senderPlan = senderRecipient.Item.PlanAdd(senderItems);
                Item[]? receiverPlan = receiverRecipient.Item.PlanAdd(receiverItems);
                if (success && (senderPlan == null || receiverPlan == null)) {
                    sender.Session.Send(TradePacket.Error(s_trade_error_slotcount));
                    receiver.Session.Send(TradePacket.Error(s_trade_error_slotcount));
                    return false;
                }

                long senderDelta = success ? receiver.Mesos - Fee(receiver.Mesos) - sender.Mesos : 0;
                long receiverDelta = success ? sender.Mesos - Fee(sender.Mesos) - receiver.Mesos : 0;
                if (success && (sender.Session.Currency.Meso < sender.Mesos || receiver.Session.Currency.Meso < receiver.Mesos ||
                    sender.Session.Currency.CanAddMeso(senderDelta) != senderDelta ||
                    receiver.Session.Currency.CanAddMeso(receiverDelta) != receiverDelta)) {
                    sender.Session.Send(TradePacket.Error(s_trade_error_meso));
                    receiver.Session.Send(TradePacket.Error(s_trade_error_meso));
                    return false;
                }
                if (success && (!first.SessionSave() || !second.SessionSave())) {
                    sender.Session.Send(TradePacket.Error(s_trade_error_system));
                    receiver.Session.Send(TradePacket.Error(s_trade_error_system));
                    return false;
                }

                var sources = new List<(long OwnerId, Item Item)>();
                var changes = new List<(long OwnerId, Item Item)>();
                if (senderPlan != null) {
                    sources.AddRange(sender.Items.Select(item => (sender.Session.CharacterId, item)));
                    changes.AddRange(senderRecipient.Item.Owned(senderPlan));
                }
                if (receiverPlan != null) {
                    sources.AddRange(receiver.Items.Select(item => (receiver.Session.CharacterId, item)));
                    changes.AddRange(receiverRecipient.Item.Owned(receiverPlan));
                }
                var mails = new List<(GameSession Session, Mail Mail)>();
                var currencyVersions = new Dictionary<GameSession, (DateTime AccountLastModified, DateTime CharacterLastModified)>();
                using GameStorage.Request db = sender.Session.GameStorage.Context();
                Item[]? saved = db.TransferItems(sources, changes, request => {
                    if (success && (!SaveCurrency(first) || !SaveCurrency(second))) {
                        return false;
                    }
                    if (!first.Item.Save(request) || !second.Item.Save(request)) {
                        return false;
                    }
                    return (senderPlan != null || MailReturns(senderRecipient, senderItems)) &&
                           (receiverPlan != null || MailReturns(receiverRecipient, receiverItems));

                    bool SaveCurrency(GameSession target) {
                        var version = request.SaveCurrency(target.Player.Value,
                            meso: target == sender.Session ? senderDelta : receiverDelta);
                        if (version == null) return false;
                        currencyVersions.Add(target, version.Value);
                        return true;
                    }

                    bool MailReturns(GameSession target, IEnumerable<Item> items) {
                        foreach (Item item in items) {
                            var mail = new Mail(Constants.MailExpiryDays) {
                                Type = MailType.System,
                                ReceiverId = target.CharacterId,
                                Content = "50000000",
                            };
                            mail.Items.Add(item);
                            Mail? created = request.CreateMail(mail);
                            if (created == null) return false;
                            mails.Add((target, created));
                        }
                        return true;
                    }
                });
                if (saved == null) {
                    sender.Session.Send(TradePacket.Error(s_trade_error_system));
                    receiver.Session.Send(TradePacket.Error(s_trade_error_system));
                    return false;
                }

                sender.Clear();
                receiver.Clear();
                state = TradeState.Completed;
                ClearSessions();
                var senderApplied = senderRecipient.Item.ApplyAddedState(saved.Take(senderPlan?.Length ?? 0).ToArray());
                var receiverApplied = receiverRecipient.Item.ApplyAddedState(saved.Skip(senderPlan?.Length ?? 0).ToArray());
                if (success) {
                    foreach (var (target, version) in currencyVersions) {
                        target.Player.Value.Account.LastModified = version.AccountLastModified;
                        target.Player.Value.Character.LastModified = version.CharacterLastModified;
                    }
                    sender.Session.Player.Value.Currency.Meso += senderDelta;
                    receiver.Session.Player.Value.Currency.Meso += receiverDelta;
                    sender.Session.Currency.NotifyChanges(meso: senderDelta);
                    receiver.Session.Currency.NotifyChanges(meso: receiverDelta);
                }
                senderRecipient.Item.NotifyAdded(senderApplied, notifyNew: success);
                receiverRecipient.Item.NotifyAdded(receiverApplied, notifyNew: success);
                foreach ((GameSession target, Mail mail) in mails) {
                    target.Item.AfterUnlock(() => _ = target.Item.NotifyMailAsync(mail.Id));
                }

                sender.Session.Send(TradePacket.EndTrade(success));
                receiver.Session.Send(TradePacket.EndTrade(success));
                return true;
            }

        long Fee(long mesos) => (long) (Constants.TradeFeePercent / 100f * mesos);

        Item[] PrepareItems(Trader trader, GameSession recipient) => trader.Items.Select(item => {
            Item copy = item.Clone();
            if (success && copy.Transfer?.Flag.HasFlag(TransferFlag.LimitTrade) == true) {
                copy.Transfer.RemainTrades--;
            }
            if (success && copy.Metadata.Limit.TransferType == TransferType.BindOnTrade) {
                copy.Transfer?.Bind(recipient.Player.Value.Character);
            }
            return copy;
        }).ToArray();
    }

    private void ClearSessions() {
        Interlocked.CompareExchange(ref sender.Session.Trade, null, this);
        Interlocked.CompareExchange(ref receiver.Session.Trade, null, this);
    }

    private bool ReleaseAborted() {
        if (!sender.Session.PersistenceAborted && !receiver.Session.PersistenceAborted) return false;
        sender.Session.Item.AbortPersistence("A trade participant requires a state reload.");
        receiver.Session.Item.AbortPersistence("A trade participant requires a state reload.");
        ClearSessions();
        return true;
    }

    // |mutex| Locking is done externally.
    private void OnTradeModified() {
        if (state != TradeState.Started) {
            Logger.Error("Trade was modified while in an invalid state: {State}", state);
            return;
        }

        if (sender.Finalized) {
            sender.Session.Send(TradePacket.UnFinalize(true));
            receiver.Session.Send(TradePacket.UnFinalize(false));
            sender.Finalized = false;
        }
        if (receiver.Finalized) {
            receiver.Session.Send(TradePacket.UnFinalize(true));
            sender.Session.Send(TradePacket.UnFinalize(false));
            receiver.Finalized = false;
        }
    }

    private (Trader self, Trader other) GetTraders(GameSession caller) {
        if (caller == sender.Session) return (sender, receiver);
        if (caller == receiver.Session) return (receiver, sender);
        throw new ArgumentException($"Invalid trader: {caller}");
    }

    private bool IsTrader(GameSession caller) => !sender.Session.PersistenceAborted && !receiver.Session.PersistenceAborted &&
        (caller == sender.Session || caller == receiver.Session);
    private bool IsReceiver(GameSession caller) => IsTrader(caller) && caller == receiver.Session;

    private class Trader {
        public readonly GameSession Session;
        public readonly ItemCollection Items;
        public long Mesos;

        public bool Finalized;
        public bool Completed;

        public Trader(GameSession session) {
            Session = session;
            Items = new ItemCollection(5);
        }

        public void Clear() {
            Mesos = 0;
            foreach (Item item in Items.ToArray()) {
                Items.Remove(item.Uid, out _);
            }
        }
    }
}
