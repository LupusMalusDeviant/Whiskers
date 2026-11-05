using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Whiskers.Migrations.Postgres.Migrations
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CheckId = table.Column<string>(type: "text", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OperationId = table.Column<string>(type: "text", nullable: true),
                    Outcome = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: false),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    TlsNotAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnsweredAddress = table.Column<string>(type: "text", nullable: true),
                    AddressesTried = table.Column<int>(type: "integer", nullable: false),
                    AddressesNotTried = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndpointCheckResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EndpointChecks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ServerId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Target = table.Column<string>(type: "text", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: true),
                    ExpectedStatus = table.Column<int>(type: "integer", nullable: true),
                    TlsWarnDays = table.Column<int>(type: "integer", nullable: true),
                    IntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    ExceptionsJson = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SuspendedReason = table.Column<string>(type: "text", nullable: true),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastOutcome = table.Column<string>(type: "text", nullable: true)
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
