namespace ServerWatch.Models;

public class ContainerInfo
{
    public string Id { get; set; } = string.Empty;
    public string ShortId => Id.Length >= 12 ? Id[..12] : Id;
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public string HealthStatus { get; set; } = "none";
    public Dictionary<string, string> Labels { get; set; } = new();
    public List<PortMapping> Ports { get; set; } = new();
    public ContainerStats? LatestStats { get; set; }
    public string ServerId { get; set; } = "local";
    public string ServerName { get; set; } = "Local";
    public string ComposeProject => Labels.TryGetValue("com.docker.compose.project", out var p) ? p : "Standalone";

    public DatabaseType DatabaseType => Services.Database.DatabaseDetector.DetectType(Image);
    public bool IsDatabase => DatabaseType != DatabaseType.None;
}

public class PortMapping
{
    public string IP { get; set; } = string.Empty;
    public ushort PrivatePort { get; set; }
    public ushort PublicPort { get; set; }
    public string Type { get; set; } = "tcp";

    public override string ToString() =>
        PublicPort > 0 ? $"{IP}:{PublicPort}->{PrivatePort}/{Type}" : $"{PrivatePort}/{Type}";
}
