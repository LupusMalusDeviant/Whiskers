namespace Whiskers.Services.Vpn.Providers;

/// <summary>
/// No-op provider: the mesh VPN is managed outside the container (on the host or in a sidecar), so
/// Whiskers just uses the resulting mesh IPs. This is the decoupled default and the posture that
/// makes a distroless image possible (no VPN daemon baked into the app image).
/// </summary>
public class NoopVpnProvider : IVpnProvider
{
    /// <summary>The id of the "managed outside the container" provider.</summary>
    public const string ProviderId = "none";

    public string Id => ProviderId;
    public string DisplayName => "Kein (Host/Sidecar)";

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<VpnBringUpResult> EnsureUpAsync(CancellationToken ct = default) =>
        Task.FromResult(new VpnBringUpResult(VpnBringUpState.HostManaged, "The VPN is managed outside the container."));
    public Task<VpnStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(VpnStatus.Disconnected("VPN außerhalb des Containers verwaltet (Host/Sidecar)"));
    public Task DownAsync(CancellationToken ct = default) => Task.CompletedTask;
}
