using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoryProcessInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProcessInstance",
                table: "UpdateHistory",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcessInstance",
                table: "TaskRunHistory",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessInstance",
                table: "UpdateHistory");

            migrationBuilder.DropColumn(
                name: "ProcessInstance",
                table: "TaskRunHistory");
        }
    }
}
