using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ServerWatch.Migrations
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
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServerId = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerId = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    AlertType = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Resolved = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    ActorType = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetName = table.Column<string>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: true),
                    ServerId = table.Column<string>(type: "TEXT", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContainerMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ContainerId = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CpuPercent = table.Column<double>(type: "REAL", nullable: false),
                    MemoryUsageBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryLimitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    NetworkRxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    NetworkTxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    BlockReadBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    BlockWriteBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CveFirstSeen",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IdentityKey = table.Column<string>(type: "TEXT", nullable: false),
                    CveId = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CveFirstSeen", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogAlertRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuleId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerId = table.Column<string>(type: "TEXT", nullable: true),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: true),
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    IsRegex = table.Column<bool>(type: "INTEGER", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    NotifyMatrix = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyMattermost = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CooldownMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTriggered = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TriggerCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogAlertRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpToolCalls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    ActorType = table.Column<string>(type: "TEXT", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    ParamsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Verdict = table.Column<string>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultSummary = table.Column<string>(type: "TEXT", nullable: true),
                    ServerId = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpToolCalls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NotificationId = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Link = table.Column<string>(type: "TEXT", nullable: true),
                    Read = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: false),
                    TaskType = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: true),
                    TargetName = table.Column<string>(type: "TEXT", nullable: true),
                    ServerId = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastRun = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRun = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", nullable: true),
                    LastSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    Config = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServerId = table.Column<string>(type: "TEXT", nullable: false),
                    ServerName = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CpuPercent = table.Column<double>(type: "REAL", nullable: false),
                    MemoryUsedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryTotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskUsedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskTotalBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskRunHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskName = table.Column<string>(type: "TEXT", nullable: false),
                    TaskType = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Output = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRunHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpdateHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ContainerId = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", nullable: true),
                    OldImageDigest = table.Column<string>(type: "TEXT", nullable: false),
                    NewImageDigest = table.Column<string>(type: "TEXT", nullable: false),
                    Image = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    RolledBack = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpdatePolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ContainerId = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", nullable: true),
                    AutoUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoRollback = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyBeforeUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    CheckIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    LastChecked = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUpdateResult = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdatePolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VolumeBackups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BackupId = table.Column<string>(type: "TEXT", nullable: false),
                    VolumeName = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolumeBackups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WebhookId = table.Column<string>(type: "TEXT", nullable: false),
                    WebhookName = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Output = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    SourceIp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Webhooks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WebhookId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Secret = table.Column<string>(type: "TEXT", nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastTriggered = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TriggerCount = table.Column<int>(type: "INTEGER", nullable: false)
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
