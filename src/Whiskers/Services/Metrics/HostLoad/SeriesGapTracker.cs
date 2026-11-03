using Whiskers.Services.Notifications;

namespace Whiskers.Services.Metrics.HostLoad;

/// <summary>
/// R1-56: a series that stops delivering readings is never silent. A host whose node_exporter is down, a container whose
/// stats call fails, a <c>df</c> that no longer answers — before, the collector only logged a warning, and since a breach
/// cannot be confirmed across readings that were never taken (<see cref="BreachTracker"/> resets it after the gap
/// tolerance), an alert that never came would have looked like a healthy series. <c>server_unreachable</c> comes from the
/// health monitor and covers none of these cases.
///
/// <para>One <c>metrics_gap</c> finding per series once its readings have been missing for longer than the tolerance, and
/// one <c>metrics_gap_recovered</c> when they are back. A gap only noticed when the next reading arrives (the loop itself
/// was held up) is not one of the series: the collector reports it once per held-up cycle. Advances on the time of the
/// reading, like the breach tracker. Safe to call from parallel tasks.</para>
/// </summary>
public sealed class SeriesGapTracker
{
    private sealed class Series
    {
        public DateTime LastSeen;            // the last reading, or the first miss of a series never seen
        public DateTime? Announced;          // when the gap was reported (null = not reported)
    }

    private readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    /// <summary>A reading of <paramref name="seriesId"/> arrived at <paramref name="atUtc"/>. Returns the end of a reported
    /// gap, or a gap that was longer than <paramref name="tolerance"/> without being reported; otherwise null.</summary>
    public HostLoadFinding? Seen(DateTime atUtc, string seriesId, string name, string what, TimeSpan tolerance)
    {
        lock (_sync)
        {
            var known = _series.TryGetValue(seriesId, out var series);
            if (!known) { _series[seriesId] = new Series { LastSeen = atUtc }; return null; }

            var gap = atUtc - series!.LastSeen;
            var announced = series.Announced;
            series.LastSeen = atUtc;
            series.Announced = null;

            if (announced is not null)
                return new HostLoadFinding(atUtc, seriesId, name, NotificationEventTypes.MetricsGap, FindingKind.Cleared,
                    $"Readings of {what} are back after {BreachTracker.Describe(gap)} without any.", 0, 0, gap);

            // A gap nobody reported while it lasted is not this series' own: the collection loop was held up (or alerts
            // were switched off). It is not reported per series — after one slow cycle that would be one message per
            // container, host and disk at once, past every throttle (review MAJOR-3); the collector reports a held-up
            // cycle once (MetricsCollectorService) and the scan supervisor a stalled loop.
            return null;
        }
    }

    /// <summary>A reading of <paramref name="seriesId"/> that should have arrived at <paramref name="atUtc"/> did not.
    /// Returns a <c>metrics_gap</c> finding once the readings have been missing for longer than
    /// <paramref name="tolerance"/>; <paramref name="openAlerts"/> names what the gap leaves unconfirmed.</summary>
    public HostLoadFinding? Missing(DateTime atUtc, string seriesId, string name, string what, TimeSpan tolerance,
        string? reason = null, IReadOnlyCollection<string>? openAlerts = null)
    {
        lock (_sync)
        {
            if (!_series.TryGetValue(seriesId, out var series))
                _series[seriesId] = series = new Series { LastSeen = atUtc };
            if (series.Announced is not null) return null;

            var gap = atUtc - series.LastSeen;
            if (gap <= tolerance) return null;

            series.Announced = atUtc;
            var open = openAlerts is { Count: > 0 }
                ? $" Open alerts for it are closed (unconfirmed) when readings are back and raised again only if new readings confirm them: {string.Join(", ", openAlerts)}."
                : "";
            return new HostLoadFinding(atUtc, seriesId, name, NotificationEventTypes.MetricsGap, FindingKind.Raised,
                $"No readings of {what} for {BreachTracker.Describe(gap)} (tolerance {BreachTracker.Describe(tolerance)})" +
                (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}.") +
                " Threshold alerts cannot fire for it until readings are back." + open, 0, 0, gap);
        }
    }

    /// <summary>The series has no reading at <paramref name="atUtc"/> because a wider gap that is reported on its own covers
    /// it (a host without a reading has no disk reading either): counted as seen, so it is not reported a second time.
    /// A gap of its own that was already reported stays open until its readings are back.</summary>
    public void Covered(DateTime atUtc, string seriesId)
    {
        lock (_sync)
        {
            if (!_series.TryGetValue(seriesId, out var series)) _series[seriesId] = new Series { LastSeen = atUtc };
            else if (series.Announced is null) series.LastSeen = atUtc;
        }
    }

    /// <summary>Drops a series that is no longer collected (a container that is gone).</summary>
    public void Forget(string seriesId)
    {
        lock (_sync) _series.Remove(seriesId);
    }

    /// <summary>The ids of every series tracked.</summary>
    public IReadOnlyCollection<string> Tracked()
    {
        lock (_sync) return _series.Keys.ToList();
    }
}
