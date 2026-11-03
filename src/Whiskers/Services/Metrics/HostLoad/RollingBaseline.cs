namespace Whiskers.Services.Metrics.HostLoad;

/// <summary>Settings for the rolling baseline (Plan-0004 WP3).</summary>
public sealed class BaselineSettings
{
    /// <summary>How much weight each new reading carries. 0.002 gives a mean that takes roughly a day of
    /// 30-second samples to turn around — slow enough that a deploy does not move it, fast enough that a
    /// server which genuinely changed shape is not compared against last week forever.</summary>
    public double Alpha { get; init; } = 0.002;

    /// <summary>How far from its own normal a reading has to be. Four, not the textbook three: real host CPU
    /// is not normally distributed — it has a floor at zero, a ceiling at a hundred, and a long tail of
    /// legitimate busy periods. Three sigma on that shape produces an alert most days, and a rule that fires
    /// most days is one nobody reads.</summary>
    public double Sigma { get; init; } = 4;

    /// <summary>How long before the baseline is trusted to judge anything. Until then it reports that it is
    /// still learning rather than guessing — a rule that cries wolf during its first two days never earns the
    /// benefit of the doubt afterwards.</summary>
    public TimeSpan LearningPeriod { get; init; } = TimeSpan.FromHours(48);

    /// <summary>A floor under the standard deviation. On a host that has been perfectly flat the deviation
    /// approaches zero, and any wobble at all becomes an infinite z-score — the quietest servers would be the
    /// noisiest alerters.</summary>
    public double MinimumStdDev { get; init; } = 3;
}

/// <summary>What the baseline currently knows about one server and metric.</summary>
public sealed record BaselineState(
    string ServerId, string Kind, double Mean, double StdDev, long Samples, bool StillLearning);

/// <summary>
/// Deviation from a host's own normal, rather than a fixed threshold (Plan-0004 WP3).
///
/// <para>An exponentially weighted mean and variance per server and metric: one pair of numbers each, updated
/// in place. No window is kept — a rolling window over a fleet of servers and half a dozen metrics is a
/// memory cost that grows with the retention period, for an answer that two doubles already give.</para>
///
/// <para><b>The interesting part is WP3.4.</b> A baseline that keeps learning through an incident eventually
/// decides that 98% is this server's normal, and then stops complaining — going quiet precisely when the
/// problem has lasted longest. Whiskers has already been bitten by that shape twice: once by the log-scan
/// watermark that grew with every failure, and once, hours ago, by an API-latency baseline that absorbed the
/// slowdown it was meant to detect. So this rule watches its own mean: when the learned normal itself crosses
/// the absolute threshold, that is the finding.</para>
/// </summary>
public sealed class RollingBaseline
{
    private sealed class Series
    {
        public double Mean;
        public double Variance;
        public long Samples;
        public DateTime FirstSeenUtc;
    }

    private readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);
    private readonly BreachTracker _tracker;
    private readonly BaselineSettings _settings;

    /// <summary>Hysteresis on a z-score, not on a percentage — the same trap that had the latency rule
    /// unable to ever give an all-clear.</summary>
    private static HostLoadThresholds ZScoreScale(HostLoadThresholds? given) => given ?? new HostLoadThresholds
    {
        ClearMargin = 1,
        EscalationStep = 2,
    };

    public RollingBaseline(BaselineSettings? settings = null, HostLoadThresholds? thresholds = null)
    {
        _settings = settings ?? new BaselineSettings();
        _tracker = new BreachTracker(ZScoreScale(thresholds));
    }

    /// <summary>Feeds one reading in and returns whatever became reportable.
    ///
    /// <param name="absoluteThreshold">The fixed threshold this metric would breach on its own (WP1). Used
    /// only to notice that the <em>learned mean</em> has climbed past it.</param>
    /// </summary>
    public IReadOnlyList<HostLoadFinding> Observe(
        DateTime atUtc, string serverId, string serverName, string kind, double value, double absoluteThreshold)
    {
        // R1-56: the event types come from the registry; a kind it has no anomaly/drift type for is a programming error.
        var (anomalyKind, driftKind) = kind switch
        {
            "host_cpu" => (Whiskers.Services.Notifications.NotificationEventTypes.HostCpuAnomaly,
                Whiskers.Services.Notifications.NotificationEventTypes.HostCpuBaselineDrifted),
            _ => throw new ArgumentException($"No anomaly event type is registered for '{kind}'.", nameof(kind)),
        };
        var key = $"{serverId}|{kind}";
        var series = _series.TryGetValue(key, out var existing) ? existing : _series[key] = new Series { FirstSeenUtc = atUtc };
        var findings = new List<HostLoadFinding>();

        // The z-score is measured against the mean BEFORE this reading is folded in. Updating first would let
        // every value pull its own baseline towards itself and quietly shrink its own deviation.
        var learning = atUtc - series.FirstSeenUtc < _settings.LearningPeriod;
        var stdDev = Math.Max(Math.Sqrt(series.Variance), _settings.MinimumStdDev);
        var z = series.Samples > 0 ? Math.Abs(value - series.Mean) / stdDev : 0;

        Update(series, value);

        if (!learning)
        {
            var anomaly = _tracker.Consider(
                atUtc, serverId, serverName, anomalyKind, z, _settings.Sigma,
                (score, open) =>
                    $"{serverName}: {kind.Replace("host_", "").Replace("_", " ")} is {value:F0}, which is " +
                    $"{score:F1} standard deviations from this server's own normal of {series.Mean:F0} " +
                    $"(±{stdDev:F0}), and has been for {BreachTracker.Describe(open)}. This is a deviation from " +
                    "how this host usually behaves, not a fixed limit — it may be perfectly acceptable and still " +
                    "worth knowing about.",
                _ => $"{serverName}: {kind.Replace("host_", "").Replace("_", " ")} is back within its usual range.");

            if (anomaly is not null) findings.Add(anomaly);
        }

        // WP3.4 — the protection against learning the fault. Deliberately outside the learning check: if the
        // very first 48 hours are already spent above the threshold, that is the most important thing this
        // rule could possibly say, and "still learning" would be the worst possible moment to stay silent.
        var drift = _tracker.Consider(
            atUtc, serverId, serverName, driftKind, series.Mean, absoluteThreshold,
            (mean, open) =>
                $"{serverName}: the learned normal for {kind.Replace("host_", "").Replace("_", " ")} has itself " +
                $"risen to {mean:F0}, past the fixed threshold of {absoluteThreshold:F0}, over {BreachTracker.Describe(open)}. " +
                "The deviation rule has started treating this as ordinary and will stop reporting it. Whatever " +
                "caused it has now been running long enough to look normal.",
            mean => $"{serverName}: the learned normal is back down to {mean:F0}.");

        if (drift is not null) findings.Add(drift);

        return findings;
    }

    /// <summary>Exponentially weighted mean and variance, updated in place (WP3.1).</summary>
    private void Update(Series series, double value)
    {
        series.Samples++;

        if (series.Samples == 1)
        {
            series.Mean = value;
            series.Variance = 0;
            return;
        }

        var diff = value - series.Mean;
        var increment = _settings.Alpha * diff;
        series.Mean += increment;
        series.Variance = (1 - _settings.Alpha) * (series.Variance + diff * increment);
    }

    /// <summary>What the baseline currently believes, for the status view and the "still learning" notice.</summary>
    public IReadOnlyList<BaselineState> States(DateTime atUtc)
        => _series.Select(kv =>
        {
            var parts = kv.Key.Split('|', 2);
            return new BaselineState(
                parts[0], parts.Length > 1 ? parts[1] : string.Empty,
                kv.Value.Mean,
                Math.Max(Math.Sqrt(kv.Value.Variance), _settings.MinimumStdDev),
                kv.Value.Samples,
                atUtc - kv.Value.FirstSeenUtc < _settings.LearningPeriod);
        })
        .OrderBy(s => s.ServerId, StringComparer.Ordinal)
        .ThenBy(s => s.Kind, StringComparer.Ordinal)
        .ToList();

    public IReadOnlyList<OpenFinding> OpenFindings() => _tracker.OpenFindings();

    /// <summary>Follow-up (2026-11-03, review R1-56 MINOR-2): the servers this rule holds alert state for.</summary>
    public IReadOnlyCollection<string> Tracked() => _tracker.Tracked();

    /// <summary>Follow-up (2026-11-03, review R1-56 MINOR-2): drops what is learned about a removed server and closes its
    /// reported anomaly and drift alerts, unconfirmed (<see cref="BreachTracker.Close"/>).</summary>
    public IReadOnlyList<HostLoadFinding> Close(string serverId, DateTime atUtc, string reason)
    {
        foreach (var key in _series.Keys.Where(k => k.StartsWith(serverId + "|", StringComparison.Ordinal)).ToList())
            _series.Remove(key);
        return _tracker.Close(serverId, atUtc, reason);
    }
}
