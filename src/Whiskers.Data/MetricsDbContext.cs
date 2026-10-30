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
    /// <summary>The audit hash chain and its signed checkpoints (R1-23, ADR-0006).</summary>
    public DbSet<AuditChainEntry> AuditChain => Set<AuditChainEntry>();
    public DbSet<AuditCheckpointEntity> AuditCheckpoints => Set<AuditCheckpointEntity>();
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

    /// <summary>Accepted webhook deliveries by delivery key: a delivery runs at most once (R1-10, INV-42).</summary>
    public DbSet<WebhookDeliveryEntity> WebhookDeliveries => Set<WebhookDeliveryEntity>();
    public DbSet<CveFirstSeenEntity> CveFirstSeen => Set<CveFirstSeenEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();

    /// <summary>Minute-by-minute readings of Whiskers' own loop health (Plan-0003 WP3.2).</summary>
    public DbSet<SelfMetricSampleEntity> SelfMetricSamples => Set<SelfMetricSampleEntity>();

    /// <summary>Automatic actions and whether they actually achieved anything (Plan-0006 WP2).</summary>
    public DbSet<ActionOutcomeEntity> ActionOutcomes => Set<ActionOutcomeEntity>();

    /// <summary>Every policy decision of the Mutation Gateway, written before the mutation runs (INV-22, R1-12).</summary>
    public DbSet<PolicyDecisionEntity> PolicyDecisions => Set<PolicyDecisionEntity>();

    /// <summary>Immutable policy versions the decisions were made under, keyed by content hash (R1-12).</summary>
    public DbSet<PolicySnapshotEntity> PolicySnapshots => Set<PolicySnapshotEntity>();

    /// <summary>Human-in-the-Loop approvals with their decision and single use, never deleted (R1-14).</summary>
    public DbSet<ApprovalRecordEntity> Approvals => Set<ApprovalRecordEntity>();

    /// <summary>The execution claim of every mutation, by operation id, never deleted (R1-18, INV-39).</summary>
    public DbSet<OperationRecordEntity> Operations => Set<OperationRecordEntity>();

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
            // R1-19: an operation's event, and the intents still waiting for their outcome.
            e.HasIndex(x => x.OperationId);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.Phase, x.Timestamp });
        });

        modelBuilder.Entity<AuditChainEntry>(e =>
        {
            // The appender assigns the position: it is hashed, so the database must not choose it.
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedNever();
            e.HasIndex(x => x.AuditId);
        });

        modelBuilder.Entity<AuditCheckpointEntity>(e =>
        {
            e.HasIndex(x => new { x.Kind, x.Seq });
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

        modelBuilder.Entity<ActionOutcomeEntity>(e =>
        {
            // The due-window sweep asks "what is pending and due?" every minute, so that pair leads. The
            // correlation index carries the trigger → action → outcome chain.
            e.HasIndex(x => new { x.Verdict, x.DueAtUtc });
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => new { x.ActionKind, x.ExecutedAtUtc });
            e.Property(x => x.Verdict).HasConversion<string>();
        });

        modelBuilder.Entity<SelfMetricSampleEntity>(e =>
        {
            // Queried two ways: "the latest reading per loop and server" on boot, and "this loop's history"
            // for the view. Both start from the timestamp, so it leads the composite index.
            e.HasIndex(x => x.TakenAtUtc);
            e.HasIndex(x => new { x.Loop, x.ServerId, x.TakenAtUtc });
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

        modelBuilder.Entity<PolicyDecisionEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.OperationId);
            e.HasIndex(x => new { x.PrincipalId, x.Timestamp });
            e.HasIndex(x => x.PolicyVersion);
        });

        modelBuilder.Entity<PolicySnapshotEntity>(e =>
        {
            e.HasKey(x => x.VersionHash);
        });

        modelBuilder.Entity<ApprovalRecordEntity>(e =>
        {
            e.ToTable("Approvals");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.Binding);
            e.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<OperationRecordEntity>(e =>
        {
            e.ToTable("Operations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.ClaimedAt);
        });

        modelBuilder.Entity<WebhookEntity>(e =>
            e.Property(x => x.Provider).HasDefaultValue("github"));

        modelBuilder.Entity<WebhookDeliveryEntity>(e =>
        {
            e.ToTable("WebhookDeliveries");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.WebhookId, x.DeliveryKey }).IsUnique();
            e.HasIndex(x => x.RunId).IsUnique();
            e.HasIndex(x => x.ReceivedAt);
        });
    }
}
