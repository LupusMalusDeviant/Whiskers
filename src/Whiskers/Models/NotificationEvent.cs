namespace Whiskers.Models;

public class NotificationEvent
{
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string ImageName { get; set; } = string.Empty;
    public string? ImageInfo { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
    public int? RestartCount { get; set; }
    public int? WindowMinutes { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
