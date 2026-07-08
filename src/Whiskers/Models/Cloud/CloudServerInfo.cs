namespace Whiskers.Models.Cloud;

/// <summary>
/// Provider-agnostic view of a cloud server, resolved by matching a Whiskers server's IP to a
/// VM/server in the configured provider account.
/// </summary>
public class CloudServerInfo
{
    public string WhiskersId { get; set; } = "";
    public string WhiskersName { get; set; } = "";
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
