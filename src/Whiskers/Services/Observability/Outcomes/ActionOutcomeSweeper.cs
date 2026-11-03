namespace Whiskers.Services.Observability.Outcomes;

/// <summary>
/// Judges the check windows that have come due (Plan-0006 WP2).
///
/// <para>A minute apart, because the shortest window is five minutes and a sweep that runs more often than
/// the windows close is just database traffic. Each pass reads existing series and writes a verdict; it takes
/// no measurements of its own and touches no server.</para>
///
/// <para><b>Observing only, deliberately.</b> The plan's own recommendation is to measure for four weeks
/// before letting anything act on these verdicts — WP3 (automatic rollback) and WP4 (repeat lock) are not
/// wired up. Turning them on without knowing whether the criteria are any good would be the very habit this
/// package exists to break: acting on an unverified belief about effect.</para>
/// </summary>
public sealed class ActionOutcomeSweeper : Whiskers.Services.FleetBackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IActionOutcomeService _outcomes;
    private readonly SelfMetrics.ISelfMetrics _selfMetrics;
    private readonly ILogger<ActionOutcomeSweeper> _logger;

    public ActionOutcomeSweeper(
        IActionOutcomeService outcomes, SelfMetrics.ISelfMetrics selfMetrics, ILogger<ActionOutcomeSweeper> logger)
    {
        _outcomes = outcomes;
        _selfMetrics = selfMetrics;
        _logger = logger;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;
            var success = true;

            try
            {
                var judged = await _outcomes.EvaluateDueAsync(DateTime.UtcNow, stoppingToken);

                foreach (var outcome in judged)
                    _selfMetrics.Count($"action_outcome_{outcome.Verdict.ToString().ToLowerInvariant()}", outcome.ServerId);

                if (judged.Count > 0)
                    _logger.LogInformation("Judged {Count} action check window(s)", judged.Count);
            }
            catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))
            {
                success = false;
                _logger.LogError(ex, "The action-outcome sweep failed");
            }

            // The sweep is itself a loop, so it reports like one — otherwise a checker that has stopped
            // checking looks exactly like a fleet with nothing to check, which is the failure this whole
            // package is about.
            _selfMetrics.RecordCycle("actionoutcomes", "*", DateTime.UtcNow - startedAt, success, Interval);

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
