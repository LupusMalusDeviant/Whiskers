using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations
{
    /// <inheritdoc />
    public partial class EndpointChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EndpointCheckResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CheckId = table.Column<string>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    LatencyMs = table.Column<int>(type: "INTEGER", nullable: true),
                    HttpStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    TlsNotAfterUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AnsweredAddress = table.Column<string>(type: "TEXT", nullable: true),
                    AddressesTried = table.Column<int>(type: "INTEGER", nullable: false),
                    AddressesNotTried = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointCheckResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EndpointChecks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    ExpectedStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    TlsWarnDays = table.Column<int>(type: "INTEGER", nullable: true),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ExceptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SuspendedReason = table.Column<string>(type: "TEXT", nullable: true),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOutcome = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointChecks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointCheckResults_CheckId_AtUtc",
                table: "EndpointCheckResults",
                columns: new[] { "CheckId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EndpointChecks_ServerId",
                table: "EndpointChecks",
                column: "ServerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EndpointCheckResults");

            migrationBuilder.DropTable(
                name: "EndpointChecks");
        }
    }
}
