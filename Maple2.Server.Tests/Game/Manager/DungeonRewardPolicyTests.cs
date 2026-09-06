using Maple2.Model.Enum;
using Maple2.Server.Game.Manager;

namespace Maple2.Server.Tests.Game.Manager;

public class DungeonRewardPolicyTests {
    [Test]
    public void NextDayUsesRewardCountWithoutSubRewardCount() {
        Assert.That(DungeonRewardPolicy.CanReceive(DungeonCooldownType.nextDay, 1, 0, 0, 0), Is.True);
        Assert.That(DungeonRewardPolicy.CanReceive(DungeonCooldownType.nextDay, 1, 0, 1, 0), Is.False);
    }

    [Test]
    public void WeeklyRewardHonorsWeeklyAndDailyCaps() {
        Assert.Multiple(() => {
            Assert.That(DungeonRewardPolicy.CanReceive(DungeonCooldownType.dayOfWeeks, 30, 15, 29, 14), Is.True);
            Assert.That(DungeonRewardPolicy.CanReceive(DungeonCooldownType.dayOfWeeks, 30, 15, 30, 14), Is.False);
            Assert.That(DungeonRewardPolicy.CanReceive(DungeonCooldownType.dayOfWeeks, 30, 15, 29, 15), Is.False);
        });
    }
}
