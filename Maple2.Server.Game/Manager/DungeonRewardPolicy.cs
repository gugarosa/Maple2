using Maple2.Model.Enum;

namespace Maple2.Server.Game.Manager;

public static class DungeonRewardPolicy {
    public static bool CanReceive(
        DungeonCooldownType cooldownType,
        int rewardCount,
        int subRewardCount,
        int currentCount,
        int currentSubCount) {
        if (rewardCount <= 0 || currentCount >= rewardCount) {
            return false;
        }

        return cooldownType switch {
            DungeonCooldownType.nextDay => true,
            DungeonCooldownType.dayOfWeeks => subRewardCount <= 0 || currentSubCount < subRewardCount,
            _ => false,
        };
    }
}
