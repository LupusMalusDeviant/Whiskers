using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class OperationJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Operations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ParentId = table.Column<string>(type: "text", nullable: true),
                    CapabilityId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    InputHash = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Instance = table.Column<string>(type: "text", nullable: false),
                    DecisionId = table.Column<string>(type: "text", nullable: true),
                    ApprovalId = table.Column<string>(type: "text", nullable: true),
                    AuditId = table.Column<long>(type: "bigint", nullable: true),
                    Verification = table.Column<string>(type: "text", nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Operations_ClaimedAt",
                table: "Operations",
                column: "ClaimedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_ParentId",
                table: "Operations",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_State",
                table: "Operations",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Operations");
        }
    }
}
