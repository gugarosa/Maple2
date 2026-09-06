using Maple2.Model.Enum;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Util;

namespace Maple2.Server.Tests.Game.Util;

public class ConditionUtilTests {
    [Test]
    public void DungeonClearMatchesDungeonId() {
        const int dungeonId = 25004001;
        var condition = new ConditionMetadata(
            ConditionType.dungeon_clear,
            1,
            new ConditionMetadata.Parameters(Integers: [dungeonId]),
            null);

        Assert.That(condition.Check(null!, codeLong: dungeonId), Is.True);
        Assert.That(condition.Check(null!, codeLong: dungeonId + 1), Is.False);
    }
}
