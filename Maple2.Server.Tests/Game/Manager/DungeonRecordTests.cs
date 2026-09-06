using Maple2.Model.Enum;
using Maple2.Model.Game.Dungeon;

namespace Maple2.Server.Tests.Game.Manager;

public class DungeonRecordTests {
    [Test]
    public void MergePreservesAccountProgressAcrossCharacters() {
        const long now = 1_000;
        var first = new DungeonRecord(29001001) {
            UnionSubClears = 1,
            UnionClears = 2,
            UnionSubCooldownTimestamp = now + 10,
            UnionCooldownTimestamp = now + 20,
            ClearTimestamp = 100,
            TotalClears = 4,
            ExtraSubClears = 1,
            ExtraClears = 2,
            Flag = DungeonRecordFlag.Veteran,
        };
        var second = new DungeonRecord(29001001) {
            UnionSubClears = 2,
            UnionClears = 3,
            UnionSubCooldownTimestamp = now + 30,
            UnionCooldownTimestamp = now + 40,
            ClearTimestamp = 200,
            TotalClears = 5,
            ExtraSubClears = 2,
            ExtraClears = 3,
            Flag = DungeonRecordFlag.Favorite,
        };

        DungeonRecord merged = DungeonRecord.Merge(29001001, [first, second], now);

        Assert.Multiple(() => {
            Assert.That(merged.AccountWide, Is.True);
            Assert.That(merged.UnionSubClears, Is.EqualTo(3));
            Assert.That(merged.UnionClears, Is.EqualTo(5));
            Assert.That(merged.TotalClears, Is.EqualTo(9));
            Assert.That(merged.ClearTimestamp, Is.EqualTo(100));
            Assert.That(merged.Flag, Is.EqualTo(DungeonRecordFlag.Veteran | DungeonRecordFlag.Favorite));
        });
    }

    [Test]
    public void MergeDoesNotCarryExpiredRewardCounts() {
        const long now = 1_000;
        var expired = new DungeonRecord(29001001) {
            UnionSubClears = 5,
            UnionClears = 7,
            UnionSubCooldownTimestamp = now - 1,
            UnionCooldownTimestamp = now - 1,
            ExtraSubClears = 2,
            ExtraClears = 3,
        };

        DungeonRecord merged = DungeonRecord.Merge(29001001, [expired], now);

        Assert.Multiple(() => {
            Assert.That(merged.UnionSubClears, Is.Zero);
            Assert.That(merged.UnionClears, Is.Zero);
            Assert.That(merged.ExtraSubClears, Is.Zero);
            Assert.That(merged.ExtraClears, Is.Zero);
        });
    }
}
