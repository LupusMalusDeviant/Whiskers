using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations
{
    /// <inheritdoc />
    public partial class AddUpdateRollback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpdateRollbacks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ContainerId = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", nullable: true),
                    OldImageRef = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateRollbacks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateRollbacks_ContainerId_ServerId",
                table: "UpdateRollbacks",
                columns: new[] { "ContainerId", "ServerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpdateRollbacks");
        }
    }
}
