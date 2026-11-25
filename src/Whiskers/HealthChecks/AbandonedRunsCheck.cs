using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Whiskers.Services.Mutations;

namespace Whiskers.HealthChecks;

/// <summary>
/// Readiness (contract 0023, D5): <c>Degraded</c> once a mutation has been running past its deadline for longer than
/// <see cref="MutationGatewayOptions.AbandonedLimit"/>. Never <c>Unhealthy</c>: Whiskers stays usable, and only targets
/// that overlap the run are refused. Fix the host, or restart Whiskers; the restart reports the run as interrupted.
/// </summary>
public sealed class AbandonedRunsCheck(IAbandonedRuns runs, IOptions<MutationGatewayOptions> options,
    TimeProvider? time = null) : IHealthCheck
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var limit = options.Value.AbandonedLimit;
        IReadOnlyList<AbandonedRun> running;
        try
        {
            running = runs.Running;
        }
        catch (Exception)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Abandoned mutations could not be read."));
        }
        var now = _time.GetUtcNow();
        var overdue = running.Count(r => now - r.Since > limit);
        return Task.FromResult(overdue == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(
                $"A mutation has been running past its deadline for over {limit.TotalMinutes:0} min ({overdue} run(s)); " +
                "fix the host or restart Whiskers."));
    }
}
