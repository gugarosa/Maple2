using Maple2.Model.Metadata;
using Maple2.Server.Game.Manager;

namespace Maple2.Server.Tests.Game.Manager;

public class DungeonRoundRewardSelectorTests {
    private static readonly DungeonRoundTable.Entry Metadata = new(90003, [
        new DungeonRoundTable.Round(1, 10000009, 47400),
        new DungeonRoundTable.Round(2, 10000010, 47400),
        new DungeonRoundTable.Round(3, 10000011, 47400),
    ]);

    [Test]
    public void SelectsExactHighestNewRoundReward() {
        DungeonRoundTable.Round? reward = DungeonRoundRewardSelector.GetReward(Metadata, completedRound: 3, claimedRound: 1);

        Assert.Multiple(() => {
            Assert.That(reward, Is.Not.Null);
            Assert.That(reward!.Number, Is.EqualTo(3));
            Assert.That(reward.RewardId, Is.EqualTo(10000011));
        });
    }

    [TestCase(1, 1)]
    [TestCase(1, 2)]
    [TestCase(0, 0)]
    public void DoesNotRewardPreviouslyClaimedOrInvalidRound(int completedRound, int claimedRound) {
        Assert.That(DungeonRoundRewardSelector.GetReward(Metadata, completedRound, claimedRound), Is.Null);
    }

    [Test]
    public void GearScoreRequirementUsesRequestedRound() {
        Assert.Multiple(() => {
            Assert.That(DungeonRoundRewardSelector.MeetsGearScore(Metadata, 2, 47399), Is.False);
            Assert.That(DungeonRoundRewardSelector.MeetsGearScore(Metadata, 2, 47400), Is.True);
            Assert.That(DungeonRoundRewardSelector.MeetsGearScore(Metadata, 4, 999999), Is.False);
        });
    }
}
