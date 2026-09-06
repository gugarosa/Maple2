using System.Collections.Generic;
using Maple2.Model.Enum;
using Maple2.Server.Game.Model;

namespace Maple2.Server.Tests.Game;

public class StatsTests {
    [Test]
    public void ConstructorAndResetUseTheSameMetadataPath() {
        var initial = new Dictionary<BasicAttribute, long> {
            [BasicAttribute.Health] = 500,
            [BasicAttribute.Strength] = 30,
            [BasicAttribute.Defense] = 123,
        };
        var stats = new Stats(initial, JobCode.Knight);
        Assert.That(stats[BasicAttribute.Health].Base, Is.EqualTo(500));
        Assert.That(stats[BasicAttribute.Defense].Base, Is.EqualTo(123));

        stats.Reset(new Dictionary<BasicAttribute, long> {
            [BasicAttribute.Health] = 1000,
            [BasicAttribute.Strength] = 45,
            [BasicAttribute.Defense] = 321,
        }, JobCode.Knight);

        Assert.That(stats[BasicAttribute.Health].Base, Is.EqualTo(1000));
        Assert.That(stats[BasicAttribute.Strength].Base, Is.EqualTo(45));
        Assert.That(stats[BasicAttribute.Defense].Base, Is.EqualTo(321));
    }
}
