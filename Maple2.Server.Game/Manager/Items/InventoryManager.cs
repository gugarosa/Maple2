using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Maple2.Database.Storage;
using Maple2.Model;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Core.Packets;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Session;
using Maple2.Tools.Extensions;
using Serilog;
using static Maple2.Model.Error.ItemInventoryError;

namespace Maple2.Server.Game.Manager.Items;

public class InventoryManager {
    private const int BATCH_SIZE = 10;

    private readonly GameSession session;
    private ConstantsTable Constants => session.ServerTableMetadata.ConstantsTable;

    private readonly Dictionary<InventoryType, ItemCollection> tabs;
    private readonly List<Item> delete;

    private readonly ILogger logger = Log.Logger.ForContext<InventoryManager>();

    public InventoryManager(GameStorage.Request db, GameSession session) {
        this.session = session;
        tabs = new Dictionary<InventoryType, ItemCollection>();
        foreach (InventoryType type in Enum.GetValues<InventoryType>()) {
            session.Player.Value.Unlock.Expand.TryGetValue(type, out short expand);
            tabs[type] = new ItemCollection((short) (BaseSize(type) + expand));
        }

        delete = [];
        foreach ((InventoryType type, List<Item> load) in db.GetInventory(session.CharacterId)) {
            if (!tabs.TryGetValue(type, out ItemCollection? items)) continue;
            foreach (Item item in load) {
                if (items.Add(item).Count != 0) continue;
                var mail = new Mail(Constants.MailExpiryDays) {
                    ReceiverId = session.CharacterId,
                    Type = MailType.System,
                    Content = "50000000",
                };
                mail.Items.Add(item);
                if (db.CreateMail(mail) == null) {
                    throw new InvalidOperationException($"Could not preserve overflowing inventory item {item.Uid}.");
                }
                Log.Warning("Mailed overflowing inventory item {ItemUid} from tab {InventoryType}", item.Uid, type);
            }
        }
    }

    private short BaseSize(InventoryType type) => SlotSizes(type)[0];

    private short MaxExpandSize(InventoryType type) => SlotSizes(type)[1];

    private short[] SlotSizes(InventoryType type) {
        short[]? sizes = type switch {
            InventoryType.Gear => Constants.bagSlotTabGameCount,
            InventoryType.Outfit => Constants.bagSlotTabSkinCount,
            InventoryType.Mount => Constants.bagSlotTabSummonCount,
            InventoryType.Catalyst => Constants.bagSlotTabMaterialCount,
            InventoryType.FishingMusic => Constants.bagSlotTabLifeCount,
            InventoryType.Quest => Constants.bagSlotTabQuestCount,
            InventoryType.Gemstone => Constants.bagSlotTabGemCount,
            InventoryType.Misc => Constants.bagSlotTabMiscCount,
            InventoryType.LifeSkill => Constants.bagSlotTabMasteryCount,
            InventoryType.Pets => Constants.bagSlotTabPetCount,
            InventoryType.Consumable => Constants.bagSlotTabActiveSkillCount,
            InventoryType.Currency => Constants.bagSlotTabCoinCount,
            InventoryType.Badge => Constants.bagSlotTabBadgeCount,
            InventoryType.Lapenshard => Constants.bagSlotTabLapenShardCount,
            InventoryType.Fragment => Constants.bagSlotTabPieceCount,
            _ => throw new ArgumentOutOfRangeException($"Invalid InventoryType: {type}"),
        };
        if (sizes is not { Length: >= 2 }) {
            throw new InvalidDataException($"Missing base/max slot metadata for inventory type {type}.");
        }
        return sizes;
    }

    public void Load() {
        lock (session.Item) {
            foreach ((InventoryType type, ItemCollection items) in tabs) {
                session.Send(ItemInventoryPacket.Reset(type));
                session.Send(ItemInventoryPacket.ExpandCount(type, items.Size - BaseSize(type)));
                // Load items for above tab
                foreach (ImmutableList<Item> batch in items.Batch(BATCH_SIZE)) {
                    session.Send(ItemInventoryPacket.Load(batch));
                }
            }
        }
    }

    public bool Move(long uid, short dstSlot) {
        lock (session.Item) {
            if (session.PersistenceAborted) return false;
            if (dstSlot < 0) {
                session.Send(ItemInventoryPacket.Error(s_item_err_Invalid_slot));
                return false;
            }

            ItemCollection? items = tabs.Values.FirstOrDefault(collection => collection.Contains(uid));
            if (items == null || dstSlot >= items.Size) {
                return false;
            }

            // Attempt to stack
            Item? srcItem = items.Get(uid);
            if (srcItem != null) {
                IList<(Item, int)> results = items.Stack(srcItem, dstSlot);
                if (results.Count > 0) {
                    (Item item, int _) = results.First();
                    if (srcItem.Amount == 0) {
                        items.Remove(uid, out _);
                        Discard(srcItem);

                        session.Send(ItemInventoryPacket.Remove(uid));
                    } else {
                        session.Send(ItemInventoryPacket.UpdateAmount(srcItem.Uid, srcItem.Amount));
                    }
                    session.Send(ItemInventoryPacket.UpdateAmount(item.Uid, item.Amount));

                    return true;
                }
            }

            if (items.Remove(uid, out srcItem)) {
                short srcSlot = srcItem.Slot;
                if (items.RemoveSlot(dstSlot, out Item? removeDst)) {
                    items[srcSlot] = removeDst;
                }

                items[dstSlot] = srcItem;

                session.Send(ItemInventoryPacket.Move(removeDst?.Uid ?? 0, srcSlot, uid, dstSlot));
            }

            return true;
        }
    }

    public bool Add(Item add, bool notifyNew = false, bool commit = false, ICollection<Action>? notifications = null) {
        return Add(add, out _, notifyNew, commit, notifications);
    }

    public bool Add(Item add, out Item? addedItem, bool notifyNew = false, bool commit = false,
        ICollection<Action>? notifications = null) {
        addedItem = null;
        lock (session.Item) {
            if (session.PersistenceAborted || add.Amount <= 0) {
                return false;
            }

            if (add.IsCurrency()) {
                AddCurrency(add, notifications);
                Discard(add, commit, notifications);
                return true;
            }

            if (add.Type.IsMedal) {
                session.Survival.AddMedal(add);
                Discard(add, commit, notifications);
                return true;
            }

            Item[]? plan = session.Item.PlanAdd([add]);
            if (plan == null) {
                session.Send(ItemInventoryPacket.Error(s_err_inventory));
                return false;
            }

            using GameStorage.Request db = session.GameStorage.Context();
            var sources = new List<(long OwnerId, Item Item)>();
            if (add.Uid != 0) {
                Item? source = db.GetItem(add.Uid);
                long? ownerId = db.GetItemOwner(add.Uid);
                if (source == null || !ownerId.HasValue || ownerId != 0 && ownerId != add.OwnerId) {
                    return false;
                }
                sources.Add((ownerId.Value, source));
            }

            try {
                // Even ordinary additions create rows. Commit the entire plan, never a prefix of a split stack.
                Item[]? saved = db.TransferItems(sources, session.Item.Owned(plan), session.Item.Save);
                if (saved == null) {
                    logger.Error("Failed to persist inventory addition {ItemUid}/{ItemId}", add.Uid, add.Id);
                    return false;
                }

                delete.RemoveAll(item => item.Uid == add.Uid);
                session.Item.ApplyAdded(saved, notifyNew, notifications);
                Item first = saved.First(item => item.Id == add.Id);
                addedItem = first.Group == ItemGroup.Furnishing
                    ? session.Item.Furnishing.GetCube(first.Uid) : Get(first.Uid);

                return true;
            } catch {
                session.Item.AbortPersistence("Inventory addition commitment or receipt application could not be confirmed.");
                throw;
            }
        }
    }

    internal Item[]? PlanAdd(IEnumerable<Item> additions) {
        var result = new List<Item>();
        foreach (IGrouping<InventoryType, Item> group in additions.GroupBy(item => item.Inventory)) {
            if (!tabs.TryGetValue(group.Key, out ItemCollection? collection)) {
                return null;
            }

            Item[] items = group.Select(item => {
                Item copy = item.Clone();
                copy.Slot = item.Slot;
                copy.Group = ItemGroup.Default;
                if (copy.Metadata.Limit.TransferType == TransferType.BindOnLoot) {
                    copy.Transfer?.Bind(session.Player.Value.Character);
                }
                return copy;
            }).ToArray();
            Item[]? planned = collection.PlanAdd(items);
            if (planned == null) {
                return null;
            }
            result.AddRange(planned);
        }
        return result.ToArray();
    }

    internal IReadOnlyList<(Item Item, int Added, bool New)>? TransferTo(long uid, int amount,
        ItemCollection destination, long ownerId, short slot = -1) {
        lock (session.Item) {
            Item? source = Get(uid);
            if (session.PersistenceAborted || source == null || amount <= 0 || amount > source.Amount) {
                return null;
            }

            Item transfer = source.Clone(amount < source.Amount ? 0 : source.Uid);
            transfer.Amount = amount;
            transfer.Slot = slot;
            Item[]? plan = destination.PlanAdd([transfer]);
            if (plan == null) {
                return null;
            }

            var changes = plan.Select(item => (ownerId, item)).ToList();
            if (amount < source.Amount) {
                Item remainder = source.Clone();
                remainder.Amount -= amount;
                remainder.Slot = source.Slot;
                remainder.Group = source.Group;
                changes.Add((session.CharacterId, remainder));
            }

            using GameStorage.Request db = session.GameStorage.Context();
            try {
                Item[]? saved = db.TransferItems([(session.CharacterId, source)], changes,
                    request => session.Item.Save(request) && request.SaveItems(ownerId, destination.ToArray()));
                if (saved == null) {
                    logger.Error("Failed to transfer inventory item {ItemUid} to owner {OwnerId}", uid, ownerId);
                    return null;
                }

                tabs[source.Inventory].ApplyRemoved(uid, amount);
                var result = new List<(Item Item, int Added, bool New)>(plan.Length);
                foreach (Item item in saved.Take(plan.Length)) {
                    bool isNew = !destination.Contains(item.Uid);
                    (Item value, int added) = destination.ApplyAdded(item);
                    result.Add((value, added, isNew));
                }
                NotifyRemoved(source);
                return result;
            } catch {
                session.Item.AbortPersistence("Inventory deposit commitment or receipt application could not be confirmed.");
                throw;
            }
        }
    }

    internal bool TransferFrom(ItemCollection sourceItems, long ownerId, long uid, int amount, short slot = -1) {
        lock (session.Item) {
            Item? source = sourceItems.Get(uid);
            if (session.PersistenceAborted || source == null || amount <= 0 || amount > source.Amount) {
                return false;
            }

            Item transfer = source.Clone(amount < source.Amount ? 0 : source.Uid);
            transfer.Amount = amount;
            transfer.Slot = slot;
            Item[]? plan = session.Item.PlanAdd([transfer]);
            if (plan == null) {
                session.Send(ItemInventoryPacket.Error(s_err_inventory));
                return false;
            }

            var changes = session.Item.Owned(plan).ToList();
            if (amount < source.Amount) {
                Item remainder = source.Clone();
                remainder.Amount -= amount;
                remainder.Slot = source.Slot;
                remainder.Group = source.Group;
                changes.Add((ownerId, remainder));
            }

            using GameStorage.Request db = session.GameStorage.Context();
            try {
                Item[]? saved = db.TransferItems([(ownerId, source)], changes,
                    request => session.Item.Save(request) && request.SaveItems(ownerId, sourceItems.ToArray()));
                if (saved == null) {
                    logger.Error("Failed to transfer item {ItemUid} from owner {OwnerId} to inventory", uid, ownerId);
                    return false;
                }

                sourceItems.ApplyRemoved(uid, amount);
                session.Item.ApplyAdded(saved.Take(plan.Length).ToArray(), notifyNew: false);
                return true;
            } catch {
                session.Item.AbortPersistence("Inventory withdrawal commitment or receipt application could not be confirmed.");
                throw;
            }
        }
    }

    // The caller holds session.Item and has already committed the transfer.
    internal void ApplyRemoved(Item source, int amount) {
        tabs[source.Inventory].ApplyRemoved(source.Uid, amount);
        NotifyRemoved(source);
    }

    private void NotifyRemoved(Item source) {
        Item? remaining = Get(source.Uid);
        session.Send(remaining == null
            ? ItemInventoryPacket.Remove(source.Uid)
            : ItemInventoryPacket.UpdateAmount(source.Uid, remaining.Amount));
    }

    internal (Item Item, int Added, bool New) ApplyAdded(Item item) {
        ItemCollection collection = tabs[item.Inventory];
        bool isNew = collection[item.Slot] == null;
        (Item value, int added) = collection.ApplyAdded(item);
        return (value, added, isNew);
    }

    internal void NotifyAdded(Item item, int added, bool isNew, bool notifyNew, ICollection<Action>? notifications = null) {
        session.Send(isNew
            ? ItemInventoryPacket.Add(item)
            : ItemInventoryPacket.UpdateAmount(item.Uid, item.Amount));
        if (notifyNew) {
            session.Send(ItemInventoryPacket.NotifyNew(item.Uid, added));
        }
        int itemId = item.Id;
        int amount = item.Amount;
        session.Item.AfterUnlock(() => {
            if (session.PersistenceAborted) return;
            session.ConditionUpdate(ConditionType.item_collect, codeLong: itemId);
            session.ConditionUpdate(ConditionType.item_collect_revise, codeLong: itemId);
            session.ConditionUpdate(ConditionType.item_add, counter: amount, codeLong: itemId);
            session.ConditionUpdate(ConditionType.item_exist, counter: amount, codeLong: itemId);
        }, notifications);
    }

    private void AddCurrency(Item add, ICollection<Action>? notifications = null) {
        switch (add.Id) {
            case 90000001 or 90000002 or 90000003:
                long meso = session.Currency.CanAddMeso(add.Amount);
                session.Player.Value.Currency.Meso += meso;
                session.Currency.NotifyChanges(meso: meso, notifications: notifications);
                break;
            // case 90000011: // Meret (Secondary)
            // case 90000015: // GameMeret (Secondary)
            case 90000016: // EventMeret (Secondary)
            case 90000020: // RedMeret
            case 90000004: // Meret
                long meret = session.Currency.CanAddMeret(add.Amount);
                session.Player.Value.Currency.Meret += meret;
                session.Currency.NotifyChanges(meret: meret, notifications: notifications);
                break;
            case 90000006: // ValorToken
                AddToken(CurrencyType.ValorToken);
                break;
            case 90000008: // ExperienceOrb
                session.Exp.AddExp(ExpType.expDrop, additionalExp: add.Amount, notifications: notifications);
                break;
            case 90000009: // SpiritOrb
                session.Stats.Values[BasicAttribute.Spirit].Add(add.Amount);
                session.Send(StatsPacket.Update(session.Player, BasicAttribute.Spirit));
                break;
            case 90000010: // StaminaOrb
                session.Stats.Values[BasicAttribute.Stamina].Add(add.Amount);
                session.Send(StatsPacket.Update(session.Player, BasicAttribute.Stamina));
                break;
            case 90000013: // Rue
                AddToken(CurrencyType.Rue);
                break;
            case 90000014: // HaviFruit
                AddToken(CurrencyType.HaviFruit);
                break;
            case 90000017: // Treva
                AddToken(CurrencyType.Treva);
                break;
            case 90000027: // MesoToken
                AddToken(CurrencyType.MesoToken);
                break;
            // case 90000005: // DungeonKey
            // case 90000007: // Karma
            // case 90000012: // Unknown (BookIcon)
            // case 90000018: // ShadowFragment
            // case 90000019: // DistinctPaul
            case 90000021: // GuildFunds
            case 90000022: // ReverseCoin
                AddToken(CurrencyType.ReverseCoin);
                break;
            case 90000023: // MentorPoint
                AddToken(CurrencyType.MentorToken);
                break;
            case 90000024: // MenteePoint
                AddToken(CurrencyType.MenteeToken);
                break;
            case 90000025: // StarPoint
                AddToken(CurrencyType.StarPoint);
                break;
                // case 90000026: // Unknown (Blank)
        }

        void AddToken(CurrencyType type) => session.Currency.Set(type, session.Currency[type] + add.Amount, notifications);
    }

    public bool CanAdd(Item item) {
        return CanAdd([item]);
    }

    public bool CanAdd(ICollection<Item> items) {
        lock (session.Item) {
            return session.Item.PlanAdd(items.Where(item => !item.IsCurrency() && !item.Type.IsMedal).ToArray()) != null;
        }
    }

    public bool Remove(long uid, [NotNullWhen(true)] out Item? removed, int amount = -1) {
        lock (session.Item) {
            return RemoveInternal(uid, amount, out removed);
        }
    }

    public bool Consume(long uid, int amount = -1) {
        lock (session.Item) {
            return ConsumeInternal(uid, amount);
        }
    }

    public bool Consume(ICollection<IngredientInfo> ingredients) {
        lock (session.Item) {
            // Build this index so we don't need to find materials twice.
            Dictionary<ItemTag, IList<Item>> ingredientsByTag = ingredients.ToDictionary(
                entry => entry.Tag,
                entry => Filter(item => item.Metadata.Property.Tag == entry.Tag && !item.IsExpired())
            );

            // Validate
            foreach (IngredientInfo info in ingredients) {
                int remaining = info.Amount;
                foreach (Item ingredient in ingredientsByTag[info.Tag]) {
                    remaining -= ingredient.Amount;
                    if (remaining <= 0) {
                        break;
                    }
                }

                if (remaining > 0) {
                    return false;
                }
            }

            // Consume
            foreach (IngredientInfo info in ingredients) {
                int remaining = info.Amount;
                foreach (Item ingredient in ingredientsByTag[info.Tag]) {
                    int consume = Math.Min(remaining, ingredient.Amount);
                    if (!ConsumeInternal(ingredient.Uid, consume)) {
                        Log.Error("Failed to consume ingredient {ItemUid}", ingredient.Uid);
                        return false;
                    }

                    remaining -= consume;
                    if (remaining <= 0) {
                        break;
                    }
                }
            }

            return true;
        }
    }

    public bool ConsumeItemComponents(IReadOnlyList<ItemComponent> components, int quantityMultiplier = 1,
        ICollection<Action>? notifications = null) {
        lock (session.Item) {

            // Check for components
            Dictionary<int, IList<Item>> materialsById = components.ToDictionary(
                ingredient => ingredient.ItemId,
                ingredient => Filter(item => !item.IsExpired() && item.Id == ingredient.ItemId && (ingredient.Rarity < 0 || item.Rarity == ingredient.Rarity))
            );
            var materialsByTag = new Dictionary<ItemTag, IList<Item>>();
            foreach (ItemComponent ingredient in components) {
                if (materialsByTag.TryGetValue(ingredient.Tag, out IList<Item>? value)) {
                    foreach (Item item in session.Item.Inventory.Find(ingredient.ItemId, ingredient.Rarity)) {
                        value.Add(item);
                    }
                } else {
                    materialsByTag.Add(ingredient.Tag, session.Item.Inventory.Find(ingredient.ItemId, ingredient.Rarity).ToList());
                }
            }

            foreach (ItemComponent ingredient in components) {
                int remaining = ingredient.Amount * quantityMultiplier;
                if (ingredient.Tag != ItemTag.None) {
                    foreach (Item material in materialsByTag[ingredient.Tag]) {
                        remaining -= material.Amount;
                        if (remaining <= 0) {
                            break;
                        }
                    }
                } else {
                    foreach (Item material in materialsById[ingredient.ItemId]) {
                        remaining -= material.Amount;
                        if (remaining <= 0) {
                            break;
                        }
                    }
                }

                if (remaining > 0) {
                    return false;
                }
            }

            foreach (ItemComponent ingredient in components) {
                int remainingIngredients = ingredient.Amount * quantityMultiplier;
                if (ingredient.Tag != ItemTag.None) {
                    foreach (Item material in materialsByTag[ingredient.Tag]) {
                        int consume = Math.Min(remainingIngredients, material.Amount);
                        if (!ConsumeInternal(material.Uid, consume, notifications: notifications)) {
                            Log.Error("Failed to consume item uid: {ItemUid}, item id: {ItemId}", material.Uid, material.Id);
                            return false;
                        }

                        remainingIngredients -= consume;
                        if (remainingIngredients <= 0) {
                            break;
                        }
                    }
                } else {
                    foreach (Item material in materialsById[ingredient.ItemId]) {
                        int consume = Math.Min(remainingIngredients, material.Amount);
                        if (!ConsumeInternal(material.Uid, consume, notifications: notifications)) {
                            Log.Error("Failed to consume item uid: {ItemUid}, item id: {ItemId}", material.Uid, material.Id);
                            return false;
                        }

                        remainingIngredients -= consume;
                        if (remainingIngredients <= 0) {
                            break;
                        }
                    }
                }
            }
        }
        return true;
    }

    public void Sort(InventoryType type, bool removeExpired = false) {
        lock (session.Item) {
            if (!tabs.TryGetValue(type, out ItemCollection? items)) {
                session.Send(ItemInventoryPacket.Error(s_item_err_not_active_tab));
                return;
            }

            if (removeExpired) {
                IEnumerable<Item> toRemove = items.Where(item => item.IsExpired());
                foreach (Item item in toRemove) {
                    if (items.Remove(item.Uid, out Item? removed)) {
                        Discard(removed);
                    }
                }
            }

            items.Sort();

            session.Send(ItemInventoryPacket.Reset(type));
            foreach (ImmutableList<Item> batch in items.Batch(BATCH_SIZE)) {
                session.Send(ItemInventoryPacket.LoadTab(type, batch));
            }
        }
    }

    public bool Expand(InventoryType type, int expandRowCount = Constant.InventoryExpandRowCount) {
        // if expandRowCount is not divisible by 6, return false
        if (expandRowCount % 6 != 0) {
            return false;
        }

        lock (session.Item) {
            if (!tabs.TryGetValue(type, out ItemCollection? items)) {
                session.Send(ItemInventoryPacket.Error(s_item_err_not_active_tab));
                return false;
            }

            short newExpand = (short) (session.Player.Value.Unlock.Expand[type] + expandRowCount);
            if (newExpand > MaxExpandSize(type)) {
                // There is client side validation for this, but if the server side limits mismatch, use this error.
                session.Send(NoticePacket.MessageBox(StringCode.s_inventory_err_expand_max));
                return false;
            }

            if (session.Currency.Meret < Constants.InventoryExpandPrice1Row) {
                session.Send(ItemInventoryPacket.Error(s_cannot_charge_merat));
                return false;
            }

            if (!items.Expand((short) (BaseSize(type) + newExpand))) {
                return false;
            }

            session.Currency.Meret -= Constants.InventoryExpandPrice1Row;
            if (session.Player.Value.Unlock.Expand.ContainsKey(type)) {
                session.Player.Value.Unlock.Expand[type] = newExpand;
            } else {
                session.Player.Value.Unlock.Expand[type] = (short) expandRowCount;
            }

            session.Send(ItemInventoryPacket.ExpandCount(type, newExpand));
            session.Send(ItemInventoryPacket.ExpandComplete());
            return true;
        }
    }

    public short FreeSlots(InventoryType type) {
        lock (session.Item) {
            return !tabs.TryGetValue(type, out ItemCollection? items) ? (short) 0 : items.OpenSlots;
        }
    }

    public short TotalSlots(InventoryType type) {
        lock (session.Item) {
            return !tabs.TryGetValue(type, out ItemCollection? items) ? (short) 0 : items.Size;
        }
    }

    public Item? Get(long uid, InventoryType? type = null) {
        lock (session.Item) {
            if (type != null) {
                return tabs[(InventoryType) type].Get(uid);
            }

            return tabs.Values.FirstOrDefault(collection => collection.Contains(uid))?.Get(uid);
        }
    }

    public IList<Item> Filter(Func<Item, bool> condition, InventoryType? type = null) {
        lock (session.Item) {
            if (type != null) {
                return tabs[(InventoryType) type].Where(condition).ToList();
            }

            return tabs.Values.SelectMany(tab => tab.Where(condition)).ToList();
        }
    }

    public IEnumerable<Item> Find(int id, int rarity = -1) {
        lock (session.Item) {
            if (!session.ItemMetadata.TryGet(id, out ItemMetadata? metadata)) {
                yield break;
            }

            InventoryType type = metadata.Inventory();
            if (!tabs.TryGetValue(type, out ItemCollection? items)) {
                session.Send(ItemInventoryPacket.Error(s_item_err_not_active_tab));
                yield break;
            }

            foreach (Item item in items) {
                if (item.Id != id) continue;
                if (rarity != -1 && item.Rarity != rarity) continue;
                if (item.IsExpired()) continue;

                yield return item;
            }
        }
    }

    public IEnumerable<Item> Find(ItemTag itemTag) {
        lock (session.Item) {
            foreach ((InventoryType type, ItemCollection items) in tabs) {
                foreach (Item item in items) {
                    if (item.IsExpired()) continue;
                    if (item.Metadata.Property.Tag != itemTag) continue;
                    yield return item;
                }
            }
        }
    }

    public void Clear(InventoryType tab) {
        lock (session.Item) {
            if (!tabs.TryGetValue(tab, out ItemCollection? items)) {
                session.Send(ItemInventoryPacket.Error(s_item_err_not_active_tab));
                return;
            }

            foreach (Item item in items) {
                if (Remove(item.Uid, out _, item.Amount)) {
                    Discard(item);
                }
            }
        }
    }

    #region Internal (No Locks)
    private bool RemoveInternal(long uid, int amount, [NotNullWhen(true)] out Item? removed) {
        ItemCollection? items = tabs.Values.FirstOrDefault(collection => collection.Contains(uid));
        if (session.PersistenceAborted || items == null || amount == 0) {
            removed = null;
            return false;
        }

        if (amount > 0) {
            Item? item = items.Get(uid);
            if (item == null || item.Amount < amount) {
                session.Send(ItemInventoryPacket.Error(s_item_err_invalid_count));
                removed = null;
                return false;
            }

            // Otherwise, we would just do a full remove.
            if (item.Amount > amount) {
                using GameStorage.Request db = session.GameStorage.Context();
                Item split = item.Clone(0);
                split.Amount = amount;
                Item remainder = item.Clone();
                remainder.Slot = item.Slot;
                remainder.Group = item.Group;
                remainder.Amount -= amount;
                try {
                    Item[]? saved = db.TransferItems([(session.CharacterId, item)],
                        [(session.CharacterId, remainder), (session.CharacterId, split)],
                        request => Save(request));
                    if (saved == null) {
                        removed = null;
                        return false;
                    }
                    removed = saved[1];
                    item.Amount -= amount;

                    session.Send(ItemInventoryPacket.UpdateAmount(uid, item.Amount));
                    return true;
                } catch {
                    session.Item.AbortPersistence("Inventory split commitment or receipt application could not be confirmed.");
                    throw;
                }
            }
        }

        // Keep an exact, owned recovery row until the receiving operation commits its ownership change.
        Item? whole = items.Get(uid);
        using (GameStorage.Request db = session.GameStorage.Context()) {
            if (whole == null || !db.UpdateItem(session.CharacterId, whole)) {
                removed = null;
                return false;
            }
        }
        if (items.Remove(uid, out removed)) {
            session.Send(ItemInventoryPacket.Remove(uid));
            return true;
        }

        return false;
    }

    private bool ConsumeInternal(long uid, int amount, bool commit = false, ICollection<Action>? notifications = null) {
        ItemCollection? items = tabs.Values.FirstOrDefault(collection => collection.Contains(uid));
        if (session.PersistenceAborted || items == null || amount == 0) {
            return false;
        }

        if (amount > 0) {
            Item? item = items.Get(uid);
            if (item == null || item.IsExpired() || item.Amount < amount) {
                return false;
            }

            // Otherwise, we would just do a full remove.
            if (item.Amount > amount) {
                item.Amount -= amount;

                session.Send(ItemInventoryPacket.UpdateAmount(uid, item.Amount));
                return true;
            }
        }

        // Full remove of item
        if (items.Remove(uid, out Item? removed)) {
            Discard(removed, commit, notifications);
            session.Send(ItemInventoryPacket.Remove(uid));
            return true;
        }

        return false;
    }
    #endregion

    public void Discard(Item item, bool commit = false, ICollection<Action>? notifications = null) {
        lock (session.Item) {
            if (session.PersistenceAborted || item.Uid == 0) {
                return;
            }
            if (commit) {
                try {
                    using GameStorage.Request db = session.GameStorage.Context();
                    if (!db.SaveItems(0, item)) {
                        throw new InvalidOperationException($"Failed to retire item {item.Uid}.");
                    }
                } catch {
                    session.Item.AbortPersistence("Item retirement could not be confirmed.");
                    throw;
                }
            } else {
                delete.Add(item);
            }
            if (item.Type is { IsSkin: false, IsHair: false, IsDecal: false, IsEar: false, IsFace: false }) {
                int itemId = item.Id;
                session.Item.AfterUnlock(() => {
                    if (!session.PersistenceAborted) {
                        session.ConditionUpdate(ConditionType.item_destroy, codeLong: itemId);
                    }
                }, notifications);
            }
        }
    }

    public bool Save(GameStorage.Request db) {
        lock (session.Item) {
            return !session.PersistenceAborted && db.SaveItems(0, delete.ToArray()) &&
                   tabs.Values.All(tab => db.SaveItems(session.CharacterId, tab.ToArray()));
        }
    }

    public string Print(InventoryType type) {
        lock (session.Item) {
            if (!tabs.TryGetValue(type, out ItemCollection? items)) {
                return $"Inventory {type} not found.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Inventory {type}:");
            foreach (Item item in items) {
                sb.AppendLine($"- {item.Id} [{item.Metadata.Name}] (Amount: {item.Amount}, Slot: {item.Slot}, Expiry: {item.ExpiryTime}, Rarity: {item.Rarity}, Tag: {item.Metadata.Property.Tag})");
            }
            sb.AppendLine($"Total Items: {items.Count}, Open Slots: {items.OpenSlots}, Size: {items.Size}");
            return sb.ToString();
        }
    }
}
