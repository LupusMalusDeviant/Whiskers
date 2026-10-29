using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whiskers.Services.Mutations;

namespace Whiskers.HealthChecks;

/// <summary>
/// Readiness: <c>Degraded</c> while the audit trail is incomplete (R1-19) — an outcome or refusal still waits to be
/// written, an intent row could not be settled, or the older after-the-fact audit lost a row in the last hour. Never
/// <c>Unhealthy</c>: Whiskers keeps serving, and a mutation still runs only after its intent is on record.
/// </summary>
public sealed class AuditTrailCheck(IAuditRecovery recovery) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        AuditHealth health;
        try
        {
            health = recovery.Health;
        }
        catch (Exception)
        {
            // Not knowing is not complete — but still only Degraded.
            return Task.FromResult(HealthCheckResult.Degraded("Audit trail state unknown."));
        }
        return Task.FromResult(health.Complete
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(
                $"Audit trail incomplete: {health.PendingEvents} event(s) waiting to be written or lost, " +
                $"{health.UnsettledIntents} intent(s) unsettled, older audit write failed: {health.LegacyWriteFailed}."));
    }
}
