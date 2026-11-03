namespace Whiskers.Services.Metrics.HostLoad;

/// <summary>
/// R1-56: the one duration model of every threshold alert, as numbers. Durations are elapsed time between readings, never
/// a sample count — a changed collection interval or a missed sample cannot change what "three minutes" means.
/// </summary>
/// <param name="SustainedFor">How long readings must stay at or above the threshold before the breach is raised.</param>
/// <param name="ClearMargin">The clear bound is the threshold minus this margin. A reading between the two (the
/// hysteresis band) neither resets nor clears a breach.</param>
/// <param name="ClearedFor">How long readings must stay below the clear bound before the breach ends.</param>
/// <param name="EscalationStep">How much worse an open breach must get to be restated; <see cref="double.PositiveInfinity"/>
/// never restates it.</param>
/// <param name="GapTolerance">The longest pause between two readings of a series that is still bridged. A longer one
/// resets the breach instead (a breach is never confirmed across readings that were not taken); null bridges any gap.</param>
/// <param name="MinimumGap">The least time between the end of a reported breach and the raise of the next one of the same
/// series. A breach that becomes reportable sooner is raised once that time has passed, if it is still open then.</param>
public sealed record BreachRules(
    TimeSpan SustainedFor,
    double ClearMargin,
    TimeSpan ClearedFor,
    double EscalationStep,
    TimeSpan? GapTolerance = null,
    TimeSpan MinimumGap = default)
{
    /// <summary>The documented clear margin of a percentage threshold alert (container CPU and memory, host disk).</summary>
    public const double PercentClearMargin = 5;

    /// <summary>The documented clear duration of a threshold alert.</summary>
    public static readonly TimeSpan DefaultClearedFor = TimeSpan.FromMinutes(5);

    /// <summary>The documented least gap tolerance: three minutes, or three collection intervals when that is longer — a
    /// series sampled once an hour cannot have a three-minute tolerance.</summary>
    public static readonly TimeSpan MinimumGapTolerance = TimeSpan.FromMinutes(3);

    /// <summary>The gap tolerance for a collection <paramref name="interval"/>: the larger of
    /// <see cref="MinimumGapTolerance"/> and three intervals. Only the tolerance follows the interval; the durations do not.</summary>
    public static TimeSpan GapToleranceFor(TimeSpan interval) =>
        interval * 3 > MinimumGapTolerance ? interval * 3 : MinimumGapTolerance;

    /// <summary>The rules of a container or disk threshold alert (<c>high_cpu</c>, <c>high_memory</c>, <c>high_disk</c>):
    /// raised once per breach after <paramref name="sustained"/>, never restated, cleared after
    /// <see cref="DefaultClearedFor"/> below the threshold minus <see cref="PercentClearMargin"/>; a new breach of the same
    /// series is raised no sooner than <paramref name="minimumGap"/> (<c>CooldownMinutes</c>) after the last one ended.</summary>
    public static BreachRules Threshold(TimeSpan sustained, TimeSpan minimumGap, TimeSpan gapTolerance) =>
        new(sustained, PercentClearMargin, DefaultClearedFor, double.PositiveInfinity, gapTolerance, minimumGap);
}

/// <summary>
/// "This value is over the line" → at most one open finding, escalated when it worsens, closed when it
/// really recovers (Plan-0004 WP5).
///
/// <para>Extracted rather than copied. The rules here are small but easy to get subtly wrong — the
/// confirmation window, the clear margin, measuring the outage to its end rather than to its confirmation —
/// and two of those were already wrong once. A second implementation for the latency rule would have been a
/// second chance to get them wrong differently, in a place nobody would think to compare.</para>
///
/// <para>R1-56: the one duration model for every threshold alert — the host rules and the container and disk alerts
/// (<c>MetricsCollectorService</c>) alike — with the rules as a <see cref="BreachRules"/>. A reading at or above the
/// threshold starts or continues a breach; one inside the hysteresis band neither resets nor clears it; the breach ends
/// only after readings stayed below the clear bound for <see cref="BreachRules.ClearedFor"/>, with a Cleared finding if it
/// was reported. A pause between readings longer than <see cref="BreachRules.GapTolerance"/> resets the breach rather
/// than bridging it (the caller reports the gap itself, <see cref="SeriesGapTracker"/>). Everything advances on the time
/// of the reading, so a replay and a fake clock drive it exactly like the collector. Safe to call from parallel tasks.</para>
/// </summary>
public sealed class BreachTracker
{
    private sealed class Breach
    {
        public DateTime? Since;              // when the value first went over
        public DateTime? Reported;           // when it was last said out loud (null = never)
        public double ReportedValue;         // what was said, so escalation can be measured against it
        public DateTime? BelowSince;         // when it came back under the clear line
        public double Peak;
        public DateTime? LastAt;             // R1-56: the previous reading, for the gap tolerance
        public DateTime? LastEnded;          // R1-56: when the last reported breach ended, for the minimum gap
    }

    private readonly Dictionary<string, Breach> _breaches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _serverNames = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly BreachRules _defaults;

    public BreachTracker(HostLoadThresholds thresholds) => _defaults = thresholds.Rules;

    /// <summary>Judges one reading. Returns a finding only at the moments something changed: first report,
    /// escalation, all-clear. Every other sample returns null, which is the normal case.</summary>
    /// <param name="rules">The rules for this reading; null takes the thresholds the tracker was built with. Passed per
    /// reading so settings changed at runtime (the metric alert thresholds) apply at the next sample.</param>
    public HostLoadFinding? Consider(
        DateTime atUtc, string serverId, string serverName, string kind, double value, double threshold,
        Func<double, TimeSpan, string> describeBreach,
        Func<double, string> describeRecovery,
        BreachRules? rules = null)
    {
        // R1-56: a breach is announced as its kind and closed as <kind>_recovered — both must be registered event types.
        if (!Whiskers.Services.Notifications.NotificationEventTypes.RecoverableKinds.Contains(kind))
            throw new ArgumentException($"'{kind}' is no registered breach kind (NotificationEventTypes.RecoverableKinds).", nameof(kind));
        var r = rules ?? _defaults;
        lock (_sync)
        {
            _serverNames[serverId] = serverName;
            var key = $"{serverId}|{kind}";
            var breach = _breaches.TryGetValue(key, out var existing) ? existing : _breaches[key] = new Breach();

            // R1-56: a pause longer than the tolerance is not bridged. The breach starts again from this reading — it
            // cannot have been confirmed across readings that were never taken — and a reported one counts as ended at
            // the last reading before the gap. The gap itself is reported by the caller (metrics_gap), never silently.
            if (r.GapTolerance is { } tolerance && breach.LastAt is { } last && atUtc - last > tolerance)
            {
                if (breach.Reported is not null) breach.LastEnded = last;
                Reset(breach);
            }
            breach.LastAt = atUtc;

            if (value < threshold)
                return BelowThreshold(atUtc, serverId, serverName, kind, value, threshold, breach, describeRecovery, r);

            // Over the line again — any recovery in progress is cancelled.
            breach.BelowSince = null;
            breach.Since ??= atUtc;
            breach.Peak = Math.Max(breach.Peak, value);

            var openFor = atUtc - breach.Since.Value;
            if (openFor < r.SustainedFor) return null;

            if (breach.Reported is null)
            {
                // R1-56: the minimum gap between two breaches of one series (CooldownMinutes). Held, not dropped: still
                // open once the gap has passed, it is raised then.
                if (breach.LastEnded is { } ended && atUtc - ended < r.MinimumGap) return null;

                breach.Reported = atUtc;
                breach.ReportedValue = value;
                return new HostLoadFinding(
                    atUtc, serverId, serverName, kind, FindingKind.Raised,
                    describeBreach(value, openFor), value, threshold, openFor);
            }

            // Already open. It is only worth saying again if it got materially worse — otherwise the operator
            // learns nothing they do not already know, and learns to skip the next one.
            if (value - breach.ReportedValue < r.EscalationStep) return null;

            breach.Reported = atUtc;
            breach.ReportedValue = value;
            return new HostLoadFinding(
                atUtc, serverId, serverName, kind, FindingKind.Escalated,
                "Getting worse — " + describeBreach(value, openFor), value, threshold, openFor);
        }
    }

    private static HostLoadFinding? BelowThreshold(
        DateTime atUtc, string serverId, string serverName, string kind, double value, double threshold,
        Breach breach, Func<double, string> describeRecovery, BreachRules rules)
    {
        // Not yet clearly below. Between the threshold and the clear line nothing is decided: the breach
        // stays open and no all-clear is given, which is what stops a value hovering at the threshold from
        // flapping between alert and all-clear until somebody mutes the channel.
        if (value > threshold - rules.ClearMargin)
        {
            breach.BelowSince = null;
            return null;
        }

        breach.BelowSince ??= atUtc;
        if (atUtc - breach.BelowSince < rules.ClearedFor) return null;

        var wasReported = breach.Reported;
        var openedAt = breach.Since;
        // The breach ended when the value dropped, not when we finished confirming it. Reporting the
        // confirmation window as part of the outage would overstate every incident by five minutes — small,
        // but it is the kind of drift that makes people stop trusting the numbers in a post-mortem.
        var endedAt = breach.BelowSince ?? atUtc;

        Reset(breach);

        // An all-clear only for something that was actually announced. A breach that resolved before it was
        // ever reported needs no closing message — sending one would mean the first thing the operator hears
        // about a problem is that it is over.
        if (wasReported is null) return null;
        breach.LastEnded = endedAt;

        var openFor = openedAt is { } start ? endedAt - start : TimeSpan.Zero;
        return new HostLoadFinding(
            atUtc, serverId, serverName, kind, FindingKind.Cleared,
            describeRecovery(value) + $" It had been over the threshold for {Describe(openFor)}.",
            value, threshold, openFor);
    }

    private static void Reset(Breach breach)
    {
        breach.Since = null;
        breach.Reported = null;
        breach.BelowSince = null;
        breach.Peak = 0;
    }

    /// <summary>R1-56: drops every series of <paramref name="serverId"/> (a container that is gone), so the state stays
    /// bounded by what is still collected.</summary>
    public void Forget(string serverId)
    {
        lock (_sync)
        {
            foreach (var key in _breaches.Keys.Where(k => k.StartsWith(serverId + "|", StringComparison.Ordinal)).ToList())
                _breaches.Remove(key);
            _serverNames.Remove(serverId);
        }
    }

    /// <summary>The ids of every series the tracker holds state for.</summary>
    public IReadOnlyCollection<string> Tracked()
    {
        lock (_sync) return _serverNames.Keys.ToList();
    }

    /// <summary>Findings that were raised and never closed, oldest first (Plan-0004 WP5.4).</summary>
    public IReadOnlyList<OpenFinding> OpenFindings()
    {
        lock (_sync)
            return _breaches
                .Where(kv => kv.Value.Reported is not null)
                .Select(kv =>
                {
                    var parts = kv.Key.Split('|', 2);
                    return new OpenFinding(
                        parts[0], _serverNames.GetValueOrDefault(parts[0], parts[0]),
                        parts.Length > 1 ? parts[1] : string.Empty,
                        kv.Value.Since ?? kv.Value.Reported!.Value,
                        kv.Value.Peak);
                })
                .OrderBy(f => f.SinceUtc)
                .ToList();
    }

    public static string Describe(TimeSpan span) => span switch
    {
        { TotalMinutes: < 90 } => $"{span.TotalMinutes:F0} minutes",
        { TotalHours: < 48 } => $"{span.TotalHours:F0} hours",
        _ => $"{span.TotalDays:F0} days"
    };
}
