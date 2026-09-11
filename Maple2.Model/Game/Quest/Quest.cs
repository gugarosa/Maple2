using System.Globalization;
using Maple2.Model.Enum;
using Maple2.Model.Metadata;
using Maple2.PacketLib.Tools;
using Maple2.Tools;

namespace Maple2.Model.Game;

public class Quest : IByteSerializable {
    public int Id => Metadata.Id;
    public readonly QuestMetadata Metadata;

    public QuestState State;
    public int CompletionCount;
    public long StartTime;
    public long EndTime;
    public bool Track;
    public SortedDictionary<int, Condition> Conditions;

    public Quest(QuestMetadata metadata) {
        Metadata = metadata;
        Conditions = new SortedDictionary<int, Condition>();
    }

    public bool IsExpired(long now) {
        if (State != QuestState.Started || StartTime <= 0 || now < StartTime) {
            return false;
        }

        if (long.TryParse(Metadata.Basic.UsePeriod, NumberStyles.None, CultureInfo.InvariantCulture, out long minutes) &&
            minutes > 0 && minutes <= (long.MaxValue - StartTime) / 60) {
            return now >= StartTime + minutes * 60;
        }

        if (Metadata.Basic.Repeatable == 3 && Metadata.Basic.UsePeriod == "fri" &&
            StartTime <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() - 7 * 86400) {
            DateTime start = DateTimeOffset.FromUnixTimeSeconds(StartTime).UtcDateTime;
            int days = ((int) DayOfWeek.Friday - (int) start.DayOfWeek + 7) % 7;
            DateTime reset = start.Date.AddDays(days == 0 ? 7 : days);
            return now >= new DateTimeOffset(reset).ToUnixTimeSeconds();
        }

        return false;
    }

    public void WriteTo(IByteWriter writer) {
        writer.WriteInt(Id);
        writer.Write<QuestState>(State);
        writer.WriteInt(CompletionCount);
        writer.WriteLong(StartTime);
        writer.WriteLong(EndTime);
        writer.WriteBool(Track);

        writer.WriteInt(Conditions.Count);
        foreach (Condition condition in Conditions.Values) {
            writer.WriteInt(condition.Counter);
        }
    }

    public class Condition {
        public readonly ConditionMetadata Metadata;
        public int Counter;

        public Condition(ConditionMetadata metadata) {
            Metadata = metadata;
        }
    }
}
