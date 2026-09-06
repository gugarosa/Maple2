using Maple2.Model.Enum;
using Maple2.Model.Game.Dungeon;
using Maple2.Model.Metadata;

namespace Maple2.Server.Game.Manager;

public static class DungeonRankRewardSelector {
    public static IReadOnlyList<DungeonRankRewardTable.Entry.Item> GetUnclaimedRewards(
        DungeonRankRewardTable.Entry metadata,
        DungeonMissionRank achievedRank,
        DungeonRankReward? claimed,
        long timestamp) {
        int claimedRank = IsCurrentWeek(claimed, timestamp) ? claimed!.RankClaimed : (int) DungeonMissionRank.F;
        int rank = (int) achievedRank;
        if (rank <= claimedRank) {
            return [];
        }

        return metadata.Items
            .Where(item => item.Rank > claimedRank && item.Rank <= rank)
            .OrderBy(item => item.Rank)
            .ToArray();
    }

    public static bool IsCurrentWeek(DungeonRankReward? reward, long timestamp) {
        return reward != null && reward.UpdatedTimestamp > 0 &&
               GetWeekStart(reward.UpdatedTimestamp) == GetWeekStart(timestamp);
    }

    public static bool RemoveExpired(IDictionary<int, DungeonRankReward> rewards, long timestamp) {
        int[] expired = rewards.Where(entry => !IsCurrentWeek(entry.Value, timestamp))
            .Select(entry => entry.Key)
            .ToArray();
        foreach (int id in expired) {
            rewards.Remove(id);
        }
        return expired.Length > 0;
    }

    public static long GetWeekStartTimestamp(long timestamp) {
        return new DateTimeOffset(GetWeekStart(timestamp)).ToUnixTimeSeconds();
    }

    private static DateTime GetWeekStart(long timestamp) {
        DateTime date = DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime.Date;
        int daysSinceFriday = ((int) date.DayOfWeek - (int) DayOfWeek.Friday + 7) % 7;
        return date.AddDays(-daysSinceFriday);
    }
}
