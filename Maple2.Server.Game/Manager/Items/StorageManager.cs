using System.Collections.Immutable;
using Maple2.Database.Storage;
using Maple2.Model.Game;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Packets;
using Maple2.Server.Game.Session;
using Maple2.Tools.Extensions;
using Serilog;
using static Maple2.Model.Error.StorageInventoryError;

namespace Maple2.Server.Game.Manager.Items;

public sealed class StorageManager : IDisposable {
    private const int BATCH_SIZE = 10;

    private readonly GameSession session;
    private ConstantsTable Constants => session.ServerTableMetadata.ConstantsTable;
    private readonly ItemCollection items;
    private long mesos;
    private short expand;

    public StorageManager(GameSession session) {
        this.session = session;

        using GameStorage.Request db = session.GameStorage.Context();
        (mesos, expand) = db.GetStorageInfo(session.AccountId);

        items = new ItemCollection((short) (Constant.BaseStorageCount + expand));

        foreach (Item item in db.GetStorage(session.AccountId)) {
            if (items.Add(item).Count == 0) {
                Log.Error("Failed to add storage item:{Uid}", item.Uid);
            }
        }
    }

    public void Dispose() {
        lock (session.Item) {
            if (session.PersistenceAborted) {
                if (session.Storage == this) session.Storage = null;
                return;
            }
            try {
                using GameStorage.Request db = session.GameStorage.Context();
                db.BeginTransaction();
                if (!db.SaveStorageInfo(session.AccountId, mesos, expand) ||
                    !db.SaveItems(session.AccountId, items.ToArray()) || !db.Commit()) {
                    throw new InvalidOperationException("Failed to save bank storage.");
                }
            } catch {
                session.Item.AbortPersistence("Bank storage closure could not be confirmed.");
                throw;
            }
            if (session.Storage == this) session.Storage = null;
        }
    }

    public void Delete(long uid) {
        lock (session.Item) {
            Item? item = items.Get(uid);
            if (item == null) {
                return;
            }

            if (items.Remove(uid, out item)) {
                session.Item.Inventory.Discard(item);
                session.Send(StorageInventoryPacket.Remove(uid));
            }
        }
    }

    public void Load() {
        lock (session.Item) {
            session.Send(StorageInventoryPacket.Reset());
            session.Send(StorageInventoryPacket.SlotsExpanded(expand));
            session.Send(StorageInventoryPacket.UpdateMesos(mesos));
            session.Send(StorageInventoryPacket.SlotsUsed(items.Count));
            foreach (ImmutableList<Item> batch in items.Batch(BATCH_SIZE)) {
                session.Send(StorageInventoryPacket.Load(batch));
            }
        }
    }

    public void Deposit(long uid, short slot, int amount) {
        lock (session.Item) {
            Item? deposit = session.Item.Inventory.Get(uid);
            if (deposit == null || amount <= 0 || deposit.Amount < amount) {
                session.Send(StorageInventoryPacket.Error(s_item_err_invalid_count));
                return;
            }

            if (items.OpenSlots == 0) {
                int remaining = items.GetStackResult(deposit, amount);
                if (amount == remaining) {
                    session.Send(StorageInventoryPacket.Error(s_item_err_store_full));
                    return;
                }

                // Stack what we can and ignore the rest.
                amount -= remaining;
            }

            IReadOnlyList<(Item Item, int Added, bool New)>? result =
                session.Item.Inventory.TransferTo(uid, amount, items, session.AccountId, slot);
            if (result == null) {
                session.Send(StorageInventoryPacket.Error(s_item_err_store_full));
                return;
            }

            foreach ((Item item, int _, bool isNew) in result) {
                session.Send(isNew
                    ? StorageInventoryPacket.Add(item)
                    : StorageInventoryPacket.Update(item.Uid, item.Amount));
            }
        }
    }

    public void Withdraw(long uid, short slot, int amount) {
        lock (session.Item) {
            Item? withdraw = items.Get(uid);
            if (withdraw == null || amount <= 0 || withdraw.Amount < amount) {
                session.Send(StorageInventoryPacket.Error(s_item_err_invalid_count));
                return;
            }

            if (withdraw.Binding != null && withdraw.Binding.CharacterId != session.CharacterId) {
                session.Send(StorageInventoryPacket.Error(s_item_err_binditem_store_out));
                return;
            }

            if (!session.Item.Inventory.TransferFrom(items, session.AccountId, uid, amount, slot)) {
                return;
            }

            session.Send(items.Get(uid) is { } remaining
                ? StorageInventoryPacket.Update(uid, remaining.Amount)
                : StorageInventoryPacket.Remove(uid));
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
                session.Send(StorageInventoryPacket.Move(removeDst?.Uid ?? 0, srcSlot, uid, dstSlot));
            }

            return true;
        }
    }

    // Deposit/Withdraw Mesos
    private bool CanDepositMeso(long amount) => 0 <= amount && mesos + amount <= Constant.MaxMeso;
    public void DepositMesos(long amount) {
        lock (session.Item) {
            if (!CanDepositMeso(amount)) {
                session.Send(StorageInventoryPacket.Error(s_store_err_deposit_invalid_money));
                return;
            }

            long negAmount = -amount;
            if (session.Currency.CanAddMeso(negAmount) != negAmount) {
                session.Send(StorageInventoryPacket.Error(s_store_err_deposit_invalid_money));
                return;
            }

            session.Currency.Meso -= amount;
            mesos += amount;
            session.Send(StorageInventoryPacket.UpdateMesos(mesos));
        }
    }

    private bool CanWithdrawMeso(long amount) => 0 <= amount && amount <= mesos;
    public void WithdrawMesos(long amount) {
        lock (session.Item) {
            if (!CanWithdrawMeso(amount)) {
                session.Send(StorageInventoryPacket.Error(s_store_err_deposit_invalid_money));
                return;
            }

            if (session.Currency.CanAddMeso(amount) != amount) {
                session.Send(StorageInventoryPacket.Error(s_store_err_deposit_invalid_money));
                return;
            }

            mesos -= amount;
            session.Send(StorageInventoryPacket.UpdateMesos(mesos));
            session.Currency.Meso += amount;
        }
    }

    public void Expand() {
        lock (session.Item) {
            short newSize = (short) (items.Size + Constant.InventoryExpandRowCount);
            if (newSize > Constants.StoreExpandMaxSlotCount) {
                session.Send(StorageInventoryPacket.Error(s_store_err_expand_max));
                return;
            }
            if (session.Currency.Meret < Constants.StoreExpandPrice1Row) {
                session.Send(StorageInventoryPacket.Error(s_cannot_charge_merat));
                return;
            }

            if (!items.Expand(newSize)) {
                session.Send(StorageInventoryPacket.Error(s_store_err_code));
                return;
            }

            session.Currency.Meret -= Constants.StoreExpandPrice1Row;
            expand += Constant.InventoryExpandRowCount;

            Load();
        }
    }

    public void Sort() {
        lock (session.Item) {
            items.Sort();

            session.Send(StorageInventoryPacket.Reset());
            foreach (ImmutableList<Item> batch in items.Batch(BATCH_SIZE)) {
                session.Send(StorageInventoryPacket.Reload(batch));
            }
        }
    }
}
