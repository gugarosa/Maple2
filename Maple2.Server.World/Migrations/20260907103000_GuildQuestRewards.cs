using Maple2.Database.Context;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maple2.Server.World.Migrations;

[DbContext(typeof(Ms2Context))]
[Migration("20260907103000_GuildQuestRewards")]
public partial class GuildQuestRewards : Migration {
    protected override void Up(MigrationBuilder migrationBuilder) {
        migrationBuilder.CreateTable(
            name: "guild-quest-reward",
            columns: table => new {
                OwnerId = table.Column<long>(type: "bigint", nullable: false),
                QuestId = table.Column<int>(type: "int", nullable: false),
                CompletionCount = table.Column<int>(type: "int", nullable: false),
                StartTime = table.Column<long>(type: "bigint", nullable: false),
                CharacterId = table.Column<long>(type: "bigint", nullable: false),
                GuildId = table.Column<long>(type: "bigint", nullable: false),
            },
            constraints: table => {
                table.PrimaryKey("PK_guild-quest-reward", value => new {
                    value.OwnerId,
                    value.QuestId,
                    value.StartTime,
                    value.CompletionCount,
                });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) {
        migrationBuilder.DropTable(name: "guild-quest-reward");
    }
}
