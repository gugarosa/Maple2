using Maple2.Database.Storage;
using Maple2.Model;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Session;
using Maple2.Server.Game.Util;
using Serilog;

namespace Maple2.Server.Game.Manager.Items;

public class ItemManager {
    private readonly GameSession session;
    private readonly ItemStatsCalculator itemStatsCalc;

    public readonly EquipManager Equips;
    public readonly InventoryManager Inventory;
    public FurnishingManager Furnishing { get; private set; }

    public ItemManager(GameStorage.Request db, GameSession session, ItemStatsCalculator itemStatsCalc) {
        this.session = session;
        this.itemStatsCalc = itemStatsCalc;

        Equips = new EquipManager(db, session);
        Inventory = new InventoryManager(db, session);
        Furnishing = new FurnishingManager(db, session);
    }

    public void ReInstantiateFurnishing() {
        using GameStorage.Request db = session.GameStorage.Context();
        Furnishing = new FurnishingManager(db, session);
        Furnishing.Load();
    }

    /// <summary>
    /// Retrieves an gear from inventory or equipment.
    /// </summary>
    /// <param name="uid">Uid of the gear to retrieve</param>
    /// <returns>Item if it exists</returns>
    public Item? GetGear(long uid) {
        Item? item = Inventory.Get(uid, InventoryType.Gear);
        return item ?? Equips.Gear.Values.FirstOrDefault(gear => gear.Uid == uid);
    }

    /// <summary>
    /// Retrieves an outfit from inventory or equipment.
    /// </summary>
    /// <param name="uid">Uid of the outfit to retrieve</param>
    /// <returns>Item if it exists</returns>
    public Item? GetOutfit(long uid) {
        Item? item = Inventory.Get(uid, InventoryType.Outfit);
        return item ?? Equips.Outfit.Values.FirstOrDefault(outfit => outfit.Uid == uid);
    }

    public void Bind(Item item) {
        if (item.Transfer?.Bind(session.Player.Value.Character) == true) {
            session.Send(ItemInventoryPacket.UpdateItem(item));
        }
    }

    internal Item[]? PlanAdd(IReadOnlyList<Item> additions) {
        if (additions.Count == 0) {
            return [];
        }
        if (additions.Any(item => item.IsCurrency() || item.Type.IsMedal)) {
            Log.Error("Cannot plan non-inventory quest acceptance rewards");
            return null;
        }
        Item[]? inventory = Inventory.PlanAdd(additions.Where(item => !item.Type.IsFurnishing));
        Item[]? furnishings = Furnishing.PlanAdd(additions.Where(item => item.Type.IsFurnishing));
        return inventory == null || furnishings == null ? null : [.. inventory, .. furnishings];
    }

    internal void ApplyAdded(IReadOnlyList<Item> additions, bool notifyNew, ICollection<Action>? notifications = null) {
        NotifyAdded(ApplyAddedState(additions), notifyNew, notifications);
    }

    // Currency and both sides of a trade must also be applied before any condition callbacks run.
    internal List<(Item Item, int Added, bool New)> ApplyAddedState(IReadOnlyList<Item> additions) {
        try {
            return additions.Select(item => item.Group == ItemGroup.Furnishing
                ? Furnishing.ApplyAdded(item) : Inventory.ApplyAdded(item)).ToList();
        } catch {
            AbortPersistence("Committed item additions could not be applied to memory.");
            throw;
        }
    }

    internal void NotifyAdded(IEnumerable<(Item Item, int Added, bool New)> applied, bool notifyNew,
        ICollection<Action>? notifications = null) {
        foreach ((Item item, int added, bool isNew) in applied) {
            if (item.Group == ItemGroup.Furnishing) {
                Furnishing.NotifyAdded(item, added, isNew, notifyNew);
            } else {
                Inventory.NotifyAdded(item, added, isNew, notifyNew, notifications);
            }
        }
    }

    internal (long OwnerId, Item Item)[] Owned(IEnumerable<Item> items) => items.Select(item =>
        (item.Group == ItemGroup.Furnishing ? session.AccountId : session.CharacterId, item)).ToArray();

    internal void AfterUnlock(Action callback, ICollection<Action>? notifications) {
        if (notifications == null) {
            AfterUnlock(callback);
        } else {
            notifications.Add(callback);
        }
    }

    internal void AfterUnlock(Action callback) {
        session.Scheduler.Schedule(Invoke);
        return;

        void Invoke() {
            if (Monitor.IsEntered(session.Item)) {
                session.Scheduler.Schedule(Invoke);
                return;
            }
            // The scheduler invokes callbacks without its queue mutex; wait out another Item holder, then release it.
            lock (session.Item) { }
            if (session.PersistenceAborted) {
                return;
            }
            try {
                callback();
            } catch {
                session.AbortPersistence("A committed item's deferred effects could not be applied.");
                throw;
            }
        }
    }

    internal void AbortPersistence(string reason) {
        session.AbortPersistence(reason);
    }

    public bool MailItem(Item item) {
        lock (session.Item) {
            if (session.PersistenceAborted || item.Amount <= 0 || item.Uid != 0 &&
                (Inventory.Get(item.Uid) != null || Furnishing.GetCube(item.Uid) != null)) {
                return false;
            }
            using GameStorage.Request db = session.GameStorage.Context();
            var mail = new Mail(session.ServerTableMetadata.ConstantsTable.MailExpiryDays) {
                Type = MailType.System,
                ReceiverId = session.CharacterId,
                Content = "50000000", // id from string/en/systemmailcontentna.xml
            };

            mail.Items.Add(item);
            Mail? created = null;
            Item[]? committed;
            try {
                committed = db.TransferItems([], [], request =>
                    Save(request) && (created = request.CreateMail(mail)) != null);
            } catch {
                AbortPersistence("Item mail commitment could not be confirmed.");
                throw;
            }
            if (committed == null || created == null) {
                return false;
            }

            long mailId = created.Id;
            AfterUnlock(() => _ = NotifyMailAsync(mailId));
        }
        return true;
    }

    // The caller has released Item; notification failure must not retry the already durable mail.
    internal async Task NotifyMailAsync(long mailId) {
        try {
            using var call = session.World.MailNotificationAsync(new MailNotificationRequest {
                CharacterId = session.CharacterId,
                MailId = mailId,
            }, deadline: DateTime.UtcNow.AddSeconds(5));
            await call.ResponseAsync.ConfigureAwait(false);
        } catch (Exception ex) {
            Log.Warning(ex, "Committed mail {MailId} could not notify character {CharacterId}", mailId, session.CharacterId);
        }
    }

    public bool Save(GameStorage.Request db) {
        lock (session.Item) {
            return !session.PersistenceAborted && Equips.Save(db) && Inventory.Save(db) && Furnishing.Save(db);
        }
    }
}
