using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations
{
    /// <inheritdoc />
    public partial class Approvals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Approvals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    ToolCallId = table.Column<string>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    ActorType = table.Column<string>(type: "TEXT", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    ParamsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Diff = table.Column<string>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: true),
                    RiskLevel = table.Column<string>(type: "TEXT", nullable: true),
                    TargetServer = table.Column<string>(type: "TEXT", nullable: true),
                    TargetWorkload = table.Column<string>(type: "TEXT", nullable: true),
                    GuardrailPreset = table.Column<string>(type: "TEXT", nullable: true),
                    GuardrailRule = table.Column<string>(type: "TEXT", nullable: true),
                    ArgumentsHash = table.Column<string>(type: "TEXT", nullable: true),
                    DecisionId = table.Column<string>(type: "TEXT", nullable: true),
                    Binding = table.Column<string>(type: "TEXT", nullable: true),
                    Interactive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", nullable: true),
                    GrantExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConsumedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConsumedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Approvals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_Binding",
                table: "Approvals",
                column: "Binding");

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_CorrelationId",
                table: "Approvals",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_CreatedAt",
                table: "Approvals",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_Status",
                table: "Approvals",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Approvals");
        }
    }
}
