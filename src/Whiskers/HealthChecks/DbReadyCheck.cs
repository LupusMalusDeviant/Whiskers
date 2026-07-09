using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whiskers.Services.Persistence;

namespace Whiskers.HealthChecks;

/// <summary>
/// Readiness probe: verifies the metrics database is reachable. Resolves a fresh
/// <see cref="MetricsDbContext"/> per probe (the context is registered Transient) through a scope.
/// Registered with the <c>ready</c> tag so it gates <c>/readyz</c> but not the <c>/healthz</c>
/// liveness endpoint.
/// </summary>
public sealed class DbReadyCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DbReadyCheck(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Metrics database is not reachable.");
        }
        catch (Exception ex)
        {
            // Description/exception are never written to the HTTP response (the endpoints use a
            // status-only writer); they are available to server-side health-check logging only.
            return HealthCheckResult.Unhealthy("Metrics database check failed.", ex);
        }
    }
}
