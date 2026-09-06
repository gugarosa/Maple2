using System;
using System.Collections.Generic;
using System.Linq;
using Maple2.Model.Enum;
using Maple2.Model.Game.Dungeon;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Manager;

namespace Maple2.Server.Tests.Game.Manager;

public class DungeonRankRewardSelectorTests {
    private static readonly DungeonRankRewardTable.Entry Metadata = new(10005, [
        new DungeonRankRewardTable.Entry.Item(1, 101, 201),
        new DungeonRankRewardTable.Entry.Item(2, 102, 202),
        new DungeonRankRewardTable.Entry.Item(3, 103, 203),
        new DungeonRankRewardTable.Entry.Item(4, 104, 204),
        new DungeonRankRewardTable.Entry.Item(5, 105, 205),
    ]);

    [TestCase(DungeonMissionRank.None)]
    [TestCase(DungeonMissionRank.F)]
    public void RankBelowFirstConfiguredRewardIsNotEligible(DungeonMissionRank rank) {
        IReadOnlyList<DungeonRankRewardTable.Entry.Item> rewards =
            DungeonRankRewardSelector.GetUnclaimedRewards(Metadata, rank, null, Timestamp(2026, 9, 6));

        Assert.That(rewards, Is.Empty);
    }

    [Test]
    public void ReachingRankIncludesEveryLowerReward() {
        long timestamp = Timestamp(2026, 9, 6);

        IReadOnlyList<DungeonRankRewardTable.Entry.Item> rewards =
            DungeonRankRewardSelector.GetUnclaimedRewards(Metadata, DungeonMissionRank.A, null, timestamp);

        Assert.That(rewards.Select(reward => reward.Rank), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void CurrentWeekClaimOnlyReturnsNewHigherRanks() {
        long timestamp = Timestamp(2026, 9, 6);
        var claimed = new DungeonRankReward(Metadata.Id) {
            RankClaimed = (int) DungeonMissionRank.C,
            UpdatedTimestamp = Timestamp(2026, 9, 5),
        };

        IReadOnlyList<DungeonRankRewardTable.Entry.Item> rewards =
            DungeonRankRewardSelector.GetUnclaimedRewards(Metadata, DungeonMissionRank.A, claimed, timestamp);

        Assert.That(rewards.Select(reward => reward.Rank), Is.EqualTo(new[] { 2, 3 }));
    }

    [Test]
    public void RepeatedClearDoesNotReturnDuplicateMailRewards() {
        long timestamp = Timestamp(2026, 9, 6);
        var claimed = new DungeonRankReward(Metadata.Id) {
            RankClaimed = (int) DungeonMissionRank.S,
            UpdatedTimestamp = Timestamp(2026, 9, 5),
        };

        IReadOnlyList<DungeonRankRewardTable.Entry.Item> rewards =
            DungeonRankRewardSelector.GetUnclaimedRewards(Metadata, DungeonMissionRank.S, claimed, timestamp);

        Assert.That(rewards, Is.Empty);
    }

    [Test]
    public void PreviousWeekClaimDoesNotBlockNewWeekRewards() {
        long timestamp = Timestamp(2026, 9, 6);
        var claimed = new DungeonRankReward(Metadata.Id) {
            RankClaimed = (int) DungeonMissionRank.SPlus,
            UpdatedTimestamp = Timestamp(2026, 9, 3),
        };

        IReadOnlyList<DungeonRankRewardTable.Entry.Item> rewards =
            DungeonRankRewardSelector.GetUnclaimedRewards(Metadata, DungeonMissionRank.B, claimed, timestamp);

        Assert.That(rewards.Select(reward => reward.Rank), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void DelayedOrRepeatedResetPreservesCurrentWeekClaims() {
        long timestamp = Timestamp(2026, 9, 4);
        var current = new DungeonRankReward(Metadata.Id) {
            RankClaimed = (int) DungeonMissionRank.S,
            UpdatedTimestamp = timestamp - 3600,
        };
        var rewards = new Dictionary<int, DungeonRankReward> {
            [Metadata.Id] = current,
            [10006] = new(10006) {
                RankClaimed = (int) DungeonMissionRank.S,
                UpdatedTimestamp = Timestamp(2026, 9, 3),
            },
        };

        Assert.That(DungeonRankRewardSelector.RemoveExpired(rewards, timestamp), Is.True);
        Assert.That(rewards.Keys, Is.EqualTo(new[] { Metadata.Id }));
        Assert.That(rewards[Metadata.Id], Is.SameAs(current));
        Assert.That(DungeonRankRewardSelector.RemoveExpired(rewards, timestamp + 3600), Is.False);
        Assert.That(DungeonRankRewardSelector.GetUnclaimedRewards(
            Metadata, DungeonMissionRank.S, rewards[Metadata.Id], timestamp), Is.Empty);
    }

    private static long Timestamp(int year, int month, int day) {
        return new DateTimeOffset(new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds();
    }
}
