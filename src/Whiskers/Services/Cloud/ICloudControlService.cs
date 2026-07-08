using Whiskers.Models.Cloud;
using Whiskers.Models.Hetzner;

namespace Whiskers.Services.Cloud;

/// <summary>Provider-agnostic control plane for cloud servers (power, snapshots, metrics) over the
/// per-server cloud credentials (Hetzner/Hostinger).</summary>
public interface ICloudControlService
{
    List<Whiskers.Models.ServerConfig> CloudServers();
    Whiskers.Models.ServerConfig? ResolveWhiskers(string idOrName);
    Task<CloudServerInfo?> ResolveAsync(Whiskers.Models.ServerConfig sw);
    Task<List<CloudServerInfo>> ListAllAsync();
    Task<string> PowerOnAsync(string idOrName);
    Task<string> ShutdownAsync(string idOrName);
    Task<string> RebootAsync(string idOrName);
    Task<string> HardResetAsync(string idOrName);
    Task<string> CreateSnapshotAsync(string idOrName, string? description);
    Task<string> MetricsAsync(string idOrName, string type);
    Task<(string token, HetznerServer server)?> HetznerContextAsync(string idOrName);
}
