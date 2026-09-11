using System.Diagnostics;
using System.Globalization;
using Maple2.Database.Context;
using Maple2.Database.Extensions;
using Maple2.Database.Model.Metadata;
using Maple2.File.Ingest;
using Maple2.File.Ingest.Helpers;
using Maple2.File.Ingest.Mapper;
using Maple2.File.IO;
using Maple2.File.IO.Nif;
using Maple2.File.Parser.Flat;
using Maple2.File.Parser.MapXBlock;
using Maple2.File.Parser.Tools;
using Maple2.Tools;
using Maple2.Tools.Extensions;
using Microsoft.EntityFrameworkCore;

const string locale = "NA";
string language = "en";
const string env = "Live";

Console.OutputEncoding = System.Text.Encoding.UTF8;

bool runNavmesh = false;
bool dropData = false;

if (args.Any(arg => arg is "-h" or "--help")) {
    PrintUsage();
    return;
}

foreach (string arg in args) {
    switch (arg) {
        case "--run-navmesh":
            runNavmesh = true;
            break;
        case "--drop-data":
            dropData = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown option: {arg}");
            PrintUsage();
            Environment.ExitCode = 2;
            return;
    }
}

// Force Globalization to en-US because we use periods instead of commas for decimals
CultureInfo.CurrentCulture = new CultureInfo("en-US");

DotEnv.Load();

string? languageEnv = Environment.GetEnvironmentVariable("LANGUAGE");
if (languageEnv == null) {
    throw new ArgumentException("LANGUAGE environment variable was not set");
}
language = languageEnv.ToLower();

string? ms2Root = Environment.GetEnvironmentVariable("MS2_DATA_FOLDER");
if (ms2Root == null) {
    throw new ArgumentException("MS2_DATA_FOLDER environment variable was not set");
}

string xmlPath = Path.Combine(ms2Root, "Xml.m2d");
string exportedPath = Path.Combine(ms2Root, "Resource", "Exported.m2d");
string serverPath = Path.Combine(ms2Root, "Server.m2d");
(string Prefix, string ArchivePath)[] modelArchives = [
    ("/library/", Path.Combine(ms2Root, "Resource", "Library.m2d")),
    ("/model/map/", Path.Combine(ms2Root, "Resource", "Model", "Map.m2d")),
    ("/model/effect/", Path.Combine(ms2Root, "Resource", "Model", "Effect.m2d")),
    ("/model/camera/", Path.Combine(ms2Root, "Resource", "Model", "Camera.m2d")),
    ("/model/tool/", Path.Combine(ms2Root, "Resource", "Model", "Tool.m2d")),
    ("/model/item/", Path.Combine(ms2Root, "Resource", "Model", "Item.m2d")),
    ("/model/npc/", Path.Combine(ms2Root, "Resource", "Model", "Npc.m2d")),
    ("/model/path/", Path.Combine(ms2Root, "Resource", "Model", "Path.m2d")),
    ("/model/character/", Path.Combine(ms2Root, "Resource", "Model", "Character.m2d")),
    ("/model/textures/", Path.Combine(ms2Root, "Resource", "Model", "Textures.m2d")),
];
foreach (string archive in new[] { xmlPath, exportedPath, serverPath }.Concat(modelArchives.Select(entry => entry.ArchivePath))) {
    foreach (string required in new[] { archive, Path.ChangeExtension(archive, ".m2h") }) {
        if (!File.Exists(required)) {
            throw new FileNotFoundException($"Required client archive is missing: {required}. Supply the original client archives and customized Server.m2d/Server.m2h before ingestion.");
        }
    }
}

string? server = Environment.GetEnvironmentVariable("DB_IP");
string? port = Environment.GetEnvironmentVariable("DB_PORT");
string? database = Environment.GetEnvironmentVariable("DATA_DB_NAME");
string? gameDatabase = Environment.GetEnvironmentVariable("GAME_DB_NAME");
string? user = Environment.GetEnvironmentVariable("DB_USER");
string? password = Environment.GetEnvironmentVariable("DB_PASSWORD");

SchemaVersionManager.ValidateDatabaseNames(database, gameDatabase);
string dataDbConnection = DatabaseConnectionString.Build(server, port, database, user, password);
string gameDbConnection = DatabaseConnectionString.Build(server, port, gameDatabase, user, password);

string worldServerDir = Path.Combine(Paths.SOLUTION_DIR, "Maple2.Server.World");

RunDotnet(Paths.SOLUTION_DIR, "tool", "restore");
RunDotnet(worldServerDir, "restore");

Console.WriteLine("Migrating game database...");
RunDotnet(worldServerDir, "ef", "database", "update");

Console.WriteLine("Game Migration complete!");

using var xmlReader = new M2dReader(xmlPath);
using var exportedReader = new M2dReader(exportedPath);
using var serverReader = new M2dReader(serverPath);

DbContextOptions options = new DbContextOptionsBuilder()
    .UseMySql(dataDbConnection, ServerVersion.AutoDetect(gameDbConnection)).Options;

Console.WriteLine("Connecting to metadata database...");
using var metadataContext = new MetadataContext(options);

bool created = metadataContext.Database.EnsureCreated();
bool schemaChanged = !created && SchemaVersionManager.ShouldRecreateDatabase(metadataContext);

if (dropData || schemaChanged) {
    Console.WriteLine("Dropping metadata database...");
    metadataContext.Database.EnsureDeleted();
    metadataContext.ChangeTracker.Clear();
}
Console.WriteLine("Ensuring metadata database is created...");
metadataContext.Database.EnsureCreated();
metadataContext.Database.ExecuteSqlRaw(@"SET GLOBAL max_allowed_packet=268435456"); // 256MB

// Store schema version after creation
SchemaVersionManager.StoreSchemaVersion(metadataContext);

Console.WriteLine("Starting data ingestion...");

// Filter Xml results based on feature settings.
Filter.Load(xmlReader, locale, env);

// new TriggerGenerator(xmlReader).Generate();

UpdateDatabase(metadataContext, new TriggerMapper(xmlReader));

UpdateDatabase(metadataContext, new ItemMapper(xmlReader, language, false));
UpdateDatabase(metadataContext, new NpcMapper(xmlReader, language));

UpdateDatabase(metadataContext, new ServerTableMapper(serverReader, xmlReader));
UpdateDatabase(metadataContext, new AiMapper(serverReader));

UpdateDatabase(metadataContext, new AdditionalEffectMapper(xmlReader));
UpdateDatabase(metadataContext, new AnimationMapper(xmlReader));
UpdateDatabase(metadataContext, new PetMapper(xmlReader));
UpdateDatabase(metadataContext, new MapMapper(xmlReader, language));
UpdateDatabase(metadataContext, new UgcMapMapper(xmlReader));
UpdateDatabase(metadataContext, new ExportedUgcMapMapper(xmlReader));
UpdateDatabase(metadataContext, new QuestMapper(xmlReader, language));
UpdateDatabase(metadataContext, new RideMapper(xmlReader));
UpdateDatabase(metadataContext, new ScriptMapper(xmlReader, language));
UpdateDatabase(metadataContext, new SkillMapper(xmlReader, language));
UpdateDatabase(metadataContext, new TableMapper(xmlReader, language));
UpdateDatabase(metadataContext, new AchievementMapper(xmlReader));
UpdateDatabase(metadataContext, new FunctionCubeMapper(xmlReader));
UpdateDatabase(metadataContext, new BanWordMapper(xmlReader));

List<PrefixedM2dReader> modelReaders = [];
try {
    foreach (var entry in modelArchives) {
        modelReaders.Add(new PrefixedM2dReader(entry.Prefix, entry.ArchivePath));
    }
    NifParserHelper.ParseNif(modelReaders);
} finally {
    foreach (PrefixedM2dReader reader in modelReaders) {
        reader.Dispose();
    }
}

UpdateDatabase(metadataContext, new NifMapper());
UpdateDatabase(metadataContext, new NxsMeshMapper());

var index = new FlatTypeIndex(exportedReader);

var xBlockParser = new XBlockParser(exportedReader, index);

UpdateDatabase(metadataContext, new MapEntityMapper(metadataContext, xBlockParser));

var mapDataMapper = new MapDataMapper(metadataContext, xBlockParser);

UpdateDatabase(metadataContext, mapDataMapper);

mapDataMapper.ReportStats();

if (runNavmesh) {
    _ = new NavMeshMapper(metadataContext, exportedReader);
}

Console.WriteLine("Done!".ColorGreen());

void PrintUsage() {
    Console.WriteLine("Usage: dotnet run -- [--run-navmesh] [--drop-data]");
    Console.WriteLine("  --run-navmesh  Generate navmeshes after metadata ingestion.");
    Console.WriteLine("  --drop-data     Recreate the metadata database before ingestion.");
}

void RunDotnet(string workingDirectory, params string[] arguments) {
    var startInfo = new ProcessStartInfo("dotnet") {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
    };
    foreach (string argument in arguments) {
        startInfo.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("Failed to start the dotnet command.");
    process.WaitForExit();
    if (process.ExitCode != 0) {
        throw new InvalidOperationException(
            $"dotnet {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.");
    }
}

void UpdateDatabase<T>(DbContext context, TypeMapper<T> mapper) where T : class {
    string? tableName = context.GetTableName<T>();
    Debug.Assert(!string.IsNullOrEmpty(tableName), $"Invalid table name: {tableName}");

    Console.Write($"Processing {tableName}... ");
    uint crc32C = mapper.Process();
    Console.Write($"Finished in {mapper.ElapsedMilliseconds}ms");
    Console.WriteLine();

    var checksum = context.Find<TableChecksum>(tableName);
    if (checksum != null) {
        if (checksum.Crc32C == crc32C) {
            Console.WriteLine($"Table {tableName} is up-to-date".ColorGreen());
            return;
        }

        checksum.Crc32C = crc32C;
        Console.WriteLine($"Table {tableName} outdated".ColorRed());
        int result = context.Database.ExecuteSqlRaw(@$"DELETE FROM `{tableName}`");
        Console.WriteLine($"Removed table {tableName} rows: {result}");
    }

    Stopwatch stopwatch = Stopwatch.StartNew();
    // Write entries to table
    foreach (T result in mapper.Results) {
        context.Add(result);
    }

    // Write checksum to table
    if (checksum == null) {
        context.Add(new TableChecksum {
            TableName = tableName,
            Crc32C = crc32C,
        });
    } else {
        context.Update(checksum);
    }

    context.SaveChanges();

    stopwatch.Stop();
    Console.WriteLine($"Wrote {mapper.Results.Count} entries to {tableName} in {stopwatch.ElapsedMilliseconds}ms");
}
