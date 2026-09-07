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

    internal void ApplyAdded(IReadOnlyList<Item> additions, bool notifyNew) {
        // Apply the entire committed batch before item conditions can grant or consume other items.
        List<(Item Item, int Added, bool New)> applied = additions.Select(item =>
            item.Group == ItemGroup.Furnishing ? Furnishing.ApplyAdded(item) : Inventory.ApplyAdded(item)).ToList();
        foreach ((Item item, int added, bool isNew) in applied) {
            if (item.Group == ItemGroup.Furnishing) {
                Furnishing.NotifyAdded(item, added, isNew, notifyNew);
            } else {
                Inventory.NotifyAdded(item, added, isNew, notifyNew);
            }
        }
    }

    public bool MailItem(Item item) {
        lock (session.Item) {
            using GameStorage.Request db = session.GameStorage.Context();
            var mail = new Mail(session.ServerTableMetadata.ConstantsTable.MailExpiryDays) {
                Type = MailType.System,
                ReceiverId = session.CharacterId,
                Content = "50000000", // id from string/en/systemmailcontentna.xml
            };

            mail = db.CreateMail(mail);
            if (mail == null) {
                return false;
            }

            if (item.Uid == 0) {
                item.Slot = -1;
                Item? newAdd = db.CreateItem(mail.Id, item);
                if (newAdd == null) {
                    return false;
                }
                item = newAdd;
            }

            mail.Items.Add(item);

            try {
                session.World.MailNotification(new MailNotificationRequest {
                    CharacterId = session.CharacterId,
                    MailId = mail.Id,
                });
            } catch { /* ignored */ }
        }
        return true;
    }

    public bool Save(GameStorage.Request db) {
        lock (session.Item) {
            return Equips.Save(db) && Inventory.Save(db) && Furnishing.Save(db);
        }
    }
}
