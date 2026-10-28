using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalRequesters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequesterIds",
                table: "Approvals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SelfApproved",
                table: "Approvals",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequesterIds",
                table: "Approvals");

            migrationBuilder.DropColumn(
                name: "SelfApproved",
                table: "Approvals");
        }
    }
}
