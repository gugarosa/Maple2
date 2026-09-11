using Maple2.Database.Storage;
using Maple2.Model.Enum;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Manager.Items;
using Maple2.Server.Game.Model;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Session;
using Serilog;

namespace Maple2.Server.Game.Manager;

public sealed class PetManager : IDisposable {
    private readonly GameSession session;
    private readonly FieldPet pet;
    private readonly ItemCollection items;

    public Item Pet => pet.Pet;
    private readonly PetConfig config;

    private ItemPet ItemPet => Pet.Pet!;
    public int OwnerId => session.Player.ObjectId;

    public PetManager(GameSession session, FieldPet pet) {
        this.session = session;
        this.pet = pet;

        short itemSlots = 0;
        if (session.ItemMetadata.TryGetPet(pet.Value.Id, out PetMetadata? metadata) && metadata.Type == 0) {
            itemSlots = (short) metadata.ItemSlots;
        }

        items = new ItemCollection(itemSlots);

        // AddOrUpdate Pet Collection
        if (!session.Player.Value.Unlock.Pets.TryGetValue(pet.Value.Id, out short rarity) || rarity < Pet.Rarity) {
            session.Player.Value.Unlock.Pets[pet.Value.Id] = (short) Pet.Rarity;
            session.Send(PetPacket.AddCollection(pet.Value.Id, (short) Pet.Rarity));
            int petId = pet.Value.Id;
            session.Item.AfterUnlock(() => {
                if (!session.PersistenceAborted) session.ConditionUpdate(ConditionType.pet_collect, codeLong: petId);
            });
        }

        using GameStorage.Request db = session.GameStorage.Context();
        config = db.GetPetConfig(Pet.Uid);
        foreach (Item item in db.GetStorage(Pet.Uid)) {
            if (items.Add(item).Count == 0) {
                Log.Error("Failed to add storage item:{Uid}", item.Uid);
            }
        }

        session.Field?.Broadcast(PetPacket.Summon(pet));
    }

    public void Load() {
        session.Send(PetPacket.Load(OwnerId, ItemPet, config));
    }

    public void LoadInventory() {
        lock (session.Item) {
            session.Send(PetInventoryPacket.Load(items.ToList()));
        }
    }

    public StringCode Add(long uid, short slot, int amount) {
        lock (session.Item) {
            Item? deposit = session.Item.Inventory.Get(uid);
            if (deposit == null || amount <= 0 || deposit.Amount < amount) {
                return StringCode.s_item_err_invalid_count;
            }

            if (deposit.Pet != null) {
                return StringCode.s_pet_inventory_not_sendin_petitem;
            }

            if (items.OpenSlots == 0) {
                int remaining = items.GetStackResult(deposit, amount);
                if (amount == remaining) {
                    return StringCode.s_pet_inventory_not_sendin;
                }

                // Stack what we can and ignore the rest.
                amount -= remaining;
            }

            IReadOnlyList<(Item Item, int Added, bool New)>? result =
                session.Item.Inventory.TransferTo(uid, amount, items, Pet.Uid, slot);
            if (result == null) {
                return StringCode.s_pet_inventory_not_sendin;
            }

            foreach ((Item item, int _, bool isNew) in result) {
                session.Send(isNew
                    ? PetInventoryPacket.Add(item)
                    : PetInventoryPacket.Update(item.Uid, item.Amount));
            }

            return StringCode.s_empty_string;
        }
    }

    public StringCode Remove(long uid, short slot, int amount) {
        lock (session.Item) {
            Item? withdraw = items.Get(uid);
            if (withdraw == null || amount <= 0 || withdraw.Amount < amount) {
                return StringCode.s_item_err_invalid_count;
            }

            if (!session.Item.Inventory.TransferFrom(items, Pet.Uid, uid, amount, slot)) {
                return StringCode.s_err_inventory;
            }

            session.Send(items.Get(uid) is { } remaining
                ? PetInventoryPacket.Update(uid, remaining.Amount)
                : PetInventoryPacket.Remove(uid));
            return StringCode.s_empty_string;
        }
    }

    public bool Move(long uid, short dstSlot) {
        lock (session.Item) {
            if (dstSlot < 0 || dstSlot >= items.Size) {
                return false;
            }

            if (items.Remove(uid, out Item? srcItem)) {
                short srcSlot = srcItem.Slot;
                if (items.RemoveSlot(dstSlot, out Item? removeDst)) {
                    items[srcSlot] = removeDst;
                }

                items[dstSlot] = srcItem;
                session.Send(PetInventoryPacket.Move(removeDst?.Uid ?? 0, srcSlot, uid, dstSlot));
            }

            return true;
        }
    }

    public void BadgeChanged(ItemBadge? badge) {
        pet.UpdateSkin(badge?.PetSkinId ?? 0);
    }

    public void Rename(string name) {
        ItemPet.Name = name;
        ItemPet.RenameRemaining = 0;

        session.Send(PetPacket.Rename(OwnerId, ItemPet));
    }

    public void UpdatePotionConfig(PetPotionConfig[] potionConfig) {
        config.PotionConfig = potionConfig;

        session.Send(PetPacket.UpdatePotionConfig(OwnerId, config.PotionConfig));
    }

    public void UpdateLootConfig(PetLootConfig lootConfig) {
        config.LootConfig = lootConfig;

        session.Send(PetPacket.UpdateLootConfig(OwnerId, config.LootConfig));
    }

    public void Dispose() {
        lock (session.Item) {
            if (!session.PersistenceAborted) {
                try {
                    using GameStorage.Request db = session.GameStorage.Context();
                    db.BeginTransaction();
                    if (!db.SavePetConfig(Pet.Uid, config) || !db.SaveItems(Pet.Uid, items.ToArray()) || !db.Commit()) {
                        throw new InvalidOperationException("Failed to save pet inventory.");
                    }
                } catch {
                    session.Item.AbortPersistence("Pet inventory closure could not be confirmed.");
                    throw;
                }
            }
        }

        session.Field?.RemovePet(pet.ObjectId);
        session.Field?.Broadcast(PetPacket.UnSummon(pet));
        if (session.Pet == this) session.Pet = null;
    }
}
