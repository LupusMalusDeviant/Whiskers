namespace ServerWatch.Models;

public enum ConnectionType
{
    Local,
    TCP,
    SSH
}

public enum VpnType
{
    None,
    Tailscale,
    WireGuard,
    OpenVPN,
    Other
}

public class ServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "Local";
    public ConnectionType ConnectionType { get; set; } = ConnectionType.Local;

    // Local
    public string SocketPath { get; set; } = "unix:///var/run/docker.sock";

    // TCP
    public string? TcpHost { get; set; }
    public int TcpPort { get; set; } = 2375;
    public bool TcpUseTls { get; set; }

    // SSH
    public string? SshHost { get; set; }
    public int SshPort { get; set; } = 22;
    public string? SshUser { get; set; }
    public string? SshKeyFileName { get; set; }

    public bool IsDefault { get; set; }
    public bool Enabled { get; set; } = true;

    // VPN
    public VpnType VpnType { get; set; } = VpnType.None;
    public string? VpnNote { get; set; }
    public string? TailscaleIP { get; set; } // 100.x.x.x — used when VpnType == Tailscale

    /// <summary>Effective SSH host — prefers TailscaleIP when VPN is Tailscale.</summary>
    public string? EffectiveSshHost => VpnType == VpnType.Tailscale && !string.IsNullOrEmpty(TailscaleIP)
        ? TailscaleIP
        : SshHost;
}

public class ServerConfigData
{
    public List<ServerConfig> Servers { get; set; } = new();
}
