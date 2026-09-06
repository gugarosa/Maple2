using Maple2.Database.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maple2.Server.World.Migrations;

[DbContext(typeof(Ms2Context))]
[Migration("20260906125000_AccountWideDungeonRecords")]
public partial class AccountWideDungeonRecords : Migration {
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropForeignKey(
            name: "FK_dungeon-record_character_OwnerId",
            table: "dungeon-record");

        migrationBuilder.DropPrimaryKey(
            name: "PK_dungeon-record",
            table: "dungeon-record");

        migrationBuilder.AddColumn<bool>(
            name: "AccountWide",
            table: "dungeon-record",
            type: "tinyint(1)",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<long>(
            name: "CharacterOwnerId",
            table: "dungeon-record",
            type: "bigint",
            nullable: true);

        migrationBuilder.Sql("UPDATE `dungeon-record` SET `CharacterOwnerId` = `OwnerId`");

        migrationBuilder.AddPrimaryKey(
            name: "PK_dungeon-record",
            table: "dungeon-record",
            columns: new[] { "OwnerId", "AccountWide", "DungeonId" });

        migrationBuilder.CreateIndex(
            name: "IX_dungeon-record_CharacterOwnerId",
            table: "dungeon-record",
            column: "CharacterOwnerId");

        migrationBuilder.AddForeignKey(
            name: "FK_dungeon-record_character_CharacterOwnerId",
            table: "dungeon-record",
            column: "CharacterOwnerId",
            principalTable: "character",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);

        // Existing rows stay character-scoped. The first load of each metadata-declared
        // account-wide dungeon atomically merges that account's character rows.
    }

    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.Sql("""
            INSERT INTO `dungeon-record`
                (`OwnerId`, `AccountWide`, `DungeonId`, `CharacterOwnerId`, `ClearTime`, `TotalClears`,
                 `CurrentSubClears`, `CurrentClears`, `LifetimeRecord`, `CurrentRecord`,
                 `ExtraCurrentSubClears`, `ExtraCurrentClears`, `DailyResetTime`,
                 `UnionCooldownTime`, `CooldownTime`, `Flag`)
            SELECT
                c.`Id`,
                FALSE,
                r.`DungeonId`,
                c.`Id`,
                r.`ClearTime`,
                r.`TotalClears`,
                r.`CurrentSubClears`,
                r.`CurrentClears`,
                r.`LifetimeRecord`,
                r.`CurrentRecord`,
                r.`ExtraCurrentSubClears`,
                r.`ExtraCurrentClears`,
                r.`DailyResetTime`,
                r.`UnionCooldownTime`,
                r.`CooldownTime`,
                r.`Flag`
            FROM `dungeon-record` r
            INNER JOIN `character` c ON c.`AccountId` = r.`OwnerId`
            WHERE r.`AccountWide` = TRUE
            ON DUPLICATE KEY UPDATE
                `ClearTime` = GREATEST(`dungeon-record`.`ClearTime`, VALUES(`ClearTime`)),
                `TotalClears` = GREATEST(`dungeon-record`.`TotalClears`, VALUES(`TotalClears`)),
                `CurrentSubClears` = GREATEST(`dungeon-record`.`CurrentSubClears`, VALUES(`CurrentSubClears`)),
                `CurrentClears` = GREATEST(`dungeon-record`.`CurrentClears`, VALUES(`CurrentClears`)),
                `LifetimeRecord` = GREATEST(`dungeon-record`.`LifetimeRecord`, VALUES(`LifetimeRecord`)),
                `CurrentRecord` = GREATEST(`dungeon-record`.`CurrentRecord`, VALUES(`CurrentRecord`)),
                `ExtraCurrentSubClears` = GREATEST(`dungeon-record`.`ExtraCurrentSubClears`, VALUES(`ExtraCurrentSubClears`)),
                `ExtraCurrentClears` = GREATEST(`dungeon-record`.`ExtraCurrentClears`, VALUES(`ExtraCurrentClears`)),
                `DailyResetTime` = GREATEST(`dungeon-record`.`DailyResetTime`, VALUES(`DailyResetTime`)),
                `UnionCooldownTime` = GREATEST(`dungeon-record`.`UnionCooldownTime`, VALUES(`UnionCooldownTime`)),
                `CooldownTime` = GREATEST(`dungeon-record`.`CooldownTime`, VALUES(`CooldownTime`)),
                `Flag` = `dungeon-record`.`Flag` | VALUES(`Flag`)
            """);
        migrationBuilder.Sql("DELETE FROM `dungeon-record` WHERE `AccountWide` = TRUE");

        migrationBuilder.DropForeignKey(
            name: "FK_dungeon-record_character_CharacterOwnerId",
            table: "dungeon-record");

        migrationBuilder.DropPrimaryKey(
            name: "PK_dungeon-record",
            table: "dungeon-record");

        migrationBuilder.DropIndex(
            name: "IX_dungeon-record_CharacterOwnerId",
            table: "dungeon-record");

        migrationBuilder.DropColumn(
            name: "AccountWide",
            table: "dungeon-record");

        migrationBuilder.DropColumn(
            name: "CharacterOwnerId",
            table: "dungeon-record");

        migrationBuilder.AddPrimaryKey(
            name: "PK_dungeon-record",
            table: "dungeon-record",
            columns: new[] { "OwnerId", "DungeonId" });

        migrationBuilder.AddForeignKey(
            name: "FK_dungeon-record_character_OwnerId",
            table: "dungeon-record",
            column: "OwnerId",
            principalTable: "character",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }
}
