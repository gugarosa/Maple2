using System;
using System.IO;
using System.Linq;
using Maple2.File.Ingest.Mapper;
using Maple2.File.IO;
using Maple2.File.Parser.Tools;
using Maple2.Model.Metadata;
using Maple2.Server.Game.Model.ActorStateComponent;
using static Maple2.Server.Game.Model.ActorStateComponent.AiState;

namespace Maple2.Server.Tests.File.Ingest;

[Explicit("Requires Xml.m2d and Server.m2d under MS2_DATA_FOLDER; reads assets without changing them.")]
public class AiLifecycleArchiveTests {
    [Test]
    public void IngestedBattleEndReachesCombatExitDispatch() {
        string? folder = Environment.GetEnvironmentVariable("MS2_DATA_FOLDER");
        if (string.IsNullOrWhiteSpace(folder)) {
            Assert.Ignore("Set MS2_DATA_FOLDER to run the read-only AI lifecycle regression.");
        }

        using var client = new M2dReader(Path.Combine(folder!, "Xml.m2d"));
        using var server = new M2dReader(Path.Combine(folder, "Server.m2d"));
        Filter.Load(client, "NA", "Live");
        var mapper = new AiMapper(server);
        mapper.Process();
        AiMetadata metadata = mapper.Results.Single(entry =>
            entry.Name.EndsWith("AI_BarkhantRedSummon.xml", StringComparison.OrdinalIgnoreCase));

        Assert.That(metadata.BattleEnd, Is.Not.Empty);
        Assert.That(metadata.BattleEnd.OfType<AiMetadata.SetMasterValueNode>()
            .Any(node => node.Key == "CheckBarkhantSummonMany"), Is.True);
        var transition = AiState.GetTreeTransition(DecisionTreeType.Battle, false,
            metadata.Battle.Length > 0, metadata.BattleEnd.Length > 0, false);
        Assert.That(transition, Is.EqualTo((DecisionTreeType.BattleEnd, true, true)));
    }
}
