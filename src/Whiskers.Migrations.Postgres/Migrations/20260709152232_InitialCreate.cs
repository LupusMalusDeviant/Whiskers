using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Whiskers.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<string>(type: "text", nullable: false),
                    ContainerId = table.Column<string>(type: "text", nullable: false),
                    ContainerName = table.Column<string>(type: "text", nullable: false),
                    AlertType = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Resolved = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    ActorType = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    TargetType = table.Column<string>(type: "text", nullable: false),
                    TargetId = table.Column<string>(type: "text", nullable: false),
                    TargetName = table.Column<string>(type: "text", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: true),
                    ServerId = table.Column<string>(type: "text", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContainerMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContainerId = table.Column<string>(type: "text", nullable: false),
                    ContainerName = table.Column<string>(type: "text", nullable: false),
                    ServerId = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CpuPercent = table.Column<double>(type: "double precision", nullable: false),
                    MemoryUsageBytes = table.Column<long>(type: "bigint", nullable: false),
                    MemoryLimitBytes = table.Column<long>(type: "bigint", nullable: false),
                    NetworkRxBytes = table.Column<long>(type: "bigint", nullable: false),
                    NetworkTxBytes = table.Column<long>(type: "bigint", nullable: false),
                    BlockReadBytes = table.Column<long>(type: "bigint", nullable: false),
                    BlockWriteBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CveFirstSeen",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IdentityKey = table.Column<string>(type: "text", nullable: false),
                    CveId = table.Column<string>(type: "text", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CveFirstSeen", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogAlertRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RuleId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ContainerId = table.Column<string>(type: "text", nullable: true),
                    ContainerName = table.Column<string>(type: "text", nullable: true),
                    Pattern = table.Column<string>(type: "text", nullable: false),
                    IsRegex = table.Column<bool>(type: "boolean", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    NotifyMatrix = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyMattermost = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CooldownMinutes = table.Column<int>(type: "integer", nullable: false),
                    LastTriggered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TriggerCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogAlertRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpToolCalls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    ActorType = table.Column<string>(type: "text", nullable: false),
                    ToolName = table.Column<string>(type: "text", nullable: false),
                    Level = table.Column<string>(type: "text", nullable: false),
                    ParamsJson = table.Column<string>(type: "text", nullable: true),
                    Verdict = table.Column<string>(type: "text", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    ResultSummary = table.Column<string>(type: "text", nullable: true),
                    ServerId = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpToolCalls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NotificationId = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    Link = table.Column<string>(type: "text", nullable: true),
                    Read = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CronExpression = table.Column<string>(type: "text", nullable: false),
                    TaskType = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<string>(type: "text", nullable: true),
                    TargetName = table.Column<string>(type: "text", nullable: true),
                    ServerId = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastRun = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRun = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastResult = table.Column<string>(type: "text", nullable: true),
                    LastSuccess = table.Column<bool>(type: "boolean", nullable: false),
                    Config = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<string>(type: "text", nullable: false),
                    ServerName = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CpuPercent = table.Column<double>(type: "double precision", nullable: false),
                    MemoryUsedBytes = table.Column<long>(type: "bigint", nullable: false),
                    MemoryTotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    DiskUsedBytes = table.Column<long>(type: "bigint", nullable: false),
                    DiskTotalBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskRunHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<string>(type: "text", nullable: false),
                    TaskName = table.Column<string>(type: "text", nullable: false),
                    TaskType = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    Output = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRunHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpdateHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContainerId = table.Column<string>(type: "text", nullable: false),
                    ContainerName = table.Column<string>(type: "text", nullable: false),
                    ServerId = table.Column<string>(type: "text", nullable: true),
                    OldImageDigest = table.Column<string>(type: "text", nullable: false),
                    NewImageDigest = table.Column<string>(type: "text", nullable: false),
                    Image = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    RolledBack = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpdatePolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContainerId = table.Column<string>(type: "text", nullable: false),
                    ContainerName = table.Column<string>(type: "text", nullable: false),
                    ServerId = table.Column<string>(type: "text", nullable: true),
                    AutoUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    AutoRollback = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyBeforeUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    CheckIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    LastChecked = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUpdateResult = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdatePolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VolumeBackups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BackupId = table.Column<string>(type: "text", nullable: false),
                    VolumeName = table.Column<string>(type: "text", nullable: false),
                    ContainerName = table.Column<string>(type: "text", nullable: false),
                    ServerId = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolumeBackups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WebhookId = table.Column<string>(type: "text", nullable: false),
                    WebhookName = table.Column<string>(type: "text", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    Output = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    SourceIp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Webhooks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WebhookId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Secret = table.Column<string>(type: "text", nullable: false),
                    TargetType = table.Column<string>(type: "text", nullable: false),
                    TargetId = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    ServerId = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastTriggered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TriggerCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Webhooks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertHistory_ServerId_AlertType",
                table: "AlertHistory",
                columns: new[] { "ServerId", "AlertType" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertHistory_Timestamp",
                table: "AlertHistory",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Action_Timestamp",
                table: "AuditLog",
                columns: new[] { "Action", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Actor",
                table: "AuditLog",
                column: "Actor");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Timestamp",
                table: "AuditLog",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ContainerMetrics_ContainerId_ServerId_Timestamp",
                table: "ContainerMetrics",
                columns: new[] { "ContainerId", "ServerId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ContainerMetrics_Timestamp",
                table: "ContainerMetrics",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_CveFirstSeen_CveId",
                table: "CveFirstSeen",
                column: "CveId");

            migrationBuilder.CreateIndex(
                name: "IX_CveFirstSeen_IdentityKey",
                table: "CveFirstSeen",
                column: "IdentityKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogAlertRules_Enabled",
                table: "LogAlertRules",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_LogAlertRules_RuleId",
                table: "LogAlertRules",
                column: "RuleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpToolCalls_Actor",
                table: "McpToolCalls",
                column: "Actor");

            migrationBuilder.CreateIndex(
                name: "IX_McpToolCalls_Timestamp",
                table: "McpToolCalls",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_McpToolCalls_ToolName_Timestamp",
                table: "McpToolCalls",
                columns: new[] { "ToolName", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Read",
                table: "Notifications",
                column: "Read");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Severity_Timestamp",
                table: "Notifications",
                columns: new[] { "Severity", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Timestamp",
                table: "Notifications",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_Enabled",
                table: "ScheduledTasks",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_NextRun",
                table: "ScheduledTasks",
                column: "NextRun");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_TaskId",
                table: "ScheduledTasks",
                column: "TaskId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerMetrics_ServerId_Timestamp",
                table: "ServerMetrics",
                columns: new[] { "ServerId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ServerMetrics_Timestamp",
                table: "ServerMetrics",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRunHistory_StartedAt",
                table: "TaskRunHistory",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRunHistory_TaskId",
                table: "TaskRunHistory",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_VolumeBackups_BackupId",
                table: "VolumeBackups",
                column: "BackupId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolumeBackups_CreatedAt",
                table: "VolumeBackups",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_VolumeBackups_ServerId_VolumeName",
                table: "VolumeBackups",
                columns: new[] { "ServerId", "VolumeName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertHistory");

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "ContainerMetrics");

            migrationBuilder.DropTable(
                name: "CveFirstSeen");

            migrationBuilder.DropTable(
                name: "LogAlertRules");

            migrationBuilder.DropTable(
                name: "McpToolCalls");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "ScheduledTasks");

            migrationBuilder.DropTable(
                name: "ServerMetrics");

            migrationBuilder.DropTable(
                name: "TaskRunHistory");

            migrationBuilder.DropTable(
                name: "UpdateHistory");

            migrationBuilder.DropTable(
                name: "UpdatePolicies");

            migrationBuilder.DropTable(
                name: "VolumeBackups");

            migrationBuilder.DropTable(
                name: "WebhookLogs");

            migrationBuilder.DropTable(
                name: "Webhooks");
        }
    }
}
