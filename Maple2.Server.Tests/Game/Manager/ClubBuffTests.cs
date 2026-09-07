using System.Collections.Generic;
using System.Xml;
using Google.Protobuf;
using Maple2.File.Ingest.Mapper;
using Maple2.Model.Enum;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Manager;

namespace Maple2.Server.Tests.Game.Manager;

public class ClubBuffTests {
    [Test]
    public void ParsesClientClubBuffMappings() {
        var document = new XmlDocument();
        document.LoadXml("""
            <ms2>
              <clubBuff id="7" additionalEffectId="200&#160;" additionalEffectLevel="3" />
            </ms2>
            """);

        ClubBuffTable table = TableMapper.ParseClubBuffTable(document);

        Assert.That(table.Entries[7], Is.EqualTo(new ClubBuffTable.Entry(200, 3)));
    }

    [Test]
    public void ValidatesSelectionIdAndSourceLevel() {
        var metadata = new Dictionary<int, ClubBuffTable.Entry> {
            [7] = new(200, 3),
        };

        var table = new ClubBuffTable(metadata);
        Assert.Multiple(() => {
            Assert.That(table.IsValidSelection(7, 3), Is.True);
            Assert.That(table.IsValidSelection(7, 2), Is.False);
            Assert.That(table.IsValidSelection(200, 3), Is.False);
        });
    }

    [Test]
    public void SelectsMappedEffectOnlyWhenAnotherMemberIsPresent() {
        var metadata = new Dictionary<int, ClubBuffTable.Entry> {
            [7] = new(200, 3),
        };
        ClubBuffPolicy.ClubSelection[] clubs = [
            new(ClubState.Established, 7, new long[] { 1, 2 }),
        ];

        Dictionary<int, short> alone = ClubBuffPolicy.SelectEffects(clubs, 1, new HashSet<long> { 1 }, metadata);
        Dictionary<int, short> together = ClubBuffPolicy.SelectEffects(clubs, 1, new HashSet<long> { 1, 2 }, metadata);

        Assert.Multiple(() => {
            Assert.That(alone, Is.Empty);
            Assert.That(together, Has.Count.EqualTo(1));
            Assert.That(together[200], Is.EqualTo(3));
        });
    }

    [Test]
    public void DoesNotActivateBeforeThePlayerHasEnteredTheField() {
        var metadata = new Dictionary<int, ClubBuffTable.Entry> {
            [7] = new(200, 3),
        };
        ClubBuffPolicy.ClubSelection[] clubs = [
            new(ClubState.Established, 7, new long[] { 1, 2 }),
        ];

        Assert.That(ClubBuffPolicy.SelectEffects(clubs, 1, new HashSet<long> { 2 }, metadata), Is.Empty);
    }

    [Test]
    public void StagedClubsAndFormerMembersDoNotReceiveEffects() {
        var metadata = new Dictionary<int, ClubBuffTable.Entry> {
            [7] = new(200, 3),
        };
        ClubBuffPolicy.ClubSelection[] staged = [
            new(ClubState.Staged, 7, new long[] { 1, 2 }),
        ];
        ClubBuffPolicy.ClubSelection[] left = [
            new(ClubState.Established, 7, new long[] { 2, 3 }),
        ];
        var present = new HashSet<long> { 1, 2, 3 };

        Assert.That(ClubBuffPolicy.SelectEffects(staged, 1, present, metadata), Is.Empty);
        Assert.That(ClubBuffPolicy.SelectEffects(left, 1, present, metadata), Is.Empty);
    }

    [Test]
    public void ReplacesChangedEffectsAndRevokesIneligibleEffects() {
        var active = new Dictionary<int, short> {
            [200] = 1,
            [300] = 1,
        };
        var desired = new Dictionary<int, short> {
            [200] = 3,
        };

        ClubBuffPolicy.Changes changes = ClubBuffPolicy.GetChanges(active, desired);

        Assert.Multiple(() => {
            Assert.That(changes.Remove, Is.EquivalentTo(new[] { 200, 300 }));
            Assert.That(changes.Add, Is.EqualTo(new[] { new ClubBuffPolicy.Effect(200, 3) }));
        });
    }

    [Test]
    public void ClubInfoTransfersSelectedBuff() {
        var info = new ClubInfo {
            Id = 10,
            BuffId = 7,
        };

        ClubInfo restored = ClubInfo.Parser.ParseFrom(info.ToByteArray());

        Assert.That(restored.BuffId, Is.EqualTo(7));
    }
}
