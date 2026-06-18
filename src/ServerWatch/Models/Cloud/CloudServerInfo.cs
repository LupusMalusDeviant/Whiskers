namespace ServerWatch.Models.Cloud;

/// <summary>
/// Provider-agnostic view of a cloud server, resolved by matching a ServerWatch server's IP to a
/// VM/server in the configured provider account.
/// </summary>
public class CloudServerInfo
{
    public string ServerWatchId { get; set; } = "";
    public string ServerWatchName { get; set; } = "";
    public CloudProvider Provider { get; set; }
    public long CloudId { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Ipv4 { get; set; }
    public string? Type { get; set; }
    public string? Location { get; set; }
    public double? TrafficPercent { get; set; }   // Hetzner only
    public bool BackupsEnabled { get; set; }       // Hetzner only
    public string? Note { get; set; }
}
