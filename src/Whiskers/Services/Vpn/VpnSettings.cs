namespace Whiskers.Services.Vpn;

/// <summary>
/// Mesh-VPN configuration (bound from the "Vpn" config section / Vpn__* env vars).
/// Defaults to "none" so existing deployments — where entrypoint.sh brings Tailscale up — keep
/// working unchanged; set <see cref="Provider"/> to let the app manage the VPN instead.
/// </summary>
public class VpnSettings
{
    public const string SectionName = "Vpn";

    /// <summary>Active provider: "none" (VPN on host/sidecar), "tailscale", or "netbird".</summary>
    public string Provider { get; set; } = "none";

    /// <summary>Hostname to register on the mesh. Falls back to the container hostname.</summary>
    public string? Hostname { get; set; }

    /// <summary>Accept subnet routes advertised by peers.</summary>
    public bool AcceptRoutes { get; set; } = true;

    public TailscaleOptions Tailscale { get; set; } = new();
    public NetbirdOptions Netbird { get; set; } = new();

    public class TailscaleOptions
    {
        /// <summary>Auth key for unattended login (tskey-…). Empty = interactive/manual login.</summary>
        public string? AuthKey { get; set; }
        public string StateDir { get; set; } = "/app/data/tailscale";
        public string Socket { get; set; } = "/var/run/tailscale/tailscaled.sock";
        /// <summary>Optional self-hosted control plane (Headscale), e.g. "https://headscale.example.com".</summary>
        public string? LoginServer { get; set; }
    }

    public class NetbirdOptions
    {
        /// <summary>Setup key for unattended enrollment. Empty = interactive/manual.</summary>
        public string? SetupKey { get; set; }
        /// <summary>Optional self-hosted management URL, e.g. "https://netbird.example.com:33073".</summary>
        public string? ManagementUrl { get; set; }
        public string ConfigPath { get; set; } = "/app/data/netbird/config.json";
    }
}
