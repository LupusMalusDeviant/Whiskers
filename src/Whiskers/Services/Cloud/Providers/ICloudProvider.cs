using Whiskers.Models;
using Whiskers.Models.Cloud;

namespace Whiskers.Services.Cloud.Providers;

/// <summary>A cloud/VPS provider behind the CloudControl seam (RoadToSAP §3.6 / changeme C10), modelled on
/// <c>IVpnProvider</c>. Each provider resolves a Whiskers server to its VM in the account (public IP, then
/// name), exposes the provider-agnostic power/snapshot/metric operations, and formats its own result messages
/// — kept <b>byte-identical</b> to the previous inline <c>if Hetzner … else Hostinger</c> dispatch.
/// <see cref="CloudControlService"/> selects the provider by <see cref="Whiskers.Models.ServerConfig.CloudProvider"/>
/// via multi-registration, so there is no hard enum-switch per action and a new provider is a new registration.</summary>
public interface ICloudProvider
{
    /// <summary>The provider this handles; matched against <c>ServerConfig.CloudProvider</c>. The enum stays the
    /// persisted key (servers.json unchanged); the seam replaces the per-action switch, not the storage format.</summary>
    CloudProvider Provider { get; }

    /// <summary>Display name for the UI, e.g. "Hetzner".</summary>
    string DisplayName { get; }

    Task<bool> TestConnectionAsync(string token, CancellationToken ct = default);

    /// <summary>List the account ONCE and map each given Whiskers server to a <see cref="CloudServerInfo"/>
    /// (public IP, then name). A no-match is omitted. Batch op so an account with N servers is listed once.</summary>
    Task<List<CloudServerInfo>> ListAndMapAsync(IReadOnlyList<Whiskers.Models.ServerConfig> accountServers, string token, CancellationToken ct = default);

    /// <summary>Resolve a single Whiskers server to its VM in the account (public IP, then name).</summary>
    Task<CloudServerInfo?> ResolveAsync(Whiskers.Models.ServerConfig sw, string token, CancellationToken ct = default);

    // Power/snapshot/metric ops for an already-resolved target. Each returns the exact result message; the
    // caller (CloudControlService) appends the weak-resolution (IP-match) note exactly where it did before. The
    // changing ones carry the capability of the CloudControlService operation they execute (R1-48), so a provider runs
    // only inside that decision.
    [Whiskers.Services.Mutations.MutatingOperation("cloud.power_on")]
    Task<string> PowerOnAsync(CloudServerInfo target, string token, CancellationToken ct = default);
    [Whiskers.Services.Mutations.MutatingOperation("cloud.shutdown")]
    Task<string> ShutdownAsync(CloudServerInfo target, string token, CancellationToken ct = default);
    [Whiskers.Services.Mutations.MutatingOperation("cloud.reboot")]
    Task<string> RebootAsync(CloudServerInfo target, string token, CancellationToken ct = default);
    [Whiskers.Services.Mutations.MutatingOperation("cloud.hard_reset")]
    Task<string> HardResetAsync(CloudServerInfo target, string token, CancellationToken ct = default);
    [Whiskers.Services.Mutations.MutatingOperation("cloud.snapshot_create")]
    Task<string> CreateSnapshotAsync(CloudServerInfo target, string token, string? description, CancellationToken ct = default);
    Task<string> MetricsAsync(CloudServerInfo target, string token, string type, CancellationToken ct = default);
}
