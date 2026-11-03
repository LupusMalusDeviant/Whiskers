using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Metrics;

/// <summary>
/// One point of a metric history. R1-55: a point stands for one or more samples — a raw sample, a rolled-up window or
/// several of them merged to fit the chart — and carries their sample-weighted average (<see cref="Value"/>), their true
/// maximum (<see cref="Max"/>) and how many samples are behind it (<see cref="Samples"/>).
/// </summary>
public class MetricPoint
{
    /// <summary>The start of the time the point covers (UTC).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>The sample-weighted average.</summary>
    public double Value { get; set; }

    /// <summary>The highest original sample — never an average of averages.</summary>
    public double Max { get; set; }

    public int Samples { get; set; } = 1;
}

/// <summary>R1-55: what a whole history says — a weighted average and the true peak, never the mean of point averages.</summary>
public static class MetricSummary
{
    public static double Average(IReadOnlyCollection<MetricPoint> points)
    {
        var samples = points.Sum(p => (double)Math.Max(1, p.Samples));
        return samples == 0 ? 0 : points.Sum(p => p.Value * Math.Max(1, p.Samples)) / samples;
    }

    public static double Peak(IReadOnlyCollection<MetricPoint> points) => points.Count == 0 ? 0 : points.Max(p => p.Max);
}

/// <summary>
/// R1-55: reads a metric history across the tiers. For a range it picks the tier whose window still gives the chart
/// enough points (<see cref="PreferredTier"/>), then stitches at window boundaries: the coarser tiers for the part older
/// than that tier holds, the finer tiers and the raw samples for the part newer than its watermark — so the whole range
/// is covered without gaps or duplicate timestamps, and the last minutes come straight from the raw samples. The result
/// is thinned to the point budget keeping every point's weighted average and true maximum.
/// </summary>
public class MetricsQueryService : IMetricsQueryService
{
    /// <summary>The chart's point budget.</summary>
    public const int MaxPoints = 100;

    private const int Cpu = 0, MemoryPercent = 1;

    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;

    public MetricsQueryService(IServiceProvider services, TimeProvider? time = null)
    {
        _services = services;
        _time = time ?? TimeProvider.System;
    }

    public Task<List<MetricPoint>> GetContainerCpuHistoryAsync(string containerId, string serverId, TimeSpan period) =>
        QueryAsync(new ContainerSeries(), new SeriesKey(serverId, containerId), Cpu, period);

    public Task<List<MetricPoint>> GetContainerMemoryHistoryAsync(string containerId, string serverId, TimeSpan period) =>
        QueryAsync(new ContainerSeries(), new SeriesKey(serverId, containerId), MemoryPercent, period);

    public Task<List<MetricPoint>> GetServerCpuHistoryAsync(string serverId, TimeSpan period) =>
        QueryAsync(new ServerSeries(), new SeriesKey(serverId, ""), Cpu, period);

    public Task<List<MetricPoint>> GetServerMemoryHistoryAsync(string serverId, TimeSpan period) =>
        QueryAsync(new ServerSeries(), new SeriesKey(serverId, ""), MemoryPercent, period);

    /// <summary>The tier a range is read from by preference: the coarsest whose window is no longer than the range divided
    /// by the point budget (raw for 1 h and 6 h, 10 min for 24 h, 1 h for 7 and 30 days).</summary>
    internal static int PreferredTier(TimeSpan period)
    {
        var bucket = period / MaxPoints;
        var preferred = 0;
        foreach (var tier in MetricTiers.Minutes)
            if (MetricTiers.Size(tier) <= bucket) preferred = tier;
        return preferred;
    }

    /// <summary>One stretch of the range read from one tier: entries at [<see cref="From"/>, <see cref="To"/>).</summary>
    internal sealed record Segment(int Tier, DateTime From, DateTime To);

    private async Task<List<MetricPoint>> QueryAsync(MetricSeriesKind kind, SeriesKey key, int value, TimeSpan period)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var now = _time.GetUtcNow().UtcDateTime;
        var since = now - period;

        var segments = await PlanAsync(db, kind, key, since, now, PreferredTier(period), CancellationToken.None);
        var points = new List<MetricPoint>();
        foreach (var s in segments)
            await foreach (var e in kind.ReadAsync(db, s.Tier, key, s.From, s.To, CancellationToken.None))
                points.Add(new MetricPoint { Timestamp = e.At, Value = e.Avg[value], Max = e.Max[value], Samples = e.Count });
        return Thin(points, MaxPoints);
    }

    /// <summary>The segments that cover [<paramref name="since"/>, <paramref name="now"/>], oldest first, adjacent and
    /// non-overlapping. Levels: 0 = raw, then the tiers. A tier finer than the preferred one serves only what is newer than
    /// the next coarser tier's watermark; the preferred tier serves from where it is complete for this series (rounded up
    /// to the next coarser window, or that tier's watermark if lower) to its own watermark; the coarser tiers serve what is
    /// older.</summary>
    internal static async Task<List<Segment>> PlanAsync(MetricsDbContext db, MetricSeriesKind kind, SeriesKey key,
        DateTime since, DateTime now, int preferred, CancellationToken ct)
    {
        int[] levels = [0, .. MetricTiers.Minutes];
        var p = Array.IndexOf(levels, preferred);
        var prefix = kind.Name + "/";
        var marks = await db.MetricRollupStates.AsNoTracking().Where(s => s.Id.StartsWith(prefix))
            .ToDictionaryAsync(s => s.Id, s => s.RolledUpTo, ct);
        // The exclusive end of what a level holds: raw goes up to now (inclusive), a tier up to its watermark.
        var end = now.AddTicks(1);
        DateTime Upper(int i) => i == 0 ? end
            : marks.TryGetValue(MetricTiers.StateId(kind.Name, levels[i]), out var m) ? m : DateTime.MinValue;

        var segments = new List<Segment>();
        // Finer than preferred: the recent part, newest last.
        for (var i = p - 1; i >= 0; i--) segments.Add(new Segment(levels[i], Upper(i + 1), Upper(i)));

        // Preferred and coarser: each from where it is complete for this series to where the finer one starts.
        var upper = Upper(p);
        for (var i = p; i < levels.Length; i++)
        {
            DateTime lower;
            if (i == levels.Length - 1) lower = DateTime.MinValue;
            else
            {
                var first = await kind.FirstOfAsync(db, levels[i], key, ct);
                var coarser = Upper(i + 1);
                if (first is null) lower = upper;   // nothing at this level: the coarser one serves it all
                else
                {
                    var boundary = MetricTiers.Ceiling(first.Value, MetricTiers.Size(levels[i + 1]));
                    lower = boundary < coarser ? boundary : coarser;
                    if (lower > upper) lower = upper;
                }
            }
            segments.Add(new Segment(levels[i], lower, upper));
            upper = lower;
            if (upper <= since) break;
        }

        // Clip to the range: a window that overlaps its start is kept whole (its start may lie before it).
        return segments
            .Select(s =>
            {
                var from = s.Tier == 0 ? since : MetricTiers.Floor(since, MetricTiers.Size(s.Tier));
                return s with { From = s.From > from ? s.From : from, To = s.To < end ? s.To : end };
            })
            .Where(s => s.From < s.To)
            .OrderBy(s => s.From)
            .ToList();
    }

    /// <summary>Merges neighbouring points into at most <paramref name="maxPoints"/> equal stretches of time: each keeps the
    /// sample-weighted average, the maximum of its members' maxima and their sample count.</summary>
    internal static List<MetricPoint> Thin(List<MetricPoint> points, int maxPoints)
    {
        if (points.Count <= maxPoints) return points;
        var first = points[0].Timestamp;
        var span = (points[^1].Timestamp - first).Ticks + 1;
        var result = new List<MetricPoint>(maxPoints);
        MetricPoint? current = null;
        var currentBucket = -1L;
        double sum = 0;
        foreach (var p in points)
        {
            var bucket = (p.Timestamp - first).Ticks * maxPoints / span;
            if (current is null || bucket != currentBucket)
            {
                if (current is not null) { current.Value = sum / current.Samples; result.Add(current); }
                current = new MetricPoint { Timestamp = p.Timestamp, Max = double.MinValue, Samples = 0 };
                currentBucket = bucket;
                sum = 0;
            }
            var n = Math.Max(1, p.Samples);
            current.Samples += n;
            sum += p.Value * n;
            if (p.Max > current.Max) current.Max = p.Max;
        }
        current!.Value = sum / current.Samples;
        result.Add(current);
        return result;
    }
}
