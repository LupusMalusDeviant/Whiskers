namespace ServerWatch.Models;

public class ImageUpdateInfo
{
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string LocalDigest { get; set; } = string.Empty;
    public string RemoteDigest { get; set; } = string.Empty;
    public bool UpdateAvailable { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public string ServerId { get; set; } = "local";
    public string? Error { get; set; }
}
