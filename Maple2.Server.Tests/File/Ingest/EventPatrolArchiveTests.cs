using System;
using System.IO;
using System.Linq;
using Maple2.File.Flat;
using Maple2.File.Flat.maplestory2library;
using Maple2.File.Ingest.Mapper;
using Maple2.File.IO;
using Maple2.File.Parser.Flat;
using Maple2.File.Parser.MapXBlock;
using Maple2.Model.Metadata;

namespace Maple2.Server.Tests.File.Ingest;

[Explicit("Requires Resource/Exported.m2d under MS2_DATA_FOLDER; reads assets without changing them.")]
public class EventPatrolArchiveTests {
    [Test]
    public void HorusCarrierSpawnsRetainResolvableSourcePatrols() {
        string? folder = Environment.GetEnvironmentVariable("MS2_DATA_FOLDER");
        if (string.IsNullOrWhiteSpace(folder)) {
            Assert.Ignore("Set MS2_DATA_FOLDER to run the read-only event patrol regression.");
        }

        using var reader = new M2dReader(Path.Combine(folder!, "Resource", "Exported.m2d"));
        var parser = new XBlockParser(reader, new FlatTypeIndex(reader));
        IMapEntity[] entities = [];
        parser.ParseMap("02000177_bf", values => entities = values.ToArray());
        var patrolIds = entities.OfType<IMS2PatrolData>()
            .Select(patrol => patrol.EntityId.Replace("-", ""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (int spawnId in new[] { 999, 998, 997 }) {
            IEventSpawnPointNPC source = entities.OfType<IEventSpawnPointNPC>()
                .Single(spawn => spawn.SpawnPointID == spawnId);
            SpawnPointNPC mapped = MapEntityMapper.CreateNpcSpawn(source,
                [new SpawnPointNPCListEntry(11001808, 1)]);

            Assert.That(mapped.PatrolData, Is.EqualTo(source.PatrolData.Replace("-", "")));
            Assert.That(patrolIds.Contains(mapped.PatrolData!), Is.True, $"Horus carrier spawn {spawnId}");
        }
    }
}
