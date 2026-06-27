using Microsoft.EntityFrameworkCore;
using ServerWatch.Models;
using ServerWatch.Models.Cve;

namespace ServerWatch.Services.Persistence;

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
    public DbSet<WebhookEntity> Webhooks => Set<WebhookEntity>();
    public DbSet<WebhookLogEntity> WebhookLogs => Set<WebhookLogEntity>();
    public DbSet<CveFirstSeenEntity> CveFirstSeen => Set<CveFirstSeenEntity>();

    public MetricsDbContext(DbContextOptions<MetricsDbContext> options) : base(options) { }

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
    }
}
