using Maple2.Model.Enum;
using Maple2.PacketLib.Tools;
using Maple2.Tools;

namespace Maple2.Model.Game.Dungeon;

public class DungeonRecord : IByteSerializable {
    public readonly int DungeonId;
    public bool AccountWide { get; init; }
    public byte UnionSubClears { get; set; }
    public byte UnionClears { get; set; }
    public long UnionSubCooldownTimestamp { get; set; }
    public long UnionCooldownTimestamp { get; set; }
    public long CooldownTimestamp { get; set; }
    public long ClearTimestamp { get; set; }
    public int TotalClears { get; set; }
    public short LifetimeRecord { get; set; }
    public short CurrentRecord { get; set; }
    public byte ExtraSubClears { get; set; }
    public byte ExtraClears { get; set; }
    public DungeonRecordFlag Flag { get; set; }

    public DungeonRecord(int dungeonId, bool accountWide = false) {
        DungeonId = dungeonId;
        AccountWide = accountWide;
        LifetimeRecord = -1;
        CurrentRecord = -1;
    }

    public static DungeonRecord Merge(int dungeonId, IEnumerable<DungeonRecord> records, long timestamp) {
        DungeonRecord[] values = records.ToArray();
        if (values.Length == 0) {
            return new DungeonRecord(dungeonId, accountWide: true);
        }

        long[] clearTimestamps = values.Select(record => record.ClearTimestamp).Where(value => value > 0).ToArray();
        return new DungeonRecord(dungeonId, accountWide: true) {
            UnionSubClears = ClampByte(values.Where(record => record.UnionSubCooldownTimestamp >= timestamp).Sum(record => record.UnionSubClears)),
            UnionClears = ClampByte(values.Where(record => record.UnionCooldownTimestamp >= timestamp).Sum(record => record.UnionClears)),
            UnionSubCooldownTimestamp = values.Max(record => record.UnionSubCooldownTimestamp),
            UnionCooldownTimestamp = values.Max(record => record.UnionCooldownTimestamp),
            CooldownTimestamp = values.Max(record => record.CooldownTimestamp),
            ClearTimestamp = clearTimestamps.Length == 0 ? 0 : clearTimestamps.Min(),
            TotalClears = (int) Math.Min(int.MaxValue, values.Sum(record => (long) record.TotalClears)),
            LifetimeRecord = values.Max(record => record.LifetimeRecord),
            CurrentRecord = values.Max(record => record.CurrentRecord),
            ExtraSubClears = ClampByte(values.Where(record => record.UnionSubCooldownTimestamp >= timestamp).Sum(record => record.ExtraSubClears)),
            ExtraClears = ClampByte(values.Where(record => record.UnionCooldownTimestamp >= timestamp).Sum(record => record.ExtraClears)),
            Flag = values.Aggregate(DungeonRecordFlag.None, (flag, record) => flag | record.Flag),
        };

        static byte ClampByte(int value) => (byte) Math.Min(byte.MaxValue, value);
    }

    public void WriteTo(IByteWriter writer) {
        writer.WriteInt(DungeonId);
        writer.WriteLong(UnionCooldownTimestamp);
        writer.WriteByte(UnionClears);
        writer.WriteByte(UnionSubClears);
        writer.WriteLong(UnionSubCooldownTimestamp);
        writer.WriteByte(ExtraSubClears);
        writer.WriteByte(ExtraClears);
        writer.WriteLong(ClearTimestamp);
        writer.WriteInt(TotalClears);
        writer.WriteShort(LifetimeRecord);
        writer.WriteLong(CooldownTimestamp);
        writer.WriteShort(CurrentRecord);
        writer.Write<DungeonRecordFlag>(Flag);
    }
}
