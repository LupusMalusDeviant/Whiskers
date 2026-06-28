namespace ServerWatch.Services.Vpn;

/// <summary>
/// On startup, brings the active VPN provider up — replacing the Tailscale bring-up that used to
/// live in entrypoint.sh. With the default "none" provider this is a no-op (the VPN runs on the
/// host/sidecar, or entrypoint.sh still handles it for legacy deployments).
/// </summary>
public class VpnBootstrapHostedService : IHostedService
{
    private readonly IVpnService _vpn;
    private readonly ILogger<VpnBootstrapHostedService> _logger;

    public VpnBootstrapHostedService(IVpnService vpn, ILogger<VpnBootstrapHostedService> logger)
    {
        _vpn = vpn;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var provider = _vpn.Active;
        if (provider.Id == "none")
        {
            _logger.LogInformation("[vpn] provider=none — VPN managed outside the container");
            return;
        }

        _logger.LogInformation("[vpn] bringing up provider '{Provider}'", provider.Id);
        try
        {
            await provider.EnsureUpAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Never let VPN bring-up crash startup — connectivity issues are recoverable at runtime.
            _logger.LogError(ex, "[vpn] bring-up failed for provider '{Provider}'", provider.Id);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
