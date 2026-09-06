using Maple2.Model.Metadata;

namespace Maple2.Server.Game.Manager;

public static class DungeonRoundRewardSelector {
    public static DungeonRoundTable.Round? GetReward(
        DungeonRoundTable.Entry metadata,
        int completedRound,
        int claimedRound) {
        if (completedRound <= claimedRound || completedRound <= 0 || completedRound > metadata.Rounds.Length) {
            return null;
        }

        DungeonRoundTable.Round reward = metadata.Rounds[completedRound - 1];
        return reward.Number == completedRound ? reward : null;
    }

    public static bool MeetsGearScore(DungeonRoundTable.Entry metadata, int round, long gearScore) {
        if (round <= 0 || round > metadata.Rounds.Length) {
            return false;
        }

        DungeonRoundTable.Round requirement = metadata.Rounds[round - 1];
        return requirement.Number == round && gearScore >= requirement.GearScore;
    }
}
