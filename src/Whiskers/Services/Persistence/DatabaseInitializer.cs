using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Whiskers.Services.Persistence;

/// <summary>Brings the SQLite metrics database to the current schema on startup using EF Core migrations,
/// while safely adopting migrations on databases that were originally created by <c>EnsureCreated</c> +
/// hand-written DDL (every deployment prior to ADR-0003).
///
/// The trap this guards against: an <c>EnsureCreated</c> database has no <c>__EFMigrationsHistory</c> table,
/// so a naive <c>MigrateAsync</c> would try to re-run <c>InitialCreate</c> against tables that already exist
/// and crash-loop the app. Instead we detect that case and <b>baseline</b> the database: heal any table the
/// old DDL forgot, then record <c>InitialCreate</c> as already applied (without running it), then migrate.
///
/// The routine is non-destructive by construction — it only issues <c>CREATE TABLE IF NOT EXISTS</c>, a
/// single history <c>INSERT</c>, and <c>MigrateAsync</c> (which has nothing pending for a baselined DB). It
/// never drops or rewrites existing data. See <c>DbMigrationBaselineTests</c> for the data-safety proof.</summary>
public static class DatabaseInitializer
{
    // Sentinel table present in every schema version — its presence (with no migration history) is how we
    // recognise a legacy EnsureCreated database that must be baselined rather than migrated from scratch.
    private const string SentinelTable = "ContainerMetrics";

    public static async Task InitializeAsync(MetricsDbContext db, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();

            if (applied.Count == 0 && await TableExistsAsync(db, SentinelTable, ct))
            {
                // Legacy EnsureCreated database: tables exist but there is no migration history. Baseline it.
                logger.LogWarning(
                    "Metrics DB: legacy schema without migration history detected — baselining onto EF Core migrations.");

                // 1. Heal — create any table the old hand-DDL forgot (notably AlertHistory) so the on-disk
                //    schema matches InitialCreate before we mark InitialCreate as applied. Idempotent.
                await db.Database.ExecuteSqlRawAsync(LegacyHealSql, ct);

                // 2. Stamp — record InitialCreate as applied WITHOUT running its CREATE TABLEs (already present).
                await StampBaselineAsync(db, ct);
            }

            // Fresh DB: applies InitialCreate (and any later migrations). Baselined DB: applies only migrations
            // after InitialCreate (none today). Up-to-date DB: no-op.
            await db.Database.MigrateAsync(ct);

            // OPT-1: WAL lets readers (UI/agent) run concurrently with the 30s metric writes instead of
            // blocking on "database is locked". journal_mode=WAL persists in the DB header; synchronous is
            // best-effort per connection. Must run outside a transaction — none is active here.
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;", ct);

            var total = (await db.Database.GetAppliedMigrationsAsync(ct)).Count();
            logger.LogInformation("Metrics DB ready ({Count} migration(s) applied).", total);
        }
        catch (Exception ex)
        {
            // Fail fast with a clear, actionable log — never swallow into a silent crash loop.
            logger.LogCritical(ex, "Metrics DB initialization failed — the schema could not be brought up to date.");
            throw;
        }
    }

    /// <summary>Records the first (baseline) migration as applied on a legacy database without executing it.</summary>
    private static async Task StampBaselineAsync(MetricsDbContext db, CancellationToken ct)
    {
        var baseline = db.Database.GetMigrations().FirstOrDefault()
            ?? throw new InvalidOperationException("No EF Core migrations found in the assembly to baseline against.");

        var history = db.GetService<IHistoryRepository>();
        // Create __EFMigrationsHistory if it is missing, then insert the baseline migration id.
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
        await db.Database.ExecuteSqlRawAsync(
            history.GetInsertScript(new HistoryRow(baseline, ProductInfo.GetVersion())), ct);
    }

    private static async Task<bool> TableExistsAsync(MetricsDbContext db, string table, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        var p = cmd.CreateParameter();
        p.ParameterName = "$name";
        p.Value = table;
        cmd.Parameters.Add(p);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // One-time reconciliation SQL for legacy EnsureCreated databases. Relocated verbatim from the old
    // Program.cs startup block (which is now retired in favour of migrations). It only runs on the legacy
    // baseline path; migration-managed databases never touch it. CREATE TABLE IF NOT EXISTS keeps it safe.
    private const string LegacyHealSql = """
        CREATE TABLE IF NOT EXISTS "ContainerMetrics" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "ContainerId" TEXT NOT NULL,
            "ContainerName" TEXT NOT NULL,
            "ServerId" TEXT NOT NULL,
            "Timestamp" TEXT NOT NULL,
            "CpuPercent" REAL NOT NULL,
            "MemoryUsageBytes" INTEGER NOT NULL,
            "MemoryLimitBytes" INTEGER NOT NULL,
            "NetworkRxBytes" INTEGER NOT NULL,
            "NetworkTxBytes" INTEGER NOT NULL,
            "BlockReadBytes" INTEGER NOT NULL,
            "BlockWriteBytes" INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_ContainerMetrics_ContainerId_ServerId_Timestamp" ON "ContainerMetrics" ("ContainerId", "ServerId", "Timestamp");
        CREATE INDEX IF NOT EXISTS "IX_ContainerMetrics_Timestamp" ON "ContainerMetrics" ("Timestamp");

        CREATE TABLE IF NOT EXISTS "ServerMetrics" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "ServerId" TEXT NOT NULL,
            "ServerName" TEXT NOT NULL,
            "Timestamp" TEXT NOT NULL,
            "CpuPercent" REAL NOT NULL,
            "MemoryUsedBytes" INTEGER NOT NULL,
            "MemoryTotalBytes" INTEGER NOT NULL,
            "DiskUsedBytes" INTEGER NOT NULL,
            "DiskTotalBytes" INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_ServerMetrics_ServerId_Timestamp" ON "ServerMetrics" ("ServerId", "Timestamp");
        CREATE INDEX IF NOT EXISTS "IX_ServerMetrics_Timestamp" ON "ServerMetrics" ("Timestamp");

        CREATE TABLE IF NOT EXISTS "AlertHistory" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "ServerId" TEXT NOT NULL,
            "ContainerId" TEXT NOT NULL,
            "ContainerName" TEXT NOT NULL,
            "AlertType" TEXT NOT NULL,
            "Message" TEXT NOT NULL,
            "Timestamp" TEXT NOT NULL,
            "Resolved" INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_AlertHistory_Timestamp" ON "AlertHistory" ("Timestamp");
        CREATE INDEX IF NOT EXISTS "IX_AlertHistory_ServerId_AlertType" ON "AlertHistory" ("ServerId", "AlertType");

        CREATE TABLE IF NOT EXISTS "AuditLog" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "Timestamp" TEXT NOT NULL,
            "Actor" TEXT NOT NULL,
            "ActorType" TEXT NOT NULL,
            "Action" TEXT NOT NULL,
            "TargetType" TEXT NOT NULL,
            "TargetId" TEXT NOT NULL,
            "TargetName" TEXT NOT NULL,
            "Details" TEXT,
            "ServerId" TEXT,
            "Success" INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_AuditLog_Timestamp" ON "AuditLog" ("Timestamp");
        CREATE INDEX IF NOT EXISTS "IX_AuditLog_Action_Timestamp" ON "AuditLog" ("Action", "Timestamp");
        CREATE INDEX IF NOT EXISTS "IX_AuditLog_Actor" ON "AuditLog" ("Actor");

        CREATE TABLE IF NOT EXISTS "Notifications" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "NotificationId" TEXT NOT NULL,
            "Timestamp" TEXT NOT NULL,
            "EventType" TEXT NOT NULL,
            "Title" TEXT NOT NULL,
            "Detail" TEXT NOT NULL,
            "Severity" TEXT NOT NULL,
            "Link" TEXT,
            "Read" INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_Notifications_Timestamp" ON "Notifications" ("Timestamp");
        CREATE INDEX IF NOT EXISTS "IX_Notifications_Severity_Timestamp" ON "Notifications" ("Severity", "Timestamp");
        CREATE INDEX IF NOT EXISTS "IX_Notifications_Read" ON "Notifications" ("Read");

        CREATE TABLE IF NOT EXISTS "McpToolCalls" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "Timestamp" TEXT NOT NULL,
            "Actor" TEXT NOT NULL,
            "ActorType" TEXT NOT NULL,
            "ToolName" TEXT NOT NULL,
            "Level" TEXT NOT NULL,
            "ParamsJson" TEXT,
            "Verdict" TEXT NOT NULL,
            "Success" INTEGER NOT NULL,
            "DurationMs" INTEGER NOT NULL,
            "ResultSummary" TEXT,
            "ServerId" TEXT,
            "Error" TEXT
        );
        CREATE INDEX IF NOT EXISTS "IX_McpToolCalls_Timestamp" ON "McpToolCalls" ("Timestamp");
        CREATE INDEX IF NOT EXISTS "IX_McpToolCalls_ToolName_Timestamp" ON "McpToolCalls" ("ToolName", "Timestamp");
        CREATE INDEX IF NOT EXISTS "IX_McpToolCalls_Actor" ON "McpToolCalls" ("Actor");

        CREATE TABLE IF NOT EXISTS "CveFirstSeen" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "IdentityKey" TEXT NOT NULL,
            "CveId" TEXT NOT NULL,
            "FirstSeenUtc" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_CveFirstSeen_IdentityKey" ON "CveFirstSeen" ("IdentityKey");
        CREATE INDEX IF NOT EXISTS "IX_CveFirstSeen_CveId" ON "CveFirstSeen" ("CveId");

        CREATE TABLE IF NOT EXISTS "VolumeBackups" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "BackupId" TEXT NOT NULL,
            "VolumeName" TEXT NOT NULL,
            "ContainerName" TEXT NOT NULL,
            "ServerId" TEXT NOT NULL,
            "FileName" TEXT NOT NULL,
            "SizeBytes" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL,
            "Notes" TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_VolumeBackups_BackupId" ON "VolumeBackups" ("BackupId");
        CREATE INDEX IF NOT EXISTS "IX_VolumeBackups_ServerId_VolumeName" ON "VolumeBackups" ("ServerId", "VolumeName");
        CREATE INDEX IF NOT EXISTS "IX_VolumeBackups_CreatedAt" ON "VolumeBackups" ("CreatedAt");

        CREATE TABLE IF NOT EXISTS "ScheduledTasks" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "TaskId" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "CronExpression" TEXT NOT NULL,
            "TaskType" INTEGER NOT NULL,
            "TargetId" TEXT,
            "TargetName" TEXT,
            "ServerId" TEXT,
            "Enabled" INTEGER NOT NULL DEFAULT 1,
            "LastRun" TEXT,
            "NextRun" TEXT,
            "LastResult" TEXT,
            "LastSuccess" INTEGER NOT NULL DEFAULT 0,
            "Config" TEXT,
            "CreatedBy" TEXT NOT NULL DEFAULT '',
            "CreatedAt" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ScheduledTasks_TaskId" ON "ScheduledTasks" ("TaskId");
        CREATE INDEX IF NOT EXISTS "IX_ScheduledTasks_Enabled" ON "ScheduledTasks" ("Enabled");
        CREATE INDEX IF NOT EXISTS "IX_ScheduledTasks_NextRun" ON "ScheduledTasks" ("NextRun");

        CREATE TABLE IF NOT EXISTS "TaskRunHistory" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "TaskId" TEXT NOT NULL,
            "TaskName" TEXT NOT NULL,
            "TaskType" INTEGER NOT NULL,
            "StartedAt" TEXT NOT NULL,
            "CompletedAt" TEXT,
            "Success" INTEGER NOT NULL,
            "Output" TEXT,
            "Error" TEXT
        );
        CREATE INDEX IF NOT EXISTS "IX_TaskRunHistory_TaskId" ON "TaskRunHistory" ("TaskId");
        CREATE INDEX IF NOT EXISTS "IX_TaskRunHistory_StartedAt" ON "TaskRunHistory" ("StartedAt");

        CREATE TABLE IF NOT EXISTS "LogAlertRules" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "RuleId" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "ContainerId" TEXT,
            "ContainerName" TEXT,
            "Pattern" TEXT NOT NULL,
            "IsRegex" INTEGER NOT NULL DEFAULT 0,
            "Severity" TEXT NOT NULL DEFAULT 'warning',
            "NotifyMatrix" INTEGER NOT NULL DEFAULT 1,
            "NotifyMattermost" INTEGER NOT NULL DEFAULT 1,
            "Enabled" INTEGER NOT NULL DEFAULT 1,
            "CooldownMinutes" INTEGER NOT NULL DEFAULT 10,
            "LastTriggered" TEXT,
            "TriggerCount" INTEGER NOT NULL DEFAULT 0,
            "CreatedAt" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_LogAlertRules_RuleId" ON "LogAlertRules" ("RuleId");
        CREATE INDEX IF NOT EXISTS "IX_LogAlertRules_Enabled" ON "LogAlertRules" ("Enabled");

        CREATE TABLE IF NOT EXISTS "UpdatePolicies" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "ContainerId" TEXT NOT NULL,
            "ContainerName" TEXT NOT NULL,
            "ServerId" TEXT,
            "AutoUpdate" INTEGER NOT NULL DEFAULT 0,
            "AutoRollback" INTEGER NOT NULL DEFAULT 1,
            "NotifyBeforeUpdate" INTEGER NOT NULL DEFAULT 1,
            "CheckIntervalMinutes" INTEGER NOT NULL DEFAULT 60,
            "LastChecked" TEXT,
            "LastUpdated" TEXT,
            "LastUpdateResult" TEXT
        );

        CREATE TABLE IF NOT EXISTS "UpdateHistory" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "ContainerId" TEXT NOT NULL,
            "ContainerName" TEXT NOT NULL,
            "ServerId" TEXT,
            "OldImageDigest" TEXT NOT NULL,
            "NewImageDigest" TEXT NOT NULL,
            "Image" TEXT NOT NULL,
            "StartedAt" TEXT NOT NULL,
            "CompletedAt" TEXT,
            "Success" INTEGER NOT NULL,
            "RolledBack" INTEGER NOT NULL DEFAULT 0,
            "Error" TEXT
        );
        CREATE INDEX IF NOT EXISTS "IX_UpdateHistory_StartedAt" ON "UpdateHistory" ("StartedAt");

        CREATE TABLE IF NOT EXISTS "Webhooks" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "WebhookId" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "Secret" TEXT NOT NULL,
            "TargetType" TEXT NOT NULL DEFAULT 'container',
            "TargetId" TEXT NOT NULL,
            "Action" TEXT NOT NULL DEFAULT 'restart',
            "ServerId" TEXT,
            "Enabled" INTEGER NOT NULL DEFAULT 1,
            "CreatedAt" TEXT NOT NULL,
            "LastTriggered" TEXT,
            "TriggerCount" INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Webhooks_WebhookId" ON "Webhooks" ("WebhookId");

        CREATE TABLE IF NOT EXISTS "WebhookLogs" (
            "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            "WebhookId" TEXT NOT NULL,
            "WebhookName" TEXT NOT NULL,
            "Timestamp" TEXT NOT NULL,
            "Success" INTEGER NOT NULL,
            "Output" TEXT,
            "Error" TEXT,
            "SourceIp" TEXT
        );
        CREATE INDEX IF NOT EXISTS "IX_WebhookLogs_Timestamp" ON "WebhookLogs" ("Timestamp");
        """;
}
