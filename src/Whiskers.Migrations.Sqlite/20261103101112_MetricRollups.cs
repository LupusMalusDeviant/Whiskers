using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whiskers.Migrations
{
    /// <inheritdoc />
    public partial class MetricRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContainerMetricRollups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TierMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerId = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuAvg = table.Column<double>(type: "REAL", nullable: false),
                    CpuMax = table.Column<double>(type: "REAL", nullable: false),
                    MemoryPercentAvg = table.Column<double>(type: "REAL", nullable: false),
                    MemoryPercentMax = table.Column<double>(type: "REAL", nullable: false),
                    MemoryUsageBytesAvg = table.Column<double>(type: "REAL", nullable: false),
                    MemoryUsageBytesMax = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerMetricRollups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MetricRollupStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RolledUpTo = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricRollupStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerMetricRollups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TierMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", nullable: false),
                    ServerName = table.Column<string>(type: "TEXT", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuAvg = table.Column<double>(type: "REAL", nullable: false),
                    CpuMax = table.Column<double>(type: "REAL", nullable: false),
                    MemoryPercentAvg = table.Column<double>(type: "REAL", nullable: false),
                    MemoryPercentMax = table.Column<double>(type: "REAL", nullable: false),
                    MemoryUsedBytesAvg = table.Column<double>(type: "REAL", nullable: false),
                    MemoryUsedBytesMax = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerMetricRollups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContainerMetricRollups_TierMinutes_ServerId_ContainerId_WindowStart",
                table: "ContainerMetricRollups",
                columns: new[] { "TierMinutes", "ServerId", "ContainerId", "WindowStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContainerMetricRollups_TierMinutes_WindowStart",
                table: "ContainerMetricRollups",
                columns: new[] { "TierMinutes", "WindowStart" });

            migrationBuilder.CreateIndex(
                name: "IX_ServerMetricRollups_TierMinutes_ServerId_WindowStart",
                table: "ServerMetricRollups",
                columns: new[] { "TierMinutes", "ServerId", "WindowStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerMetricRollups_TierMinutes_WindowStart",
                table: "ServerMetricRollups",
                columns: new[] { "TierMinutes", "WindowStart" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContainerMetricRollups");

            migrationBuilder.DropTable(
                name: "MetricRollupStates");

            migrationBuilder.DropTable(
                name: "ServerMetricRollups");
        }
    }
}
