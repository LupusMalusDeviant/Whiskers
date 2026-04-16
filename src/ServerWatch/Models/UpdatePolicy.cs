namespace ServerWatch.Models;

public class UpdatePolicyEntity
{
    public long Id { get; set; }
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string? ServerId { get; set; }
    public bool AutoUpdate { get; set; } = false;        // OFF by default — opt-in only!
    public bool AutoRollback { get; set; } = true;        // Rollback on health-check failure
    public bool NotifyBeforeUpdate { get; set; } = true;  // Send notification before updating
    public int CheckIntervalMinutes { get; set; } = 60;
    public DateTime? LastChecked { get; set; }
    public DateTime? LastUpdated { get; set; }
    public string? LastUpdateResult { get; set; }
}

public class UpdateHistoryEntity
{
    public long Id { get; set; }
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string? ServerId { get; set; }
    public string OldImageDigest { get; set; } = "";
    public string NewImageDigest { get; set; } = "";
    public string Image { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool Success { get; set; }
    public bool RolledBack { get; set; }
    public string? Error { get; set; }
}

public class WebhookEntity
{
    public long Id { get; set; }
    public string WebhookId { get; set; } = Guid.NewGuid().ToString("N")[..16];
    public string Name { get; set; } = "";
    public string Secret { get; set; } = Guid.NewGuid().ToString("N");  // HMAC secret
    public string TargetType { get; set; } = "container"; // container, compose
    public string TargetId { get; set; } = "";         // Container name or compose project dir
    public string Action { get; set; } = "restart";    // restart, rebuild, deploy
    public string? ServerId { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastTriggered { get; set; }
    public int TriggerCount { get; set; }
}

public class WebhookLogEntity
{
    public long Id { get; set; }
    public string WebhookId { get; set; } = "";
    public string WebhookName { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public string? SourceIp { get; set; }
}
