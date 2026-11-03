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

/// <summary>R1-56: one series' breach as it is kept across a restart (<see cref="BreachTracker.Snapshot"/>).</summary>
/// <param name="Series">The series id (a container id, <c>disk:&lt;server&gt;</c>, a server id).</param>
/// <param name="Kind">The breach kind (<c>high_cpu</c>, …).</param>
/// <param name="ServerName">The name the findings carry.</param>
/// <param name="Since">When the value went over the line (null when no breach is open).</param>
/// <param name="Reported">When it was raised (null when no breach is open).</param>
/// <param name="ReportedValue">The value it was raised with.</param>
/// <param name="Peak">The highest value of the open breach.</param>
/// <param name="LastEnded">When the last reported breach ended, for the minimum gap.</param>
public sealed record BreachState(string Series, string Kind, string ServerName, DateTime? Since, DateTime? Reported,
    double ReportedValue, double Peak, DateTime? LastEnded);

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
        public bool Resumed;                 // review round 2: taken over from before a restart
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
            // the last reading before the gap. The gap itself is reported by the caller (metrics_gap).
            // Review round 2 — one rule for every reset of a reported breach (a series gap, a paused server, a failed
            // listing, alerts switched off and on, a held-up cycle): its raise is always closed, here, with
            // <kind>_recovered saying the recovery is not confirmed. No raise is ever left open by a reset, and a breach
            // that goes on is raised again only once new readings confirm it — as a new, closed-before incident.
            HostLoadFinding? closed = null;
            if (r.GapTolerance is { } tolerance && breach.LastAt is { } last && atUtc - last > tolerance)
            {
                if (breach.Reported is not null)
                {
                    breach.LastEnded = last;
                    closed = new HostLoadFinding(atUtc, serverId, serverName, kind, FindingKind.Cleared,
                        $"{serverName}: the {kind} alert is closed without a confirmed recovery — no readings for " +
                        $"{Describe(atUtc - last)} (tolerance {Describe(tolerance)}). It is raised again only if new readings " +
                        "confirm the breach.", value, threshold, last - (breach.Since ?? last));
                }
                Reset(breach);
            }
            breach.LastAt = atUtc;

            var judged = Judge(atUtc, serverId, serverName, kind, value, threshold, breach, describeBreach, describeRecovery, r);
            if (closed is null) return judged;
            // One transition per reading: a raise this same reading would make (a zero sustained duration) follows next.
            if (judged?.What == FindingKind.Raised) breach.Reported = null;
            return closed;
        }
    }

    /// <summary>Judges one reading of a breach whose gap handling is done. Under <see cref="_sync"/>.</summary>
    private static HostLoadFinding? Judge(
        DateTime atUtc, string serverId, string serverName, string kind, double value, double threshold, Breach breach,
        Func<double, TimeSpan, string> describeBreach, Func<double, string> describeRecovery, BreachRules r)
    {
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
        var resumed = breach.Resumed;
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
            describeRecovery(value) + $" It had been over the threshold for {Describe(openFor)}" +
            (resumed ? " (resumed across a restart of Whiskers; the time it was not running is included)." : "."),
            value, threshold, openFor);
    }

    private static void Reset(Breach breach)
    {
        breach.Since = null;
        breach.Reported = null;
        breach.BelowSince = null;
        breach.Peak = 0;
        breach.Resumed = false;
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

    /// <summary>R1-56 (review MAJOR-4): what must survive a restart — every breach that was reported and not yet closed, and
    /// when the last reported breach of a series ended (the minimum gap). A breach still being confirmed is not kept: it was
    /// never said, and confirming it again from new readings loses nothing.</summary>
    public IReadOnlyList<BreachState> Snapshot()
    {
        lock (_sync)
            return _breaches
                .Where(kv => kv.Value.Reported is not null || kv.Value.LastEnded is not null)
                .Select(kv =>
                {
                    var parts = kv.Key.Split('|', 2);
                    var b = kv.Value;
                    return b.Reported is null
                        ? new BreachState(parts[0], parts[1], _serverNames.GetValueOrDefault(parts[0], parts[0]), null, null, 0, 0, b.LastEnded)
                        : new BreachState(parts[0], parts[1], _serverNames.GetValueOrDefault(parts[0], parts[0]),
                            b.Since, b.Reported, b.ReportedValue, b.Peak, b.LastEnded);
                })
                .OrderBy(s => s.Series, StringComparer.Ordinal).ThenBy(s => s.Kind, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>R1-56 (review MAJOR-4): takes over the state <see cref="Snapshot"/> saved before a restart, at
    /// <paramref name="atUtc"/>. A reported breach stays open — it is not raised a second time while the value stays over
    /// the line, and it is closed with its recovery once readings show one — and its gap tolerance counts from now: the
    /// time Whiskers was not running is not a gap of the series. An entry of a kind that is no breach kind is skipped.</summary>
    public void Restore(IEnumerable<BreachState> states, DateTime atUtc)
    {
        lock (_sync)
            foreach (var s in states)
            {
                if (string.IsNullOrEmpty(s.Series) || s.Series.Contains('|')
                    || !Whiskers.Services.Notifications.NotificationEventTypes.RecoverableKinds.Contains(s.Kind)) continue;
                _breaches[$"{s.Series}|{s.Kind}"] = new Breach
                {
                    Since = s.Reported is null ? null : s.Since ?? s.Reported,
                    Reported = s.Reported,
                    ReportedValue = s.ReportedValue,
                    Peak = s.Peak,
                    LastAt = s.Reported is null ? null : atUtc,
                    LastEnded = s.LastEnded,
                    Resumed = s.Reported is not null,
                };
                _serverNames[s.Series] = s.ServerName;
            }
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
