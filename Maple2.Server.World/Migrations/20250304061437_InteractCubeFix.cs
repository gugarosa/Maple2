using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maple2.Server.World.Migrations {
    /// <inheritdoc />
    public partial class InteractCubeFix : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.Sql("DELETE FROM `ugcmap-cube` WHERE `Interact` IS NOT NULL AND `Interact` <> '';");
            migrationBuilder.Sql("DELETE FROM `home-layout-cube` WHERE `Interact` IS NOT NULL AND `Interact` <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {

        }
    }
}
