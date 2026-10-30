using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations
{
    /// <inheritdoc />
    public partial class AuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditChain",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    AuditId = table.Column<long>(type: "INTEGER", nullable: false),
                    RowHash = table.Column<string>(type: "TEXT", nullable: false),
                    PrevHash = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Mac = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditChain", x => x.Seq);
                });

            migrationBuilder.CreateTable(
                name: "AuditCheckpoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Seq = table.Column<long>(type: "INTEGER", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    PrunedThroughId = table.Column<long>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Mac = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditCheckpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditChain_AuditId",
                table: "AuditChain",
                column: "AuditId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditCheckpoints_Kind_Seq",
                table: "AuditCheckpoints",
                columns: new[] { "Kind", "Seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditChain");

            migrationBuilder.DropTable(
                name: "AuditCheckpoints");
        }
    }
}
