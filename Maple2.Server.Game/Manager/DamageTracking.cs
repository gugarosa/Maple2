using Maple2.Model.Enum;
using Maple2.Server.Game.Model;

namespace Maple2.Server.Game.Manager;

public sealed class DamageTracking(int roomId, int targetId, long startTick) {
    private readonly object mutex = new();
    private long? stopTick;
    private long hits;
    private long criticalHits;
    private long misses;
    private long blocks;
    private long total;
    private long minimum;
    private long maximum;

    public int RoomId { get; } = roomId;
    public int TargetId { get; } = targetId;

    public void Record(int room, int target, DamageType type, long amount) {
        lock (mutex) {
            if (stopTick != null || room != RoomId || target != TargetId) {
                return;
            }
            if (type == DamageType.Miss) {
                misses++;
                return;
            }
            if (type == DamageType.Block) {
                blocks++;
                return;
            }

            // Healing does not contribute to outgoing damage.
            if (amount < 0) {
                return;
            }
            hits++;
            if (type == DamageType.Critical) {
                criticalHits++;
            }
            total += amount;
            minimum = hits == 1 ? amount : Math.Min(minimum, amount);
            maximum = Math.Max(maximum, amount);
        }
    }

    public void Stop(long tick) {
        lock (mutex) {
            stopTick ??= tick;
        }
    }

    public DamageSummary Snapshot(long tick) {
        lock (mutex) {
            double seconds = Math.Max(0, (stopTick ?? tick) - startTick) / 1000.0;
            return new DamageSummary(TargetId, stopTick == null, hits, criticalHits, misses, blocks,
                total, minimum, maximum, seconds);
        }
    }

    public static void Observe(IActor caster, IActor target, DamageType type, long amount) {
        if (target is not FieldNpc) {
            return;
        }

        FieldPlayer? player = caster as FieldPlayer;
        if (player == null && caster is FieldPet pet) {
            caster.Field.TryGetPlayer(pet.OwnerId, out player);
        }
        player?.Session.DamageTracking?.Record(target.Field.RoomId, target.ObjectId, type, amount);
    }
}

public readonly record struct DamageSummary(
    int TargetId, bool Running, long Hits, long CriticalHits, long Misses, long Blocks,
    long Total, long Minimum, long Maximum, double Seconds) {
    public double Dps => Seconds > 0 ? Total / Seconds : 0;
}
