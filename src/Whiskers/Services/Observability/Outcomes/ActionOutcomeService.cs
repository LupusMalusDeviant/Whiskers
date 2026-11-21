using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Observability.Outcomes;

/// <summary>
/// Records automatic actions and judges them against the criterion declared in advance (Plan-0006 WP2).
///
/// <para><b>Reads existing series only.</b> The evaluation asks the self-metrics and the stored server
/// metrics what happened; it measures nothing itself. A checker that collected its own data would add load in
/// exactly the situations it exists to examine — which is the mistake it is there to catch.</para>
/// </summary>
public sealed class ActionOutcomeService : IActionOutcomeService
{
    /// <summary>
    /// Whiskers' own start time. An action whose window spans a restart cannot be judged: the in-memory
    /// series it would be measured against began again from nothing.
    ///
    /// <para>Read from the OS, not captured as <c>DateTime.UtcNow</c> in a static field. The first attempt did
    /// the latter and was wrong in the worst possible way: a <c>static readonly</c> field in a class without a
    /// static constructor is <c>beforefieldinit</c>, so the runtime may initialise it as late as the first
    /// access — which here is the evaluation itself. The "start time" therefore landed <em>after</em> every
    /// action, the restart guard matched every time, and every single outcome came back "not measurable".
    /// A checker that never checks, wearing the costume of diligence. The OS value is a fixed fact and does
    /// not care when it is asked.</para>
    /// </summary>
    private static DateTime ProcessStartedUtc =>
        System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SelfMetrics.ISelfMetrics _selfMetrics;
    private readonly ILogger<ActionOutcomeService> _logger;
    private readonly Whiskers.Configuration.EffectiveInterval<Whiskers.Configuration.MetricsSettings>? _collection;

    /// <param name="collection">The metric collection interval (<c>Metrics:CollectionIntervalSeconds</c>), which sets how
    /// far before the window's end a stored reading may lie; without it the tolerance is <see cref="MinimumWindowEndTolerance"/>.</param>
    public ActionOutcomeService(
        IServiceScopeFactory scopeFactory, SelfMetrics.ISelfMetrics selfMetrics, ILogger<ActionOutcomeService> logger,
        Whiskers.Configuration.EffectiveInterval<Whiskers.Configuration.MetricsSettings>? collection = null)
    {
        _scopeFactory = scopeFactory;
        _selfMetrics = selfMetrics;
        _logger = logger;
        _collection = collection;
    }

    public async Task<string> RecordAsync(
        AutomaticActionKind kind, string serverId, string targetId, string targetName,
        string? reason = null, string? correlationId = null, CancellationToken ct = default)
    {
        // Throws for an undeclared kind. That is the enforcement point: an automatic action nobody can check
        // must not even reach the point of being recorded as done.
        var criterion = ActionCriteria.For(kind);
        var now = DateTime.UtcNow;
        var id = correlationId ?? Guid.NewGuid().ToString("N");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        db.ActionOutcomes.Add(new ActionOutcomeEntity
        {
            CorrelationId = id,
            ActionKind = kind.ToString(),
            ServerId = serverId,
            TargetId = targetId,
            TargetName = targetName,
            ExecutedAtUtc = now,
            DueAtUtc = now + criterion.Window,
            Verdict = ActionVerdict.Pending,
            Reason = reason
        });

        await db.SaveChangesAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<ActionOutcomeEntity>> EvaluateDueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var due = await db.ActionOutcomes
            .Where(o => o.Verdict == ActionVerdict.Pending && o.DueAtUtc <= nowUtc)
            .OrderBy(o => o.DueAtUtc)
            .Take(200)
            .ToListAsync(ct);

        foreach (var outcome in due)
            Judge(db, outcome, nowUtc);

        if (due.Count > 0) await db.SaveChangesAsync(ct);
        return due;
    }

    private void Judge(MetricsDbContext db, ActionOutcomeEntity outcome, DateTime nowUtc)
    {
        outcome.EvaluatedAtUtc = nowUtc;

        if (!Enum.TryParse<AutomaticActionKind>(outcome.ActionKind, out var kind))
        {
            // A row written by a newer version whose action kind this build does not know. Not measurable —
            // certainly not a success.
            outcome.Verdict = ActionVerdict.NotMeasurable;
            outcome.Detail = $"This build does not know the action kind '{outcome.ActionKind}'.";
            return;
        }

        // WP2.4, first case. A restart inside the window wiped the in-memory series this would be measured
        // against. Reading whatever is there now and calling it a result would be worse than admitting the
        // gap: the reading describes the new process, not the action.
        if (ProcessStartedUtc > outcome.ExecutedAtUtc)
        {
            outcome.Verdict = ActionVerdict.NotMeasurable;
            outcome.Detail = "Whiskers restarted inside the check window, so the measurements it would have " +
                             "been judged against start after the action.";
            return;
        }

        var criterion = ActionCriteria.For(kind);
        var reading = Read(db, criterion.Metric, outcome);

        // WP2.4, second case and the central rule of this plan: no data is not success. Folding this into
        // "worked" is the incident's own shape — the absence of a signal read as the absence of a problem.
        if (reading is not { } value)
        {
            outcome.Verdict = ActionVerdict.NotMeasurable;
            outcome.Detail = $"No reading for {criterion.Metric} on {outcome.ServerId} when the window closed.";
            return;
        }

        var met = criterion.IsMet(value);
        outcome.Verdict = met ? ActionVerdict.Worked : ActionVerdict.DidNotWork;
        outcome.Detail =
            $"{criterion.Metric} was {value:F1} {criterion.Window.TotalMinutes:F0} minutes after the action; " +
            $"the criterion asked for {(criterion.Direction == OutcomeDirection.Below ? "below" : "at least")} " +
            $"{criterion.Threshold:F1}. {(met ? "Met." : "Not met.")}";

        if (!met)
            _logger.LogWarning(
                "Automatic action {Kind} on {Target} did not achieve what it promised: {Detail}",
                outcome.ActionKind, outcome.TargetName, outcome.Detail);
    }

    /// <summary>Reads one of the existing series. Returns null when there is nothing to read — which becomes
    /// <see cref="ActionVerdict.NotMeasurable"/>, never a pass.
    ///
    /// <para>Every metric a criterion can name must be readable here. A criterion whose metric always came
    /// back null would be permanently "not measurable" — which looks like diligence and is in fact a control
    /// that never controls anything. <c>ActionOutcomeTests</c> walks the declared criteria and demands each
    /// one can actually produce a verdict.</para></summary>
    private double? Read(MetricsDbContext db, string metric, ActionOutcomeEntity outcome)
    {
        switch (metric)
        {
            case ActionCriteria.Metrics.ApiLatencyMs:
            {
                if (!_selfMetrics.ApiLatencies().TryGetValue(outcome.ServerId, out var window) || window.Samples.Count == 0)
                    return null;
                // Review 1.0.0: the newest call must have been measured after the action and at the end of its window.
                // A reading from before describes the state the action was meant to change; judging on it inflated the
                // success rate of every throttle and lockout whose server was not called again afterwards.
                if (!DescribesWindowEnd(window.LastAtUtc, outcome)) return null;
                return window.Samples[^1].TotalMilliseconds;
            }

            case ActionCriteria.Metrics.LoopSuccessAgeSeconds:
            {
                var loop = _selfMetrics.Loops().FirstOrDefault(l => l.ServerId == outcome.ServerId);
                if (loop?.LastSuccess is not { } last) return null;
                return (DateTime.UtcNow - last).TotalSeconds;
            }

            case ActionCriteria.Metrics.HostCpuPercent:
            {
                // The newest reading taken AFTER the action. One from before would describe the world the
                // action was meant to change.
                var sample = db.ServerMetrics
                    .Where(m => m.ServerId == outcome.ServerId && m.Timestamp >= outcome.ExecutedAtUtc)
                    .OrderByDescending(m => m.Timestamp)
                    .FirstOrDefault();
                return sample is not null && DescribesWindowEnd(sample.Timestamp, outcome) ? sample.CpuPercent : null;
            }

            case ActionCriteria.Metrics.ContainerUp:
            {
                // Scoped to the server too: container ids are unique per host only.
                var sample = db.ContainerMetrics
                    .Where(m => m.ServerId == outcome.ServerId && m.ContainerId == outcome.TargetId
                                && m.Timestamp >= outcome.ExecutedAtUtc)
                    .OrderByDescending(m => m.Timestamp)
                    .FirstOrDefault();

                // A metric row exists only while the container is running and answering, so its presence is
                // the liveness signal. Its absence is genuinely ambiguous — stopped, or simply not collected
                // yet — and ambiguity is what NotMeasurable is for. Review 1.0.0: the row has to come from the end of
                // the window — "running five minutes later" — not from seconds after the action: a container that
                // died right after its restart left one such row and was counted as a restart that worked.
                return sample is not null && DescribesWindowEnd(sample.Timestamp, outcome) ? 1 : null;
            }

            default:
                return null;
        }
    }

    /// <summary>The least distance before the end of the check window a reading may lie and still describe it.</summary>
    internal static readonly TimeSpan MinimumWindowEndTolerance = TimeSpan.FromMinutes(2);

    /// <summary>How far before the end of the check window a reading may lie and still describe it: two collection
    /// intervals, at least <see cref="MinimumWindowEndTolerance"/> — with a long interval no stored reading could lie
    /// closer to the end. An older reading says nothing about the end of the window.</summary>
    internal TimeSpan WindowEndTolerance =>
        _collection?.Interval * 2 is { } twice && twice > MinimumWindowEndTolerance ? twice : MinimumWindowEndTolerance;

    /// <summary>Review 1.0.0: whether a reading taken at <paramref name="at"/> describes the end of the action's window —
    /// taken after the action, and no earlier than <see cref="WindowEndTolerance"/> before the window closed.</summary>
    private bool DescribesWindowEnd(DateTime at, ActionOutcomeEntity outcome) =>
        at >= outcome.ExecutedAtUtc && at >= outcome.DueAtUtc - WindowEndTolerance;

    public async Task<IReadOnlyList<OutcomeTally>> TalliesAsync(DateTime sinceUtc, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var rows = await db.ActionOutcomes
            .Where(o => o.ExecutedAtUtc >= sinceUtc)
            .GroupBy(o => o.ActionKind)
            .Select(g => new
            {
                Kind = g.Key,
                Worked = g.Count(o => o.Verdict == ActionVerdict.Worked),
                DidNotWork = g.Count(o => o.Verdict == ActionVerdict.DidNotWork),
                NotMeasurable = g.Count(o => o.Verdict == ActionVerdict.NotMeasurable),
                Pending = g.Count(o => o.Verdict == ActionVerdict.Pending)
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new OutcomeTally(r.Kind, r.Worked, r.DidNotWork, r.NotMeasurable, r.Pending))
            .OrderBy(t => t.ActionKind, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<int> OverdueCountAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        // A minute of slack so a window that came due seconds ago is not called overdue.
        var cutoff = nowUtc.AddMinutes(-1);
        return await db.ActionOutcomes.CountAsync(o => o.Verdict == ActionVerdict.Pending && o.DueAtUtc < cutoff, ct);
    }
}
