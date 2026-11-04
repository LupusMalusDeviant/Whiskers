using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whiskers.Services.Auth;

namespace Whiskers.HealthChecks;

/// <summary>
/// Readiness (R1-27): <c>Degraded</c> while <c>identity-bindings.json</c> cannot be read — every Google/OIDC sign-in and
/// session is refused until it is repaired, local sign-in works. Never <c>Unhealthy</c>: Whiskers keeps serving.
/// </summary>
public sealed class IdentityBindingsReadyCheck(IIdentityBindingStore bindings) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        bool available;
        try { available = bindings.IsAvailable; }
        catch (Exception) { available = false; }
        return Task.FromResult(available
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("The identity bindings cannot be read: Google/OIDC sign-in is refused."));
    }
}
