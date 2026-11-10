using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class WebhookSecretVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SecretVersion",
                table: "Webhooks",
                type: "integer",
                nullable: false,
                // Contract 0014 (D4): every secret stored before is its first generation.
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecretVersion",
                table: "Webhooks");
        }
    }
}
