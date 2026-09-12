using System.Linq;
using Maple2.Server.World.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Maple2.Server.Tests.Persistence;

public class InteractCubeMigrationTests {
    [Test]
    public void CleanupUsesTheMigrationConnectionWithoutAnEnvironmentDatabaseQualifier() {
        var migration = new InteractCubeFix();
        string[] sql = migration.UpOperations.Cast<SqlOperation>().Select(operation => operation.Sql).ToArray();

        Assert.That(sql, Is.EqualTo(new[] {
            "DELETE FROM `ugcmap-cube` WHERE `Interact` IS NOT NULL AND `Interact` <> '';",
            "DELETE FROM `home-layout-cube` WHERE `Interact` IS NOT NULL AND `Interact` <> '';",
        }));
    }
}
