namespace ServerWatch.Services.Vpn;

/// <summary>
/// A mesh-VPN backend (Tailscale, NetBird, …). Decouples ServerWatch from any single VPN: the
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

    /// <summary>Bring the mesh connection up using the configured credentials. Best-effort; logs and returns on failure.</summary>
    Task EnsureUpAsync(CancellationToken ct = default);

    /// <summary>Current connection status.</summary>
    Task<VpnStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Disconnect from the mesh (does not necessarily stop the daemon).</summary>
    Task DownAsync(CancellationToken ct = default);
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
