using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Metrics;

/// <summary>One series' identity: a server, or a container on a server (<see cref="ContainerId"/> empty for a server).</summary>
internal readonly record struct SeriesKey(string ServerId, string ContainerId);

/// <summary>R1-55: one window being built — sums weighted by sample count and the true maximum per value.</summary>
internal sealed class WindowStats(SeriesKey key, DateTime start)
{
    public const int Values = 3;

    public SeriesKey Key { get; } = key;
    public DateTime Start { get; } = start;
    public string Name { get; private set; } = "";
    private DateTime _nameAt = DateTime.MinValue;
    public int Count { get; private set; }
    private readonly double[] _sum = new double[Values];
    private readonly double[] _max = [double.MinValue, double.MinValue, double.MinValue];

    /// <summary>Adds <paramref name="count"/> samples whose averages are <paramref name="avg"/> and maxima
    /// <paramref name="max"/> (a raw sample: count 1, avg = max = its value).</summary>
    public void Add(int count, string name, DateTime at, ReadOnlySpan<double> avg, ReadOnlySpan<double> max)
    {
        if (count <= 0) return;
        Count += count;
        for (var i = 0; i < Values; i++)
        {
            _sum[i] += avg[i] * count;
            if (max[i] > _max[i]) _max[i] = max[i];
        }
        if (at >= _nameAt) { _nameAt = at; Name = name; }   // the newest name of the window
    }

    public double Avg(int i) => Count == 0 ? 0 : _sum[i] / Count;
    public double Max(int i) => Count == 0 ? 0 : _max[i];
}

/// <summary>
/// R1-55: the storage of one kind of series (containers or servers) across its raw table and its roll-up table, so the
/// roll-up, the retention and the query run the same code for both. Value index 0 is CPU %, 1 memory %, 2 memory bytes.
/// Tier 0 is the raw table; any other tier is its window length in minutes.
/// </summary>
internal abstract class MetricSeriesKind
{
    public abstract string Name { get; }

    public static IReadOnlyList<MetricSeriesKind> All { get; } = [new ContainerSeries(), new ServerSeries()];

    /// <summary>The oldest timestamp (raw) or window start (roll-up) at or after <paramref name="from"/>, or null.</summary>
    public abstract Task<DateTime?> FirstAsync(MetricsDbContext db, int tier, DateTime from, CancellationToken ct);

    /// <summary>The oldest entry of one series in a tier, or null.</summary>
    public abstract Task<DateTime?> FirstOfAsync(MetricsDbContext db, int tier, SeriesKey key, CancellationToken ct);

    /// <summary>Everything of a tier in [<paramref name="from"/>, <paramref name="to"/>), grouped into windows of
    /// <paramref name="window"/>.</summary>
    public async Task<List<WindowStats>> AggregateAsync(MetricsDbContext db, int tier, DateTime from, DateTime to,
        TimeSpan window, CancellationToken ct)
    {
        var windows = new Dictionary<(SeriesKey, DateTime), WindowStats>();
        await foreach (var e in ReadAsync(db, tier, null, from, to, ct))
        {
            var start = MetricTiers.Floor(e.At, window);
            if (!windows.TryGetValue((e.Key, start), out var w))
                windows[(e.Key, start)] = w = new WindowStats(e.Key, start);
            w.Add(e.Count, e.Name, e.At, e.Avg, e.Max);
        }
        return windows.Values.ToList();
    }

    /// <summary>One entry of a tier: a raw sample (count 1) or a rolled-up window.</summary>
    internal sealed record Entry(SeriesKey Key, string Name, DateTime At, int Count, double[] Avg, double[] Max);

    /// <summary>The entries of a tier in [<paramref name="from"/>, <paramref name="to"/>), oldest first; of one series when
    /// <paramref name="key"/> is set.</summary>
    public abstract IAsyncEnumerable<Entry> ReadAsync(MetricsDbContext db, int tier, SeriesKey? key, DateTime from,
        DateTime to, CancellationToken ct);

    /// <summary>Removes a roll-up tier's windows in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public abstract Task DeleteAsync(MetricsDbContext db, int tier, DateTime from, DateTime to, CancellationToken ct);

    public abstract void Add(MetricsDbContext db, int tier, IEnumerable<WindowStats> windows);

    /// <summary>Removes everything of a tier older than <paramref name="cutoff"/>; returns how many rows.</summary>
    public abstract Task<int> PruneAsync(MetricsDbContext db, int tier, DateTime cutoff, CancellationToken ct);

    internal static double Percent(double used, double total) => total > 0 ? used / total * 100 : 0;
}

internal sealed class ContainerSeries : MetricSeriesKind
{
    public override string Name => MetricTiers.ContainerKind;

    public override async Task<DateTime?> FirstAsync(MetricsDbContext db, int tier, DateTime from, CancellationToken ct) =>
        tier == 0
            ? await db.ContainerMetrics.Where(m => m.Timestamp >= from).OrderBy(m => m.Timestamp)
                .Select(m => (DateTime?)m.Timestamp).FirstOrDefaultAsync(ct)
            : await db.ContainerMetricRollups.Where(r => r.TierMinutes == tier && r.WindowStart >= from)
                .OrderBy(r => r.WindowStart).Select(r => (DateTime?)r.WindowStart).FirstOrDefaultAsync(ct);

    public override async Task<DateTime?> FirstOfAsync(MetricsDbContext db, int tier, SeriesKey key, CancellationToken ct) =>
        tier == 0
            ? await db.ContainerMetrics.Where(m => m.ContainerId == key.ContainerId && m.ServerId == key.ServerId)
                .OrderBy(m => m.Timestamp).Select(m => (DateTime?)m.Timestamp).FirstOrDefaultAsync(ct)
            : await db.ContainerMetricRollups.Where(r => r.TierMinutes == tier && r.ServerId == key.ServerId && r.ContainerId == key.ContainerId)
                .OrderBy(r => r.WindowStart).Select(r => (DateTime?)r.WindowStart).FirstOrDefaultAsync(ct);

    public override async IAsyncEnumerable<Entry> ReadAsync(MetricsDbContext db, int tier, SeriesKey? key, DateTime from,
        DateTime to, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (tier == 0)
        {
            var q = db.ContainerMetrics.AsNoTracking().Where(m => m.Timestamp >= from && m.Timestamp < to);
            if (key is { } k) q = q.Where(m => m.ContainerId == k.ContainerId && m.ServerId == k.ServerId);
            await foreach (var m in q.OrderBy(m => m.Timestamp)
                               .Select(m => new { m.ServerId, m.ContainerId, m.ContainerName, m.Timestamp, m.CpuPercent, m.MemoryUsageBytes, m.MemoryLimitBytes })
                               .AsAsyncEnumerable().WithCancellation(ct))
            {
                double[] v = [m.CpuPercent, Percent(m.MemoryUsageBytes, m.MemoryLimitBytes), m.MemoryUsageBytes];
                yield return new Entry(new SeriesKey(m.ServerId, m.ContainerId), m.ContainerName, m.Timestamp, 1, v, v);
            }
            yield break;
        }

        var r = db.ContainerMetricRollups.AsNoTracking().Where(x => x.TierMinutes == tier && x.WindowStart >= from && x.WindowStart < to);
        if (key is { } rk) r = r.Where(x => x.ServerId == rk.ServerId && x.ContainerId == rk.ContainerId);
        await foreach (var x in r.OrderBy(x => x.WindowStart).AsAsyncEnumerable().WithCancellation(ct))
            yield return new Entry(new SeriesKey(x.ServerId, x.ContainerId), x.ContainerName, x.WindowStart, x.SampleCount,
                [x.CpuAvg, x.MemoryPercentAvg, x.MemoryUsageBytesAvg], [x.CpuMax, x.MemoryPercentMax, x.MemoryUsageBytesMax]);
    }

    public override Task DeleteAsync(MetricsDbContext db, int tier, DateTime from, DateTime to, CancellationToken ct) =>
        db.ContainerMetricRollups.Where(r => r.TierMinutes == tier && r.WindowStart >= from && r.WindowStart < to)
            .ExecuteDeleteAsync(ct);

    public override void Add(MetricsDbContext db, int tier, IEnumerable<WindowStats> windows) =>
        db.ContainerMetricRollups.AddRange(windows.Select(w => new ContainerMetricRollupEntity
        {
            TierMinutes = tier,
            ServerId = w.Key.ServerId,
            ContainerId = w.Key.ContainerId,
            ContainerName = w.Name,
            WindowStart = w.Start,
            SampleCount = w.Count,
            CpuAvg = w.Avg(0), CpuMax = w.Max(0),
            MemoryPercentAvg = w.Avg(1), MemoryPercentMax = w.Max(1),
            MemoryUsageBytesAvg = w.Avg(2), MemoryUsageBytesMax = w.Max(2),
        }));

    public override Task<int> PruneAsync(MetricsDbContext db, int tier, DateTime cutoff, CancellationToken ct) =>
        tier == 0
            ? db.ContainerMetrics.Where(m => m.Timestamp < cutoff).ExecuteDeleteAsync(ct)
            : db.ContainerMetricRollups.Where(r => r.TierMinutes == tier && r.WindowStart < cutoff).ExecuteDeleteAsync(ct);
}

internal sealed class ServerSeries : MetricSeriesKind
{
    public override string Name => MetricTiers.ServerKind;

    public override async Task<DateTime?> FirstAsync(MetricsDbContext db, int tier, DateTime from, CancellationToken ct) =>
        tier == 0
            ? await db.ServerMetrics.Where(m => m.Timestamp >= from).OrderBy(m => m.Timestamp)
                .Select(m => (DateTime?)m.Timestamp).FirstOrDefaultAsync(ct)
            : await db.ServerMetricRollups.Where(r => r.TierMinutes == tier && r.WindowStart >= from)
                .OrderBy(r => r.WindowStart).Select(r => (DateTime?)r.WindowStart).FirstOrDefaultAsync(ct);

    public override async Task<DateTime?> FirstOfAsync(MetricsDbContext db, int tier, SeriesKey key, CancellationToken ct) =>
        tier == 0
            ? await db.ServerMetrics.Where(m => m.ServerId == key.ServerId)
                .OrderBy(m => m.Timestamp).Select(m => (DateTime?)m.Timestamp).FirstOrDefaultAsync(ct)
            : await db.ServerMetricRollups.Where(r => r.TierMinutes == tier && r.ServerId == key.ServerId)
                .OrderBy(r => r.WindowStart).Select(r => (DateTime?)r.WindowStart).FirstOrDefaultAsync(ct);

    public override async IAsyncEnumerable<Entry> ReadAsync(MetricsDbContext db, int tier, SeriesKey? key, DateTime from,
        DateTime to, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (tier == 0)
        {
            var q = db.ServerMetrics.AsNoTracking().Where(m => m.Timestamp >= from && m.Timestamp < to);
            if (key is { } k) q = q.Where(m => m.ServerId == k.ServerId);
            await foreach (var m in q.OrderBy(m => m.Timestamp)
                               .Select(m => new { m.ServerId, m.ServerName, m.Timestamp, m.CpuPercent, m.MemoryUsedBytes, m.MemoryTotalBytes })
                               .AsAsyncEnumerable().WithCancellation(ct))
            {
                double[] v = [m.CpuPercent, Percent(m.MemoryUsedBytes, m.MemoryTotalBytes), m.MemoryUsedBytes];
                yield return new Entry(new SeriesKey(m.ServerId, ""), m.ServerName, m.Timestamp, 1, v, v);
            }
            yield break;
        }

        var r = db.ServerMetricRollups.AsNoTracking().Where(x => x.TierMinutes == tier && x.WindowStart >= from && x.WindowStart < to);
        if (key is { } rk) r = r.Where(x => x.ServerId == rk.ServerId);
        await foreach (var x in r.OrderBy(x => x.WindowStart).AsAsyncEnumerable().WithCancellation(ct))
            yield return new Entry(new SeriesKey(x.ServerId, ""), x.ServerName, x.WindowStart, x.SampleCount,
                [x.CpuAvg, x.MemoryPercentAvg, x.MemoryUsedBytesAvg], [x.CpuMax, x.MemoryPercentMax, x.MemoryUsedBytesMax]);
    }

    public override Task DeleteAsync(MetricsDbContext db, int tier, DateTime from, DateTime to, CancellationToken ct) =>
        db.ServerMetricRollups.Where(r => r.TierMinutes == tier && r.WindowStart >= from && r.WindowStart < to)
            .ExecuteDeleteAsync(ct);

    public override void Add(MetricsDbContext db, int tier, IEnumerable<WindowStats> windows) =>
        db.ServerMetricRollups.AddRange(windows.Select(w => new ServerMetricRollupEntity
        {
            TierMinutes = tier,
            ServerId = w.Key.ServerId,
            ServerName = w.Name,
            WindowStart = w.Start,
            SampleCount = w.Count,
            CpuAvg = w.Avg(0), CpuMax = w.Max(0),
            MemoryPercentAvg = w.Avg(1), MemoryPercentMax = w.Max(1),
            MemoryUsedBytesAvg = w.Avg(2), MemoryUsedBytesMax = w.Max(2),
        }));

    public override Task<int> PruneAsync(MetricsDbContext db, int tier, DateTime cutoff, CancellationToken ct) =>
        tier == 0
            ? db.ServerMetrics.Where(m => m.Timestamp < cutoff).ExecuteDeleteAsync(ct)
            : db.ServerMetricRollups.Where(r => r.TierMinutes == tier && r.WindowStart < cutoff).ExecuteDeleteAsync(ct);
}
