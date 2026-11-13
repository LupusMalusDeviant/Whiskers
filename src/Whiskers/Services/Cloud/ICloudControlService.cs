using Whiskers.Services.Mutations;
using Whiskers.Models.Cloud;
using Whiskers.Models.Hetzner;

namespace Whiskers.Services.Cloud;

/// <summary>Provider-agnostic control plane for cloud servers (power, snapshots, metrics) over the
/// per-server cloud credentials (Hetzner/Hostinger).</summary>
public interface ICloudControlService
{
    [Whiskers.Services.Mutations.InventoryLookup]
    List<Whiskers.Models.ServerConfig> CloudServers();
    Whiskers.Models.ServerConfig? ResolveWhiskers(string idOrName);
    // INV-40: the reads take the caller's token (a read tool's); it reaches the provider's HTTP calls.
    Task<CloudServerInfo?> ResolveAsync(Whiskers.Models.ServerConfig sw, CancellationToken ct = default);
    Task<List<CloudServerInfo>> ListAllAsync(CancellationToken ct = default);
    [MutatingOperation("cloud.power_on")]
    Task<string> PowerOnAsync([MutationTarget] string idOrName);
    [MutatingOperation("cloud.shutdown")]
    Task<string> ShutdownAsync([MutationTarget] string idOrName);
    [MutatingOperation("cloud.reboot")]
    Task<string> RebootAsync([MutationTarget] string idOrName);
    [MutatingOperation("cloud.hard_reset")]
    Task<string> HardResetAsync([MutationTarget] string idOrName);
    [MutatingOperation("cloud.snapshot_create")]
    Task<string> CreateSnapshotAsync([MutationTarget] string idOrName, string? description);
    Task<string> MetricsAsync(string idOrName, string type, CancellationToken ct = default);
    Task<(string token, HetznerServer server)?> HetznerContextAsync(string idOrName, CancellationToken ct = default);
}
