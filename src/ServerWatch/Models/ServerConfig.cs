namespace ServerWatch.Models;

public enum ConnectionType
{
    Local,
    TCP,
    SSH
}

public enum CloudProvider
{
    None,
    Hetzner,
    Hostinger
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

    // Cloud provider control (out-of-band power/snapshot via provider API).
    // The API key is per-server: Hetzner tokens are per-project and Hostinger keys per-account,
    // so each server carries the credential for whatever account/project it lives in.
    public CloudProvider CloudProvider { get; set; } = CloudProvider.None;
    public string? CloudApiKey { get; set; }

    public bool IsDefault { get; set; }
    public bool Enabled { get; set; } = true;
}

public class ServerConfigData
{
    public List<ServerConfig> Servers { get; set; } = new();
}
