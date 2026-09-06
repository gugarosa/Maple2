namespace Maple2.Server.Game.Manager.Field;

internal sealed class FieldAdmission {
    private readonly int capacity;
    private readonly Dictionary<long, object> occupants = new();
    private readonly object mutex = new();

    public FieldAdmission(int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
    }

    public bool IsFull {
        get {
            lock (mutex) {
                return occupants.Count >= capacity;
            }
        }
    }

    public bool TryReserve(long characterId, object owner) {
        ArgumentNullException.ThrowIfNull(owner);
        lock (mutex) {
            if (!occupants.ContainsKey(characterId) && occupants.Count >= capacity) {
                return false;
            }
            occupants[characterId] = owner;
            return true;
        }
    }

    public void Release(long characterId, object owner) {
        lock (mutex) {
            if (occupants.TryGetValue(characterId, out object? current) && ReferenceEquals(current, owner)) {
                occupants.Remove(characterId);
            }
        }
    }
}
