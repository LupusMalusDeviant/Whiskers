using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whiskers.Services.Maintenance;

namespace Whiskers.HealthChecks;

/// <summary>
/// Readiness probe that reports <see cref="HealthCheckResult.Unhealthy(string?,Exception?,IReadOnlyDictionary{string,object}?)"/>
/// while the app is in maintenance (the F3 restore window), so load balancers / orchestrators drain traffic
/// away from an instance that is about to restart. Tagged <c>ready</c> → it gates <c>/readyz</c> only, NOT the
/// <c>/healthz</c> liveness endpoint that the container HEALTHCHECK probes — so flipping to maintenance never
/// makes Docker kill the container mid-restore.
/// </summary>
public sealed class MaintenanceReadyCheck : IHealthCheck
{
    private readonly IMaintenanceStateService _maintenance;

    public MaintenanceReadyCheck(IMaintenanceStateService maintenance) => _maintenance = maintenance;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(_maintenance.IsMaintenance
            ? HealthCheckResult.Unhealthy(_maintenance.Reason ?? "Maintenance in progress.")
            : HealthCheckResult.Healthy());
}
