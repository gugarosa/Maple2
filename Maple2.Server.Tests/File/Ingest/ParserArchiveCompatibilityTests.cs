using System;
using System.IO;
using System.Linq;
using Maple2.File.Ingest.Mapper;
using Maple2.File.IO;
using Maple2.File.Parser;
using Maple2.File.Parser.Tools;
using Maple2.Model.Metadata;

namespace Maple2.Server.Tests.File.Ingest;

[Explicit("Requires existing Xml.m2d and Server.m2d under MS2_DATA_FOLDER; reads archives without changing them.")]
[NonParallelizable]
public class ParserArchiveCompatibilityTests {
    [Test]
    public void ReadOnlyArchivesRetainEveryFishingLureLevel() {
        string? folder = Environment.GetEnvironmentVariable("MS2_DATA_FOLDER");
        if (string.IsNullOrWhiteSpace(folder)) {
            Assert.Ignore("Set MS2_DATA_FOLDER to run the parser archive regression.");
        }
        string path = Path.Combine(folder!, "Server.m2d");
        using var sharedRead = System.IO.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var server = new M2dReader(path);
        using var client = new M2dReader(Path.Combine(folder, "Xml.m2d"));
        Filter.Load(client, "NA", "Live");
        var mapper = new ServerTableMapper(server, client);
        mapper.Process();
        FishTable table = mapper.Results.Select(metadata => metadata.Table).OfType<FishTable>().Single();
        var source = new ServerTableParser(server).ParseFishLure().ToArray();

        Assert.That(source, Has.Length.EqualTo(57));
        Assert.That(table.Lures, Has.Count.EqualTo(55));
        Assert.That(table.Lures.Values.Sum(levels => levels.Count), Is.EqualTo(source.Length));
        foreach (var (code, level, _) in source) {
            FishTable.Lure lure = table.Lures[code][checked((short) level)];
            Assert.That(lure.BuffId, Is.EqualTo(code));
            Assert.That(lure.BuffLevel, Is.EqualTo(level));
        }
    }
}
