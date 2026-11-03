using Whiskers.Services.Mutations;

namespace Whiskers.Services.Vpn;

/// <summary>
/// A mesh-VPN backend (Tailscale, NetBird, …). Decouples Whiskers from any single VPN: the
/// daemon bring-up that used to live in entrypoint.sh moves behind this seam so the image is no
/// longer hard-wired to Tailscale, and a "none" provider lets the VPN run on the host / a sidecar
/// instead — which also unblocks a distroless image down the line.
/// </summary>
public interface IVpnProvider
{
    /// <summary>Stable id, e.g. "tailscale", "netbird", "none". Matched against <see cref="VpnSettings.Provider"/>.</summary>
    string Id { get; }

    /// <summary>Display name for the UI, e.g. "Tailscale".</summary>
    string DisplayName { get; }

    /// <summary>Whether the backend's tooling is present and usable in this environment.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Brings the mesh connection up using the configured credentials (R1-43): starts the daemon if needed
    /// and joins the mesh. Only the Mutation Gateway calls it (<c>vpn.bootstrap</c>, <see cref="VpnBootstrapHostedService"/>).
    /// Returns what happened; a result that is not <see cref="VpnBringUpResult.Success"/> is recorded as failed. The
    /// result's message never carries the enrollment key.</summary>
    [MutatingOperation("vpn.bootstrap")]
    Task<VpnBringUpResult> EnsureUpAsync(CancellationToken ct = default);

    /// <summary>Current connection status.</summary>
    Task<VpnStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Disconnect from the mesh (does not necessarily stop the daemon). It has no caller today; it changes
    /// the mesh, so it needs a capability of its own (<c>[MutatingOperation]</c> + catalog entry, decided by the
    /// gateway) before anything calls it (R1-43).</summary>
    Task DownAsync(CancellationToken ct = default);
}

/// <summary>How a bring-up ended.</summary>
public enum VpnBringUpState
{
    /// <summary>The node joined the mesh.</summary>
    Connected,
    /// <summary>The VPN is managed outside the container (host or sidecar); nothing was started.</summary>
    HostManaged,
    /// <summary>The CLI is not installed in the image.</summary>
    NotInstalled,
    /// <summary>A configured value cannot become a command argument (hostname, URL); nothing ran.</summary>
    InvalidSetting,
    /// <summary>The daemon runs, but no key was configured and it is not logged in.</summary>
    NeedsLogin,
    /// <summary>The CLI reported a failure.</summary>
    Failed,
}

/// <summary>The outcome of <see cref="IVpnProvider.EnsureUpAsync"/>. <see cref="Message"/> is safe to log and audit:
/// providers scrub the CLI output of the key before it gets here.</summary>
public sealed record VpnBringUpResult(VpnBringUpState State, string Message)
{
    /// <summary>The mesh is usable: joined, or managed outside the container.</summary>
    public bool Success => State is VpnBringUpState.Connected or VpnBringUpState.HostManaged;
}

/// <summary>Snapshot of a VPN provider's connection state.</summary>
public record VpnStatus(
    bool Connected,
    string? State,
    string? Hostname,
    IReadOnlyList<string> Addresses,
    string? Message)
{
    public static VpnStatus Disconnected(string? message = null) =>
        new(false, null, null, Array.Empty<string>(), message);
}
