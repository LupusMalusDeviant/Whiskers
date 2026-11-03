namespace Whiskers.Services.Metrics.HostLoad;

/// <summary>What kind of statement a finding is (Plan-0004 WP5.1/WP5.2).</summary>
public enum FindingKind
{
    /// <summary>First time this breach is reported.</summary>
    Raised,

    /// <summary>It got materially worse while already open. Escalates rather than repeats — a signal that
    /// says the same thing every six hours is one people build a filter rule for.</summary>
    Escalated,

    /// <summary>Back below the threshold, and stayed there. The finding is closed.</summary>
    Cleared
}

/// <summary>What the evaluator decided about one sample.</summary>
/// <param name="AtUtc">The time of the <em>sample</em>, not of the evaluation. That distinction is what makes
/// a replay meaningful: a six-day series pushed through in a second must still report 20 August, 14:14.</param>
/// <param name="OpenFor">How long the breach had been going when this was said. An alert without a duration
/// cannot be triaged — "98%" is a different problem at two minutes and at six days.</param>
public sealed record HostLoadFinding(
    DateTime AtUtc,
    string ServerId,
    string ServerName,
    string Kind,
    FindingKind What,
    string Summary,
    double Value,
    double Threshold,
    TimeSpan OpenFor)
{
    /// <summary>The event type this is delivered as. An all-clear must not arrive under the alert's own name:
    /// every channel, filter rule and severity mapping keys off this string, so a closing message labelled
    /// <c>host_cpu_high</c> would be rendered, coloured and escalated as a fresh alarm.</summary>
    public string EventType => What == FindingKind.Cleared ? Whiskers.Services.Notifications.NotificationEventTypes.Recovered(Kind) : Kind;

    /// <summary>Warning for a problem, informational for its end.</summary>
    public string Severity => What == FindingKind.Cleared ? "Info" : "Warning";
}

/// <summary>An unresolved finding, for the "is the closing path working?" metric (Plan-0004 WP5.4).</summary>
public sealed record OpenFinding(string ServerId, string ServerName, string Kind, DateTime SinceUtc, double Value);

/// <summary>Thresholds for the host-level rules. Defaults are aimed at the 2-core machines in this fleet.</summary>
public sealed class HostLoadThresholds
{
    /// <summary>Percent of the whole machine.</summary>
    public double CpuPercent { get; init; } = 90;

    public double MemoryPercent { get; init; } = 90;

    /// <summary>How much host CPU may go unexplained by containers before it is worth saying so. Set well
    /// above the noise between two samples taken moments apart, and below the incident's own 86 points.</summary>
    public double UnexplainedCpuPercent { get; init; } = 40;

    /// <summary>How long a breach must last before it is reported. A build peak is not an incident; six days
    /// at 98% is. Ten minutes is short enough to have caught 2026-08-26 by 14:14 — the incident report puts a
    /// host threshold at 14:17, so this is if anything a little quicker.</summary>
    public TimeSpan SustainedFor { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How far below the threshold the value must fall before the all-clear is given, and how long
    /// it must stay there (<see cref="ClearedFor"/>).
    ///
    /// <para>Without this margin a server sitting exactly at the threshold produces alert, all-clear, alert,
    /// all-clear, forever — and a channel that does that is muted within a day. Hysteresis is not polish
    /// here; it is what keeps the signal usable.</para></summary>
    public double ClearMargin { get; init; } = 5;

    public TimeSpan ClearedFor { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How much worse it has to get before an open finding is restated as an escalation.
    ///
    /// <para>Five points, not ten: CPU and memory are capped at 100, so from a threshold of 90 a ten-point
    /// step is almost unreachable — 91% climbing to 99% is a real escalation an operator wants, and it would
    /// have been swallowed.</para></summary>
    public double EscalationStep { get; init; } = 5;

    /// <summary>R1-56: the longest pause between two readings that is still bridged; a longer one resets the breach. Null
    /// (the default) bridges any gap; the collector passes the documented tolerance for its interval with each sample.</summary>
    public TimeSpan? GapTolerance { get; init; }

    /// <summary>These thresholds in the one duration model (R1-56).</summary>
    public BreachRules Rules => new(SustainedFor, ClearMargin, ClearedFor, EscalationStep, GapTolerance);

    /// <summary>An open finding this old means the closing path is broken, not that the problem is patient.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);
}

/// <summary>
/// The rules that would have caught the 2026-08-26 incident (Plan-0004 WP1/WP2/WP5).
///
/// <para>Whiskers recorded roughly 8,900 measurements of BurgCloud over six days, practically every one of
/// them above 98%, and evaluated none of them — because <c>EvaluateAlertsAsync</c> judges containers, and
/// <c>dockerd</c> runs in no container. The load fell through the gap between "per container" and "disk".</para>
///
/// <para><b>Driven by sample time, never by the wall clock.</b> Everything here advances on the timestamp in
/// the sample, which is what lets a recorded or reconstructed week be pushed through in under a second and
/// still produce findings dated correctly. A rule that consults <c>DateTime.UtcNow</c> cannot be replayed,
/// and a rule that cannot be replayed cannot be shown to catch the incident it was written for.</para>
///
/// <para><b>One open finding per server and metric.</b> It is raised once, escalated if it gets materially
/// worse, and closed when the load comes back down and stays down. Repetition is what trains people to
/// ignore a channel, and an alert nobody reads is indistinguishable from no alert at all — which is the
/// state this whole package exists to leave behind.</para>
/// </summary>
public sealed class HostLoadEvaluator
{
    private readonly BreachTracker _tracker;
    private readonly HostLoadThresholds _thresholds;

    public HostLoadEvaluator(HostLoadThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? new HostLoadThresholds();
        _tracker = new BreachTracker(_thresholds);
    }

    /// <summary>Judges one sample and returns whatever became reportable at that moment. Usually empty.</summary>
    public IReadOnlyList<HostLoadFinding> Evaluate(HostSample sample) => Evaluate(sample, null);

    /// <summary>Judges one sample and returns whatever became reportable at that moment. Usually empty.</summary>
    /// <param name="gapTolerance">R1-56: the longest pause since the previous sample that is still bridged (the collector
    /// passes <see cref="BreachRules.GapToleranceFor"/> its interval); null keeps <see cref="HostLoadThresholds.GapTolerance"/>.</param>
    public IReadOnlyList<HostLoadFinding> Evaluate(HostSample sample, TimeSpan? gapTolerance)
    {
        var findings = new List<HostLoadFinding>();
        var rules = gapTolerance is null ? _thresholds.Rules : _thresholds.Rules with { GapTolerance = gapTolerance };

        Add(findings, _tracker.Consider(
            sample.AtUtc, sample.ServerId, sample.ServerName, Whiskers.Services.Notifications.NotificationEventTypes.HostCpuHigh,
            sample.HostCpuPercent, _thresholds.CpuPercent,
            (v, open) => $"{sample.ServerName} has been at {v:F0}% CPU for {BreachTracker.Describe(open)} " +
                         $"(threshold {_thresholds.CpuPercent:F0}%). This is the whole machine, not one container.",
            v => $"{sample.ServerName} is back to {v:F0}% CPU.", rules));

        Add(findings, _tracker.Consider(
            sample.AtUtc, sample.ServerId, sample.ServerName, Whiskers.Services.Notifications.NotificationEventTypes.HostMemoryHigh,
            sample.MemoryPercent, _thresholds.MemoryPercent,
            (v, open) => $"{sample.ServerName} has been at {v:F0}% memory for {BreachTracker.Describe(open)} " +
                         $"(threshold {_thresholds.MemoryPercent:F0}%).",
            v => $"{sample.ServerName} is back to {v:F0}% memory.", rules));

        // The specific one. It names the class of cause rather than only the symptom, which is what turns
        // "the server is busy" into "something outside the containers is busy" — the exact fact that took six
        // days to establish by hand.
        Add(findings, _tracker.Consider(
            sample.AtUtc, sample.ServerId, sample.ServerName, Whiskers.Services.Notifications.NotificationEventTypes.HostCpuUnexplained,
            sample.UnexplainedCpuPercent, _thresholds.UnexplainedCpuPercent,
            (v, open) => $"{sample.ServerName} is using {sample.HostCpuPercent:F0}% CPU while its containers together " +
                         $"account for only {sample.ContainerCpuPercentOfMachine:F0}% — {v:F0} points unexplained, " +
                         $"for {BreachTracker.Describe(open)}. A host process is the likely cause; dockerd, a backup " +
                         "job and a runaway service all look like this. Short-lived containers are missing from the " +
                         "sum, so treat this as a strong hint rather than proof.",
            v => $"{sample.ServerName}: the unexplained load is down to {v:F0} points — its containers account " +
                 "for what the host is doing again.", rules));

        return findings;
    }

    /// <summary>Findings that were raised and never closed, oldest first. Drives the WP5.4 metric: if the
    /// count of stale entries only ever climbs, the closing path is broken — and a monitor whose alerts never
    /// close stops being read long before anyone works out why.</summary>
    public IReadOnlyList<OpenFinding> OpenFindings() => _tracker.OpenFindings();

    /// <summary>R1-56 (review MAJOR-4): the open host breaches to keep across a restart (<see cref="BreachTracker.Snapshot"/>).</summary>
    public IReadOnlyList<BreachState> Snapshot() => _tracker.Snapshot();

    /// <summary>R1-56 (review MAJOR-4): resumes the host breaches saved before a restart (<see cref="BreachTracker.Restore"/>).</summary>
    public void Restore(IEnumerable<BreachState> states, DateTime atUtc) => _tracker.Restore(states, atUtc);

    /// <summary>The CPU threshold this evaluator is using. Exposed so the rolling baseline can compare its
    /// learned mean against the SAME number rather than keeping its own copy — two definitions of "too high"
    /// would eventually disagree, and the drift guard would then be measuring against a limit nobody
    /// alerts on.</summary>
    public static double CpuThresholdOf(HostLoadEvaluator evaluator) => evaluator._thresholds.CpuPercent;

    private static void Add(List<HostLoadFinding> findings, HostLoadFinding? finding)
    {
        if (finding is not null) findings.Add(finding);
    }
}
