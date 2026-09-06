using System;
using Maple2.Server.Game.Manager;

namespace Maple2.Server.Tests.Game.Manager;

public class ExperienceManagerTests {
    [TestCase(100L, 100L, 5000, 150L, 50L)]
    [TestCase(100L, 0L, 5000, 100L, 0L)]
    [TestCase(100L, 20L, 5000, 120L, 0L)]
    [TestCase(100L, 100L, 0, 100L, 100L)]
    [TestCase(150L, 200L, 5000, 225L, 125L)]
    [TestCase(0L, 100L, 5000, 0L, 100L)]
    public void CreditsBaseAndRestedBonusFromTheSameScaledAmount(
        long amount, long rest, int rate, long expectedTotal, long expectedRest) {
        (long total, long remaining) = ExperienceManager.CalculateRestedGain(amount, rest, rate);

        Assert.That(total, Is.EqualTo(expectedTotal));
        Assert.That(remaining, Is.EqualTo(expectedRest));
        Assert.That(total - amount, Is.EqualTo(rest - remaining));
    }

    [Test]
    public void RejectsOverflowRatherThanWrappingExperience() {
        Assert.Throws<OverflowException>(() => ExperienceManager.CalculateRestedGain(long.MaxValue, 1, 10000));
    }
}
