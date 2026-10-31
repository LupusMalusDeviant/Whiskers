namespace Whiskers.Models;

public class NotificationEvent
{
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string ImageName { get; set; } = string.Empty;
    public string? ImageInfo { get; set; }
    public string EventType { get; set; } = string.Empty;
    // Which host the event happened on. Container names repeat across a fleet, so an alert without the
    // server is ambiguous; also the structured column the persisted alert history is keyed by.
    public string? ServerId { get; set; }
    public string? ServerName { get; set; }
    public int? ExitCode { get; set; }
    public int? RestartCount { get; set; }
    public int? WindowMinutes { get; set; }
    /// <summary>WP-05: for agent-approval events, ties the notification to the approval + history entry.</summary>
    public string? CorrelationId { get; set; }
    /// <summary>R1-15: for agent-approval events, the approval to open (the in-app link goes to its card).</summary>
    public string? ApprovalId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>A shallow copy (every member is a value or an immutable string).</summary>
    public NotificationEvent Copy() => (NotificationEvent)MemberwiseClone();
}
