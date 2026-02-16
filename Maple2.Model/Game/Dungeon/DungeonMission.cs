using Maple2.Model.Metadata;
using Maple2.PacketLib.Tools;
using Maple2.Tools;

namespace Maple2.Model.Game.Dungeon;

public class DungeonMission : IByteSerializable {
    public required DungeonMissionMetadata Metadata;
    public int Id => Metadata.Id;
    public short Score { get; private set; }
    public short Counter { get; private set; }

    public void Initialize() {
        if (Metadata.IsPenaltyType) {
            Score = Metadata.MaxScore;
        }
    }

    public bool Update(int counter = 1) {
        if (Counter >= Metadata.ApplyCount) {
            return false;
        }

        Counter = (short) Math.Min(Counter + counter, Metadata.ApplyCount);
        if (Metadata.IsPenaltyType) {
            float percentage = (float) Counter / Metadata.ApplyCount;
            Score = (short) (Metadata.MaxScore - (short) (percentage * Metadata.MaxScore));
        } else {
            float percentage = (float) Counter / Metadata.ApplyCount;
            Score = (short) (percentage * Metadata.MaxScore);
        }
        return true;
    }

    public void Complete() {
        Counter = Metadata.ApplyCount;
        Score = Metadata.MaxScore;
    }

    public void WriteTo(IByteWriter writer) {
        writer.WriteInt(Id);
        writer.WriteShort(Score);
        writer.WriteShort(Counter);
    }
}
