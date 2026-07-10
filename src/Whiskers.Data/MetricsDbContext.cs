using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Models.Cve;

namespace Whiskers.Services.Persistence;

public class ContainerMetricEntity
{
    public long Id { get; set; }
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string ServerId { get; set; } = "local";
    public DateTime Timestamp { get; set; }
    public double CpuPercent { get; set; }
    public long MemoryUsageBytes { get; set; }
    public long MemoryLimitBytes { get; set; }
    public long NetworkRxBytes { get; set; }
    public long NetworkTxBytes { get; set; }
    public long BlockReadBytes { get; set; }
    public long BlockWriteBytes { get; set; }
}

public class ServerMetricEntity
{
    public long Id { get; set; }
    public string ServerId { get; set; } = "local";
    public string ServerName { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public double CpuPercent { get; set; }
    public long MemoryUsedBytes { get; set; }
    public long MemoryTotalBytes { get; set; }
    public long DiskUsedBytes { get; set; }
    public long DiskTotalBytes { get; set; }
}

public class AlertHistoryEntity
{
    public long Id { get; set; }
    public string ServerId { get; set; } = "local";
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string AlertType { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public bool Resolved { get; set; }
}

/// <summary>Persisted in-app notification (the bell feed + the /notifications page) so the history
/// survives restarts. Mirrors <see cref="Whiskers.Models.InAppNotification"/> plus a read flag.</summary>
public class NotificationEntity
{
    public long Id { get; set; }
    public string NotificationId { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Severity { get; set; } = "Info";
    public string? Link { get; set; }
    public bool Read { get; set; }
}

public class MetricsDbContext : DbContext
{
    public DbSet<ContainerMetricEntity> ContainerMetrics => Set<ContainerMetricEntity>();
    public DbSet<ServerMetricEntity> ServerMetrics => Set<ServerMetricEntity>();
    public DbSet<AlertHistoryEntity> AlertHistory => Set<AlertHistoryEntity>();
    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();
    public DbSet<McpToolCallEntity> McpToolCalls => Set<McpToolCallEntity>();
    public DbSet<VolumeBackupEntity> VolumeBackups => Set<VolumeBackupEntity>();
    public DbSet<ScheduledTaskEntity> ScheduledTasks => Set<ScheduledTaskEntity>();
    public DbSet<TaskRunHistoryEntity> TaskRunHistory => Set<TaskRunHistoryEntity>();
    public DbSet<LogAlertRuleEntity> LogAlertRules => Set<LogAlertRuleEntity>();
    public DbSet<UpdatePolicyEntity> UpdatePolicies => Set<UpdatePolicyEntity>();
    public DbSet<UpdateHistoryEntity> UpdateHistory => Set<UpdateHistoryEntity>();
    public DbSet<UpdateRollbackEntity> UpdateRollbacks => Set<UpdateRollbackEntity>();
    public DbSet<WebhookEntity> Webhooks => Set<WebhookEntity>();
    public DbSet<WebhookLogEntity> WebhookLogs => Set<WebhookLogEntity>();
    public DbSet<CveFirstSeenEntity> CveFirstSeen => Set<CveFirstSeenEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();

    public MetricsDbContext(DbContextOptions<MetricsDbContext> options) : base(options) { }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Keep every DateTime UTC end-to-end (stableDB.md step 2 / U2). Applies to DateTime and DateTime?.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContainerMetricEntity>(e =>
        {
            e.HasIndex(x => new { x.ContainerId, x.ServerId, x.Timestamp });
            e.HasIndex(x => x.Timestamp); // For pruning
        });

        modelBuilder.Entity<ServerMetricEntity>(e =>
        {
            e.HasIndex(x => new { x.ServerId, x.Timestamp });
            e.HasIndex(x => x.Timestamp);
        });

        modelBuilder.Entity<AlertHistoryEntity>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.ServerId, x.AlertType });
        });

        modelBuilder.Entity<AuditLogEntity>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.Action, x.Timestamp });
            e.HasIndex(x => x.Actor);
        });

        modelBuilder.Entity<McpToolCallEntity>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.ToolName, x.Timestamp });
            e.HasIndex(x => x.Actor);
        });

        modelBuilder.Entity<VolumeBackupEntity>(e =>
        {
            e.HasIndex(x => x.BackupId).IsUnique();
            e.HasIndex(x => new { x.ServerId, x.VolumeName });
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<ScheduledTaskEntity>(e =>
        {
            e.HasIndex(x => x.TaskId).IsUnique();
            e.HasIndex(x => x.Enabled);
            e.HasIndex(x => x.NextRun);
        });

        modelBuilder.Entity<TaskRunHistoryEntity>(e =>
        {
            e.HasIndex(x => x.TaskId);
            e.HasIndex(x => x.StartedAt);
        });

        modelBuilder.Entity<LogAlertRuleEntity>(e =>
        {
            e.HasIndex(x => x.RuleId).IsUnique();
            e.HasIndex(x => x.Enabled);
        });

        modelBuilder.Entity<CveFirstSeenEntity>(e =>
        {
            e.HasIndex(x => x.IdentityKey).IsUnique();
            e.HasIndex(x => x.CveId);
        });

        modelBuilder.Entity<NotificationEntity>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.Severity, x.Timestamp });
            e.HasIndex(x => x.Read);
        });

        modelBuilder.Entity<UpdateRollbackEntity>(e =>
        {
            // Access index for looking a container's snapshot up by id + server. The service keeps exactly one
            // row per (ContainerName, ServerId) — the name is the stable key, since the container id changes on
            // every update/recreate — but that upsert runs at most once per check interval, so a plain scan is
            // fine and this index just speeds id-scoped reads.
            e.HasIndex(x => new { x.ContainerId, x.ServerId });
        });
    }
}
