using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Whiskers.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddUpdateRollback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UpdateRollbacks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContainerId = table.Column<string>(type: "text", nullable: false),
                    ContainerName = table.Column<string>(type: "text", nullable: false),
                    ServerId = table.Column<string>(type: "text", nullable: true),
                    OldImageRef = table.Column<string>(type: "text", nullable: false),
                    ConfigJson = table.Column<string>(type: "text", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateRollbacks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateRollbacks_ContainerId_ServerId",
                table: "UpdateRollbacks",
                columns: new[] { "ContainerId", "ServerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UpdateRollbacks");
        }
    }
}
