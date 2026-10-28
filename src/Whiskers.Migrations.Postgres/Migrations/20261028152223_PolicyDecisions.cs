using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PolicyDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PolicyDecisions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OperationId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    PrincipalKind = table.Column<string>(type: "text", nullable: false),
                    OnBehalfOfId = table.Column<string>(type: "text", nullable: true),
                    CredentialId = table.Column<string>(type: "text", nullable: true),
                    Surface = table.Column<string>(type: "text", nullable: false),
                    CapabilityId = table.Column<string>(type: "text", nullable: false),
                    RequiredLevel = table.Column<string>(type: "text", nullable: false),
                    TargetKind = table.Column<string>(type: "text", nullable: false),
                    TargetId = table.Column<string>(type: "text", nullable: false),
                    TargetName = table.Column<string>(type: "text", nullable: true),
                    TargetServerId = table.Column<string>(type: "text", nullable: true),
                    TargetRelated = table.Column<string>(type: "text", nullable: true),
                    InputHash = table.Column<string>(type: "text", nullable: false),
                    PolicyVersion = table.Column<string>(type: "text", nullable: false),
                    Preset = table.Column<string>(type: "text", nullable: false),
                    MatchedRules = table.Column<string>(type: "text", nullable: false),
                    Verdict = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PolicySnapshots",
                columns: table => new
                {
                    VersionHash = table.Column<string>(type: "text", nullable: false),
                    Preset = table.Column<string>(type: "text", nullable: false),
                    PolicyJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicySnapshots", x => x.VersionHash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyDecisions_OperationId",
                table: "PolicyDecisions",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyDecisions_PolicyVersion",
                table: "PolicyDecisions",
                column: "PolicyVersion");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyDecisions_PrincipalId_Timestamp",
                table: "PolicyDecisions",
                columns: new[] { "PrincipalId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyDecisions_Timestamp",
                table: "PolicyDecisions",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PolicyDecisions");

            migrationBuilder.DropTable(
                name: "PolicySnapshots");
        }
    }
}
