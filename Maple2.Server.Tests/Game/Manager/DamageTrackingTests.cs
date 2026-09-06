using Maple2.Model.Enum;
using Maple2.Server.Game.Manager;

namespace Maple2.Server.Tests.Game.Manager;

public class DamageTrackingTests {
    [Test]
    public void SummarizesObservedHitsWithoutSimulatingDamage() {
        var tracking = new DamageTracking(10, 20, 1000);
        tracking.Record(10, 20, DamageType.Normal, 100);
        tracking.Record(10, 20, DamageType.Critical, 200);
        tracking.Record(10, 20, DamageType.Miss, 0);
        tracking.Record(10, 20, DamageType.Block, 0);

        DamageSummary summary = tracking.Snapshot(3000);
        Assert.Multiple(() => {
            Assert.That(summary.Total, Is.EqualTo(300));
            Assert.That(summary.Minimum, Is.EqualTo(100));
            Assert.That(summary.Maximum, Is.EqualTo(200));
            Assert.That(summary.Hits, Is.EqualTo(2));
            Assert.That(summary.CriticalHits, Is.EqualTo(1));
            Assert.That(summary.Misses, Is.EqualTo(1));
            Assert.That(summary.Blocks, Is.EqualTo(1));
            Assert.That(summary.Dps, Is.EqualTo(150));
        });
    }

    [Test]
    public void IgnoresOtherTargetsAndRooms() {
        var tracking = new DamageTracking(10, 20, 1000);
        tracking.Record(11, 20, DamageType.Normal, 100);
        tracking.Record(10, 21, DamageType.Normal, 100);
        tracking.Record(10, 20, DamageType.Normal, -100);

        Assert.That(tracking.Snapshot(1000).Total, Is.Zero);
        Assert.That(tracking.Snapshot(1000).Hits, Is.Zero);
        Assert.That(tracking.Snapshot(1000).Dps, Is.Zero);
    }

    [Test]
    public void StoppedMeasurementKeepsItsWindowAndIgnoresLaterHits() {
        var tracking = new DamageTracking(10, 20, 1000);
        tracking.Record(10, 20, DamageType.Normal, 100);
        tracking.Stop(2000);
        tracking.Record(10, 20, DamageType.Normal, 200);
        tracking.Stop(5000);

        DamageSummary summary = tracking.Snapshot(9000);
        Assert.That(summary.Total, Is.EqualTo(100));
        Assert.That(summary.Seconds, Is.EqualTo(1));
        Assert.That(summary.Dps, Is.EqualTo(100));
        Assert.That(summary.Running, Is.False);
    }
}
