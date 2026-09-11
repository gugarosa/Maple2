using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Maple2.Model.Enum;
using Maple2.Model.Game;

namespace Maple2.Server.Game.Manager.Items;

/// <remarks>
/// This class is thread-safe.
/// </remarks>
public class ItemCollection : IEnumerable<Item> {
    private readonly ReaderWriterLockSlim mutex;
    private readonly IDictionary<long, short> uidToSlot;
    private Item?[] items;

    public short OpenSlots => (short) (Size - Count);
    public short Size => (short) items.Length;
    public short Count { get; private set; }

    public ItemCollection(short size) {
        mutex = new ReaderWriterLockSlim();
        uidToSlot = new Dictionary<long, short>();
        items = new Item[size];

        Count = 0;
    }

    public Item? this[short slot] {
        get {
            mutex.EnterReadLock();
            try {
                return ValidSlot(slot) ? items[slot] : null;
            } finally {
                mutex.ExitReadLock();
            }
        }
        set {
            // Setting slot to null or Slot is already taken
            // Use `RemoveSlot()` to remove items.
            if (value == null || slot < 0 || slot >= Size || this[slot] != null) {
                return;
            }

            mutex.EnterUpgradeableReadLock();
            try {
                mutex.EnterWriteLock();
                try {
                    value.Slot = slot;
                    uidToSlot[value.Uid] = slot;
                    items[slot] = value;
                    Count++;
                } finally {
                    mutex.ExitWriteLock();
                }
            } finally {
                mutex.ExitUpgradeableReadLock();
            }
        }
    }

    /// <summary>
    /// Stacks the added item onto any available stacks
    /// <paramref name="add"/> will be modified to reflect the remaining amount.
    /// </summary>
    /// <param name="add">The item to stack</param>
    /// <param name="slot">The specific slot to stack on. If unspecified, attempt to stack on all possible items.</param>
    /// <returns>A list of items which were stacked on and their added amounts.</returns>
    public IList<(Item, int Added)> Stack(Item add, short slot = -1) {
        var result = new List<(Item, int)>();
        mutex.EnterReadLock();
        try {
            if (slot >= 0) {
                Item? item = items[slot];
                if (item == null) {
                    return result;
                }

                int added = StackItem(item, add);
                if (added > 0) {
                    result.Add((item, added));
                }

                return result;
            }

            foreach (Item item in GetInternalEnumerator()) {
                int added = StackItem(item, add);
                if (added == 0) {
                    continue;
                }

                result.Add((item, added));

                if (add.Amount <= 0) {
                    return result;
                }
            }
        } finally {
            mutex.ExitReadLock();
        }

        return result;
    }

    /// <summary>
    /// Inserts the added item into the first open slot.
    /// </summary>
    /// <param name="insert">The item to insert</param>
    /// <returns>true if the item was successfully inserted</returns>
    private bool Append(Item insert) {
        if (OpenSlots <= 0) {
            return false;
        }

        for (short i = 0; i < Size; i++) {
            if (this[i] != null) {
                continue;
            }

            this[i] = insert;
            insert.Slot = i;
            return true;
        }

        return false;
    }

    /// <summary>
    /// - If the added item has an existing slot, it will attempt to add to that slot.
    /// - If <paramref name="stack"/> is true, the item will first be stacked on existing
    /// items of the same type.
    /// - Any remaining amount will be added to the first open slot.
    ///
    /// If the item cannot be fully added, no action will take place.
    /// </summary>
    /// <param name="add">The item to add</param>
    /// <param name="stack">Whether or not to attempt stacking the item to add</param>
    /// <returns>A list of items which were stacked on and their added amounts.</returns>
    public IList<(Item, int Added)> Add(Item add, bool stack = false) {
        if (OpenSlots <= 0 && (!stack || GetStackResult(add) > 0)) {
            return [];
        }

        // Prefer item slot if specified
        if (ValidSlot(add.Slot) && this[add.Slot] == null) {
            this[add.Slot] = add;
            return [(add, add.Amount)];
        }

        IList<(Item, int Added)> result = new List<(Item, int)>();
        if (stack) {
            result = Stack(add);
            if (add.Amount <= 0) {
                return result;
            }
        }

        // All stacks are maxed out at this point, remaining items go in first open slot.
        if (Append(add)) {
            result.Add((add, add.Amount));
            return result;
        }

        return [];
    }

    public Item? Get(long uid) {
        short slot;
        mutex.EnterReadLock();
        try {
            if (!uidToSlot.TryGetValue(uid, out slot)) {
                return null;
            }
        } finally {
            mutex.ExitReadLock();
        }

        return this[slot];
    }

    public bool Contains(long uid) {
        mutex.EnterReadLock();
        try {
            return uidToSlot.ContainsKey(uid);
        } finally {
            mutex.ExitReadLock();
        }
    }

    public bool Remove(long uid, [NotNullWhen(true)] out Item? removed) {
        mutex.EnterUpgradeableReadLock();
        try {
            if (!uidToSlot.TryGetValue(uid, out short slot)) {
                removed = null;
                return false;
            }

            return RemoveSlot(slot, out removed);
        } finally {
            mutex.ExitUpgradeableReadLock();
        }
    }

    public bool RemoveSlot(short slot, [NotNullWhen(true)] out Item? removed) {
        removed = this[slot];
        if (removed == null) {
            return false;
        }

        mutex.EnterWriteLock();
        try {
            items[slot] = null;
            uidToSlot.Remove(removed.Uid);
            Count--;
            return true;
        } finally {
            mutex.ExitWriteLock();
        }
    }

    public void Sort() {
        mutex.EnterWriteLock();
        try {
            Array.Sort(items, SortItem);

            // Update the slot mapping
            uidToSlot.Clear();
            short i = 0;
            while (i < items.Length && items[i] is { } item) {
                item.Slot = i;
                uidToSlot[items[i]!.Uid] = i;
                i++;
            }
        } finally {
            mutex.ExitWriteLock();
        }
    }

    public bool Expand(short newSize) {
        if (newSize <= Size) {
            return false;
        }

        mutex.EnterWriteLock();
        try {
            Array.Resize(ref items, newSize);
            return true;
        } finally {
            mutex.ExitWriteLock();
        }
    }

    /// <summary>
    /// Simulates stacking an item on existing items in this collection without modification.
    /// Ignores any open slots.
    /// </summary>
    /// <param name="item">The item to attempt to add.</param>
    /// <param name="amount">The amount of item to add.</param>
    /// <returns>The remaining amount that could not be stacked.</returns>
    public int GetStackResult(Item item, int amount = -1) {
        int remaining = amount < 0 ? item.Amount : Math.Min(amount, item.Amount);
        mutex.EnterReadLock();
        try {
            foreach (Item existing in GetInternalEnumerator()) {
                if (!CanStack(existing, item)) continue;

                int available = existing.Metadata.Property.SlotMax - existing.Amount;
                remaining -= available;
                if (remaining <= 0) {
                    return 0;
                }
            }
        } finally {
            mutex.ExitReadLock();
        }

        return remaining;
    }

    // Plan on detached copies so a failed batch cannot consume stack space or change live items.
    internal Item[]? PlanAdd(IEnumerable<Item> additions, bool furnishing = false) {
        mutex.EnterReadLock();
        try {
            Item?[] planned = items.Select(item => {
                if (item == null) return null;
                Item copy = item.Clone();
                copy.Slot = item.Slot;
                copy.Group = item.Group;
                return copy;
            }).ToArray();
            var incomingUids = new HashSet<long>();

            foreach (Item source in additions) {
                if (source.Amount <= 0 || source.Uid != 0 &&
                    (uidToSlot.ContainsKey(source.Uid) || !incomingUids.Add(source.Uid))) {
                    return null;
                }

                Item add = source.Clone();
                add.Group = furnishing ? ItemGroup.Furnishing : source.Group;
                int slotMax = Math.Max(1, add.Metadata.Property.SlotMax);
                if (furnishing) {
                    Item? stored = planned.FirstOrDefault(item =>
                        item != null && item.Id == add.Id && item.Template?.Url == add.Template?.Url);
                    if (stored != null) {
                        if (add.Amount > slotMax - stored.Amount) {
                            return null;
                        }
                        stored.Amount += add.Amount;
                        continue;
                    }
                    if (add.Amount > slotMax) {
                        return null;
                    }
                } else {
                    foreach (Item? item in planned) {
                        if (item != null) {
                            StackItem(item, add);
                        }
                        if (add.Amount == 0) break;
                    }
                }

                bool retainUid = true;
                while (add.Amount > 0) {
                    int slot = retainUid && ValidSlot(source.Slot) && planned[source.Slot] == null
                        ? source.Slot : Array.FindIndex(planned, item => item == null);
                    if (slot < 0) {
                        return null;
                    }
                    // A transferred UID belongs to only one output stack; extra stacks are new rows.
                    Item split = add.Clone(retainUid ? add.Uid : 0);
                    split.Slot = (short) slot;
                    split.Group = add.Group;
                    split.Amount = Math.Min(add.Amount, slotMax);
                    planned[slot] = split;
                    add.Amount -= split.Amount;
                    retainUid = false;
                }
            }

            return planned.Where(item => item != null && items[item.Slot]?.Amount != item.Amount)
                .Select(item => item!).ToArray();
        } finally {
            mutex.ExitReadLock();
        }
    }

    // The owning ItemManager lock must cover planning, persistence, and application.
    internal (Item Item, int Added) ApplyAdded(Item saved) {
        mutex.EnterWriteLock();
        try {
            if (!ValidSlot(saved.Slot) || saved.Uid <= 0) {
                throw new InvalidOperationException("Invalid persisted inventory addition.");
            }

            Item? existing = items[saved.Slot];
            if (existing != null) {
                if (existing.Uid != saved.Uid || saved.Amount <= existing.Amount) {
                    throw new InvalidOperationException("Inventory changed during a planned addition.");
                }
                int added = saved.Amount - existing.Amount;
                existing.Amount = saved.Amount;
                return (existing, added);
            }

            uidToSlot.Add(saved.Uid, saved.Slot);
            items[saved.Slot] = saved;
            Count++;
            return (saved, saved.Amount);
        } finally {
            mutex.ExitWriteLock();
        }
    }

    internal void ApplyRemoved(long uid, int amount) {
        mutex.EnterWriteLock();
        try {
            if (!uidToSlot.TryGetValue(uid, out short slot) || items[slot] is not { } item ||
                amount <= 0 || item.Amount < amount) {
                throw new InvalidOperationException("Inventory changed during a planned removal.");
            }
            if (item.Amount == amount) {
                items[slot] = null;
                uidToSlot.Remove(uid);
                Count--;
            } else {
                item.Amount -= amount;
            }
        } finally {
            mutex.ExitWriteLock();
        }
    }

    private bool ValidSlot(short slot) => slot >= 0 && slot < Size;

    // Enumerate array while ignoring nulls
    public IEnumerator<Item> GetEnumerator() {
        mutex.EnterReadLock();
        try {
            return items.Where(item => item != null)
                .Select(item => item!)
                .GetEnumerator();
        } finally {
            mutex.ExitReadLock();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() {
        return GetEnumerator();
    }

    private IEnumerable<Item> GetInternalEnumerator() {
        foreach (Item? item in items) {
            if (item == null) continue;
            yield return item;
        }
    }

    private static int StackItem(Item stackTo, Item stackFrom) {
        if (!CanStack(stackTo, stackFrom)) {
            return 0;
        }

        int available = stackTo.Metadata.Property.SlotMax - stackTo.Amount;
        int added = Math.Min(available, stackFrom.Amount);
        stackFrom.Amount -= added;
        stackTo.Amount += added;

        return added;
    }

    private static bool CanStack(Item item, Item stack) {
        return (item.Uid == 0 || item.Uid != stack.Uid)
               && item.Id == stack.Id
               && item.Rarity == stack.Rarity
               && item.Amount < item.Metadata.Property.SlotMax
               && Equals(item.Transfer, stack.Transfer)
               && item.ExpiryTime / 60 == stack.ExpiryTime / 60; // Expiry time is in seconds, convert to minutes
    }

    private static int SortItem(Item? x, Item? y) {
        if (x == null) return 1;
        if (y == null) return -1;

        int result = x.Id.CompareTo(y.Id);
        if (result != 0) return result;
        result = x.Rarity.CompareTo(y.Rarity);
        if (result != 0) return result;
        return x.Amount.CompareTo(y.Amount);
    }
}
