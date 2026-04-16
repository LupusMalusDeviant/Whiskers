namespace ServerWatch.Models;

public class HealthRecord
{
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string State { get; set; } = string.Empty;
    public string HealthStatus { get; set; } = "none";
    public int? ExitCode { get; set; }
    public bool OomKilled { get; set; }
}
