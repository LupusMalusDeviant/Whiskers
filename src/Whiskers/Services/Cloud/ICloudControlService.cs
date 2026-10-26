using Whiskers.Services.Mutations;
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
    [MutatingOperation("cloud.power_on")]
    Task<string> PowerOnAsync(string idOrName);
    [MutatingOperation("cloud.shutdown")]
    Task<string> ShutdownAsync(string idOrName);
    [MutatingOperation("cloud.reboot")]
    Task<string> RebootAsync(string idOrName);
    [MutatingOperation("cloud.hard_reset")]
    Task<string> HardResetAsync(string idOrName);
    [MutatingOperation("cloud.snapshot_create")]
    Task<string> CreateSnapshotAsync(string idOrName, string? description);
    Task<string> MetricsAsync(string idOrName, string type);
    Task<(string token, HetznerServer server)?> HetznerContextAsync(string idOrName);
}
