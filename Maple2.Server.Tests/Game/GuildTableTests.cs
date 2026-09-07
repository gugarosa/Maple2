using System;
using System.Collections.Generic;
using System.Linq;
using Maple2.Model.Enum;
using Maple2.Model.Metadata;

namespace Maple2.Server.Tests.Game;

public class GuildTableTests {
    [Test]
    public void PropertySelectionUsesHighestUnlockedThreshold() {
        GuildTable table = Table(
            Property(1, 0, 100),
            Property(2, 100, 500),
            Property(3, 200, 1000));

        Assert.That(table.GetProperty(0).Level, Is.EqualTo(1));
        Assert.That(table.GetProperty(150).Level, Is.EqualTo(2));
        Assert.That(table.GetProperty(250).Level, Is.EqualTo(3));
    }

    [Test]
    public void ProgressCrossingThresholdUsesNewLevelFundCap() {
        GuildTable table = Table(
            Property(1, 0, 100),
            Property(2, 100, 500));

        (int experience, int funds) = table.AddProgress(99, 490, 1, 100);

        Assert.That(experience, Is.EqualTo(100));
        Assert.That(funds, Is.EqualTo(500));
    }

    [TestCase(-1, 0)]
    [TestCase(0, -1)]
    public void NegativeAwardsAreRejected(int experience, int funds) {
        Assert.Throws<ArgumentOutOfRangeException>(() => Table(Property(1, 0, 100))
            .AddProgress(0, 0, experience, funds));
    }

    private static GuildTable Table(params GuildTable.Property[] properties) {
        return new GuildTable(
            new Dictionary<int, IReadOnlyDictionary<short, GuildTable.Buff>>(),
            new Dictionary<int, IReadOnlyDictionary<int, GuildTable.House>>(),
            new Dictionary<GuildNpcType, IReadOnlyDictionary<short, GuildTable.Npc>>(),
            properties.ToDictionary(property => property.Level));
    }

    private static GuildTable.Property Property(short level, int experience, long fundMax) {
        return new GuildTable.Property(
            level,
            experience,
            60,
            fundMax,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0);
    }
}
