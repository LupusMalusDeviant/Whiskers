namespace Whiskers.Services.Metrics.HostLoad;

/// <summary>Settings for the Docker API response-time rule (Plan-0004 WP4).</summary>
public sealed class ApiLatencySettings
{
    /// <summary>How many of the most recent readings form the "now" median. Small enough to react within a
    /// couple of cycles, large enough that a single slow call is not a verdict.</summary>
    public int RecentSamples { get; init; } = 5;

    /// <summary>How many readings are needed before any judgement is offered. Below this the rule stays
    /// silent — a baseline built from three measurements would call the fourth an anomaly.</summary>
    public int MinimumSamples { get; init; } = 20;

    /// <summary>How many times the baseline the recent median must reach. Three, not two: Docker call times
    /// vary by more than a factor of two on a healthy host, and the fingerprint this exists to catch is 100 ms
    /// becoming 5 s — a factor of fifty.</summary>
    public double Factor { get; init; } = 3;

    /// <summary>How slow the CURRENT median has to be before a ratio means anything. Going from 2 ms to 8 ms
    /// is a factor of four and tells nobody anything.
    ///
    /// <para>Applies to the recent median only, never to the baseline — that was the first version and it
    /// silenced the exact case this rule exists for: the incident's healthy value was 100 ms, so a floor on
    /// the baseline threw away every comparison that mattered.</para></summary>
    public TimeSpan Floor { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// The Docker daemon's own response time as a signal (Plan-0004 WP4).
///
/// <para>An overloaded daemon has a fingerprint that says nothing about <em>who</em> overloaded it: calls that
/// took 100 ms start taking seconds. During the 2026-08-26 incident dockerd was burning 184% of two cores and
/// every Whiskers call to that host went through the same treacle — but nothing measured it, so the only
/// visible symptom was that things "felt slow".</para>
///
/// <para>Deliberately a ratio against the host's own recent history, not a fixed millisecond threshold. A
/// Raspberry Pi over a tunnel and a local socket differ by an order of magnitude when both are perfectly
/// healthy, and any single number would be wrong for one of them.</para>
/// </summary>
public sealed class ApiLatencyEvaluator
{
    private readonly BreachTracker _tracker;
    private readonly ApiLatencySettings _settings;

    /// <summary>The hysteresis for this rule is on a RATIO, not a percentage.
    ///
    /// <para>Inheriting the host rules' defaults was the second mistake here: a five-point clear margin below
    /// a threshold of three means the all-clear can only be given at a ratio of minus two, which never
    /// happens. The finding would have been raised and then stayed open forever — and an alert that never
    /// closes is precisely what the open-findings metric was added to catch.</para></summary>
    private static HostLoadThresholds RatioScale(HostLoadThresholds? given) => given ?? new HostLoadThresholds
    {
        ClearMargin = 1,      // clears once it is back under 2× its usual speed
        EscalationStep = 3,   // 3× → 6× is worth saying again; 3× → 3.5× is not
    };

    public ApiLatencyEvaluator(HostLoadThresholds? thresholds = null, ApiLatencySettings? settings = null)
    {
        _tracker = new BreachTracker(RatioScale(thresholds));
        _settings = settings ?? new ApiLatencySettings();
    }

    /// <summary>Judges one server's recent call durations, oldest first.</summary>
    public HostLoadFinding? Evaluate(DateTime atUtc, string serverId, string serverName, IReadOnlyList<TimeSpan> samples)
    {
        // Not enough history to have an opinion. Staying silent is the honest answer: a baseline built from a
        // handful of readings would call the next one an anomaly, and a rule that cries wolf while it is
        // still learning gets switched off before it is ever useful.
        if (samples.Count < _settings.MinimumSamples) return null;

        var split = Math.Max(0, samples.Count - _settings.RecentSamples);
        var recent = Median(samples.Skip(split).ToList());

        // The baseline EXCLUDES the recent window. Including it was the first attempt and it failed exactly
        // the way Plan-0004 WP3.4 warns about: a sustained slowdown fills the window, the median moves with
        // the problem, and the ratio falls back under the threshold while the daemon is still crawling. With
        // 40 fast readings followed by 40 slow ones the median sat at 2,550 ms and nothing fired at all.
        //
        // The consequence is worth being explicit about: **this rule detects the transition, not the state.**
        // Once the whole window is slow, the host's own new normal IS slow and this goes quiet again. That is
        // not a gap left open — the sustained case is what the host-CPU rule and the supervisory rule are for,
        // and each of the three sees something the others cannot.
        var baseline = Median(samples.Take(split).ToList());

        // The floor is on the CURRENT median only. A recent median of 8 ms is not a problem whatever the
        // ratio says; a baseline of 100 ms is the healthy value from the incident itself and must not be
        // thrown away.
        if (recent < _settings.Floor || baseline <= TimeSpan.Zero) return null;

        var ratio = recent.TotalMilliseconds / baseline.TotalMilliseconds;

        return _tracker.Consider(
            atUtc, serverId, serverName, Whiskers.Services.Notifications.NotificationEventTypes.HostApiSlow, ratio, _settings.Factor,
            (r, open) =>
                $"Docker on {serverName} is answering {r:F1}× slower than usual — {recent.TotalMilliseconds:F0} ms " +
                $"against a baseline of {baseline.TotalMilliseconds:F0} ms, for {BreachTracker.Describe(open)}. " +
                "This is the daemon itself being slow; it says nothing yet about what is loading it. Check the " +
                "host's own CPU and whether any container explains it.",
            _ => $"Docker on {serverName} is answering at its usual speed again ({recent.TotalMilliseconds:F0} ms).");
    }

    public IReadOnlyList<OpenFinding> OpenFindings() => _tracker.OpenFindings();

    /// <summary>Median rather than mean: one 8-second timeout in a window of twenty would drag an average
    /// far enough to raise an alert on a host that is perfectly fine.</summary>
    private static TimeSpan Median(IReadOnlyList<TimeSpan> values)
    {
        if (values.Count == 0) return TimeSpan.Zero;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : TimeSpan.FromTicks((sorted[mid - 1].Ticks + sorted[mid].Ticks) / 2);
    }
}
