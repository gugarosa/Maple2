using System;
using Maple2.File.Ingest;

namespace Maple2.Server.Tests.File.Ingest;

public class IngestionDatabaseSafetyTests {
    [TestCase("game-server", "game-server")]
    [TestCase("GAME-SERVER", "game-server")]
    [TestCase("mysql", "game-server")]
    [TestCase("maple-data", "sys")]
    [TestCase("", "game-server")]
    [TestCase("maple-data", null)]
    public void UnsafeDatabaseTargetsAreRejectedBeforeMigrations(string? metadata, string? game) {
        Assert.Throws<ArgumentException>(() => SchemaVersionManager.ValidateDatabaseNames(metadata, game));
    }

    [Test]
    public void SeparateMetadataAndPlayerSchemasAreAllowed() {
        Assert.DoesNotThrow(() => SchemaVersionManager.ValidateDatabaseNames("maple-data", "game-server"));
    }
}
