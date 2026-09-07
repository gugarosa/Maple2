using System;
using System.IO;
using System.Linq;
using Maple2.File.Ingest.Mapper;
using Maple2.File.IO;
using Maple2.File.Parser;
using Maple2.File.Parser.Tools;
using Maple2.Model.Enum;
using Maple2.Model.Metadata;

namespace Maple2.Server.Tests.File.Ingest;

[Explicit("Requires Xml.m2d under MS2_DATA_FOLDER; reads assets without changing them.")]
[NonParallelizable]
public class QuestLifecycleArchiveTests {
    [Test]
    public void IngestionPreservesQuestTimingAndAcceptanceContracts() {
        string? folder = Environment.GetEnvironmentVariable("MS2_DATA_FOLDER");
        if (string.IsNullOrWhiteSpace(folder)) {
            Assert.Ignore("Set MS2_DATA_FOLDER to run the read-only quest lifecycle regression.");
        }

        using var client = new M2dReader(Path.Combine(folder!, "Xml.m2d"));
        Filter.Load(client, "NA", "Live");
        var mapper = new QuestMapper(client, "en");
        mapper.Process();
        var quests = mapper.Results.ToDictionary(quest => quest.Id);
        var source = new QuestParser(client, "en").Parse().ToDictionary(entry => entry.Id);
        Assert.That(quests.Count, Is.EqualTo(source.Count));
        foreach ((int id, QuestMetadata quest) in quests) {
            Assert.That(quest.Basic.Repeatable, Is.EqualTo(source[id].Data.basic.repeatable), $"Quest {id}");
            Assert.That(quest.Basic.UsePeriod, Is.EqualTo(source[id].Data.basic.usePeriod), $"Quest {id}");
            Assert.That(quest.Basic.Alliance, Is.EqualTo(source[id].Data.basic.alliance.ToString()), $"Quest {id}");
            Assert.That(quest.Basic.AllianceRank, Is.EqualTo(source[id].Data.basic.rank.ToString()), $"Quest {id}");
            Assert.That(quest.Require.Alliance, Is.EqualTo(source[id].Data.require.alliance), $"Quest {id}");
            Assert.That(quest.Require.FameGrade, Is.EqualTo(source[id].Data.require.fameGrade), $"Quest {id}");
            Assert.That(quest.CompleteReward.UseMainFamePoint, Is.EqualTo(source[id].Data.completeReward.useMainFamePoint), $"Quest {id}");
            Assert.That(quest.CompleteReward.FameLog, Is.EqualTo(source[id].Data.completeReward.fameLog), $"Quest {id}");
        }

        for (int id = 93000123; id <= 93000127; id++) {
            QuestMetadata quest = quests[id];
            Assert.That(quest.Basic.Type, Is.EqualTo(QuestType.AllianceQuest));
            Assert.That(quest.Basic.Repeatable, Is.EqualTo(2));
            Assert.That(quest.Basic.UsePeriod, Is.EqualTo("5"));
            Assert.That(quest.AcceptReward.EssentialItem, Is.Not.Empty);
            Assert.That(quest.SummonPortal?.MapId, Is.EqualTo(2000043));
        }

        int[] furnishingQuests = [90000660, 90000670, 90000680, 90000690, 90000700, 90000760, 90000770];
        Assert.That(furnishingQuests.Sum(id => quests[id].AcceptReward.EssentialItem.Count), Is.EqualTo(8));
        Assert.That(quests[90000660].AcceptReward.EssentialItem.Single().Id, Is.EqualTo(50200094));
        Assert.That(quests[93000158].CompleteReward.UseMainFamePoint, Is.EqualTo(2));
        Assert.That(quests[93000158].CompleteReward.FameLog, Is.EqualTo(5000));
        Assert.That(quests[91000140].Require.Alliance, Is.EqualTo("MapleUnion_KritiasExped"));
        Assert.That(quests[91000140].Require.FameGrade, Is.EqualTo(2));

        Assert.That(quests.Values.Count(quest => quest.CompleteReward.GuildExp > 0 || quest.CompleteReward.GuildFund > 0),
            Is.EqualTo(60));
        foreach (int id in new[] { 73000001, 73000002, 73000003, 73000004 }) {
            Assert.That(quests[id].CompleteReward.GuildExp, Is.EqualTo(120));
            Assert.That(quests[id].CompleteReward.GuildFund, Is.EqualTo(20000));
        }
    }
}
