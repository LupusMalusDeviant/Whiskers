using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Whiskers.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class WebhookDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "Webhooks",
                type: "text",
                nullable: false,
                defaultValue: "github");

            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WebhookId = table.Column<string>(type: "text", nullable: false),
                    DeliveryKey = table.Column<string>(type: "text", nullable: false),
                    RunId = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Instance = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_ReceivedAt",
                table: "WebhookDeliveries",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_RunId",
                table: "WebhookDeliveries",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_WebhookId_DeliveryKey",
                table: "WebhookDeliveries",
                columns: new[] { "WebhookId", "DeliveryKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookDeliveries");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Webhooks");
        }
    }
}
