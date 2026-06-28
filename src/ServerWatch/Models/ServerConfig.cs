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

public enum MetricsSourceKind
{
    // Legacy pull: container stats via Docker API, host metrics via SSH /proc-exec.
    Docker,
    // Read from a Prometheus-compatible TSDB (VictoriaMetrics) fed by node_exporter/cAdvisor.
    // Keeps the SSH key out of the metrics hot path — see
    // docs/plan-zero-ssh-telemetry-dockerapi.md.
    Prometheus
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
    // mTLS for the TCP path (e.g. a docker-socket-proxy fronted by ghostunnel over the mesh).
    // When TcpUseTls is set and these point at PEM files, ServerWatch presents the client cert and
    // verifies the server against the CA — no SSH key involved. See docs/plan-zero-ssh-...md (Step 2).
    public string? TcpClientCertPath { get; set; }
    public string? TcpClientKeyPath { get; set; }
    public string? TcpCaCertPath { get; set; }

    // Tailscale SSH: keyless, mesh-identity shell for the interactive web terminal. When set on a
    // TCP/mTLS server (whose Docker plane is SSH-free), the terminal reaches the host via
    // `ssh root@<TcpHost>` over the tailnet — Tailscale's node identity does the auth (no standing
    // SSH key on disk), gated by the tailnet ACL ssh rules. The mTLS Docker proxy can't carry an
    // interactive attach stream, so this is how the SSH-free hosts get a real PTY back.
    public bool TailscaleSsh { get; set; }

    // SSH
    public string? SshHost { get; set; }
    public int SshPort { get; set; } = 22;
    public string? SshUser { get; set; }
    public string? SshKeyFileName { get; set; }

    // Transient root/SSH password used ONLY for a one-time onboarding bootstrap (e.g. a fresh VPS that
    // only offers root+password). Held in memory, never written to servers.json, and cleared after a
    // successful onboarding (which switches the host to SSH-free mTLS).
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SshPassword { get; set; }

    // Telemetry: where the metrics collector reads this server's metrics from.
    // MetricsEndpoint is the Prometheus-compatible query base URL (e.g. the VictoriaMetrics
    // instance http://<mesh-ip>:8428); the server is matched in queries by the `server` label
    // carrying this server's Name. Defaults preserve the legacy Docker/SSH behaviour.
    public MetricsSourceKind MetricsSource { get; set; } = MetricsSourceKind.Docker;
    public string? MetricsEndpoint { get; set; }

    // Cloud provider control (out-of-band power/snapshot via provider API).
    // The API key is per-server: Hetzner tokens are per-project and Hostinger keys per-account,
    // so each server carries the credential for whatever account/project it lives in.
    public CloudProvider CloudProvider { get; set; } = CloudProvider.None;
    public string? CloudApiKey { get; set; }

    public bool IsDefault { get; set; }
    public bool Enabled { get; set; } = true;

    // Shallow copy for the edit-form dialog. All members are value types or strings, so MemberwiseClone
    // is safe — and using it (instead of a hand-listed `new ServerConfig { ... }`) guarantees that
    // newly-added fields can never be silently dropped when a server is edited and saved.
    public ServerConfig Clone() => (ServerConfig)MemberwiseClone();
}

public class ServerConfigData
{
    public List<ServerConfig> Servers { get; set; } = new();
}
