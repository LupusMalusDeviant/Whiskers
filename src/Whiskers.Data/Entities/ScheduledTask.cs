namespace Whiskers.Models;

public enum ScheduledTaskType
{
    ContainerRestart,
    DbBackup,
    VolumeBackup,
    CustomCommand,
    Cleanup,
    // F3: schedule a Whiskers self-backup of /app/data. MUST stay last — TaskType is persisted as an INT
    // (see DatabaseInitializer schema), so inserting a member mid-list would renumber existing rows.
    SelfBackup
}

public class ScheduledTaskEntity
{
    public long Id { get; set; }
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string CronExpression { get; set; } = ""; // NCrontab format: "0 2 * * *"
    public ScheduledTaskType TaskType { get; set; }
    public string? TargetId { get; set; }       // Container ID, Volume name, etc.
    public string? TargetName { get; set; }     // Human-readable target
    public string? ServerId { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime? LastRun { get; set; }
    public DateTime? NextRun { get; set; }
    public string? LastResult { get; set; }     // "success" or error message
    public bool LastSuccess { get; set; }
    public string? Config { get; set; }         // JSON: {"retainDays": 7, "maxBackups": 10, "command": "...", "database": "..."}
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TaskRunHistoryEntity
{
    public long Id { get; set; }
    public string TaskId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public ScheduledTaskType TaskType { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
}
