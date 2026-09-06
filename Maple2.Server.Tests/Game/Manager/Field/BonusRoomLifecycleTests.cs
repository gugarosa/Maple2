using System.Collections.Generic;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Manager.Field;

namespace Maple2.Server.Tests.Game.Manager.Field;

public class BonusRoomLifecycleTests {
    private static readonly RoomEntry FirstRoom = new(11, [80000001], 30000, "portal-a", 1, true);
    private static readonly RoomEntry SecondRoom = new(12, [80000004], 90000, "portal-b", 4, true);

    [Test]
    public void ResolvesRandomGroupFromEligibleMapIds() {
        RoomRandomTable table = CreateTable();

        bool resolved = FieldManager.TryResolveBonusRoomGroup(
            table, new HashSet<int> { 80000001, 80000004 },
            out RandomRoomEntry? group, out List<(RoomEntry Room, int Weight)> candidates, out string diagnostic);

        Assert.Multiple(() => {
            Assert.That(resolved, Is.True, diagnostic);
            Assert.That(group!.Id, Is.EqualTo(1));
            Assert.That(candidates, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void EmptyCandidatesReturnDiagnosticWithoutFallback() {
        bool resolved = FieldManager.TryResolveBonusRoomGroup(
            CreateTable(), new HashSet<int>(),
            out RandomRoomEntry? group, out List<(RoomEntry Room, int Weight)> candidates, out string diagnostic);

        Assert.Multiple(() => {
            Assert.That(resolved, Is.False);
            Assert.That(group, Is.Null);
            Assert.That(candidates, Is.Empty);
            Assert.That(diagnostic, Is.Not.Empty);
        });
    }

    [Test]
    public void WeightedSelectionReturnsTableRoomConfiguration() {
        var candidates = new List<(RoomEntry Room, int Weight)> {
            (FirstRoom, 1),
            (SecondRoom, 3),
        };

        Assert.That(FieldManager.TrySelectWeightedRoom(candidates, 1, out RoomEntry? selected), Is.True);
        Assert.Multiple(() => {
            Assert.That(selected, Is.SameAs(SecondRoom));
            Assert.That(selected!.DurationTick, Is.EqualTo(90000));
            Assert.That(selected.AssetName, Is.EqualTo("portal-b"));
            Assert.That(selected.MaxUserCount, Is.EqualTo(4));
            Assert.That(selected.AutoClose, Is.True);
        });
    }

    [Test]
    public void WeightedSelectionRejectsEmptyCandidates() {
        Assert.That(FieldManager.TrySelectWeightedRoom([], 0, out RoomEntry? selected), Is.False);
        Assert.That(selected, Is.Null);
    }

    [TestCase(1500, 1499, true)]
    [TestCase(1500, 1500, false)]
    [TestCase(10000, 9999, true)]
    public void ProbabilityUsesTenThousandScale(int probability, int roll, bool expected) {
        Assert.That(FieldManager.PassesBonusRoomProbability(probability, roll), Is.EqualTo(expected));
    }

    private static RoomRandomTable CreateTable() {
        return new RoomRandomTable(
            new Dictionary<int, RandomRoomEntry> {
                [1] = new RandomRoomEntry(1, 10000, new Dictionary<int, int> {
                    [11] = 100,
                    [12] = 100,
                }),
                [2] = new RandomRoomEntry(2, 10000, new Dictionary<int, int> {
                    [21] = 100,
                }),
            },
            new Dictionary<int, RoomEntry> {
                [11] = FirstRoom,
                [12] = SecondRoom,
                [21] = new RoomEntry(21, [90000001], 60000, "portal-c", 1, true),
            });
    }
}
