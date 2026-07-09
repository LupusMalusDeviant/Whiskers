using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whiskers.Services.ServerConfig;

namespace Whiskers.HealthChecks;

/// <summary>
/// Readiness probe: verifies the server-config registry finished loading at startup
/// (<see cref="IServerConfigService.IsInitialized"/>). Registered with the <c>ready</c> tag so it
/// gates <c>/readyz</c> but not the <c>/healthz</c> liveness endpoint.
/// </summary>
public sealed class ServerConfigReadyCheck : IHealthCheck
{
    private readonly IServerConfigService _serverConfig;

    public ServerConfigReadyCheck(IServerConfigService serverConfig) => _serverConfig = serverConfig;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(_serverConfig.IsInitialized
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Server configuration has not finished initializing."));
}
