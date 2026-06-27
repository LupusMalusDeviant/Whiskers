using Microsoft.EntityFrameworkCore;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Metrics;

public class MetricPoint
{
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
}

public class MetricsQueryService : IMetricsQueryService
{
    private readonly IServiceProvider _services;

    public MetricsQueryService(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<List<MetricPoint>> GetContainerCpuHistoryAsync(
        string containerId, string serverId, TimeSpan period)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var since = DateTime.UtcNow - period;

        var metrics = await db.ContainerMetrics
            .Where(m => m.ContainerId == containerId && m.ServerId == serverId && m.Timestamp >= since)
            .OrderBy(m => m.Timestamp)
            .Select(m => new MetricPoint { Timestamp = m.Timestamp, Value = m.CpuPercent })
            .ToListAsync();

        return Downsample(metrics, 100);
    }

    public async Task<List<MetricPoint>> GetContainerMemoryHistoryAsync(
        string containerId, string serverId, TimeSpan period)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var since = DateTime.UtcNow - period;

        return await db.ContainerMetrics
            .Where(m => m.ContainerId == containerId && m.ServerId == serverId && m.Timestamp >= since)
            .OrderBy(m => m.Timestamp)
            .Select(m => new MetricPoint
            {
                Timestamp = m.Timestamp,
                Value = m.MemoryLimitBytes > 0 ? (double)m.MemoryUsageBytes / m.MemoryLimitBytes * 100 : 0
            })
            .ToListAsync();
    }

    public async Task<List<MetricPoint>> GetServerCpuHistoryAsync(string serverId, TimeSpan period)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var since = DateTime.UtcNow - period;

        return Downsample(await db.ServerMetrics
            .Where(m => m.ServerId == serverId && m.Timestamp >= since)
            .OrderBy(m => m.Timestamp)
            .Select(m => new MetricPoint { Timestamp = m.Timestamp, Value = m.CpuPercent })
            .ToListAsync(), 100);
    }

    public async Task<List<MetricPoint>> GetServerMemoryHistoryAsync(string serverId, TimeSpan period)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var since = DateTime.UtcNow - period;

        return Downsample(await db.ServerMetrics
            .Where(m => m.ServerId == serverId && m.Timestamp >= since)
            .OrderBy(m => m.Timestamp)
            .Select(m => new MetricPoint
            {
                Timestamp = m.Timestamp,
                Value = m.MemoryTotalBytes > 0 ? (double)m.MemoryUsedBytes / m.MemoryTotalBytes * 100 : 0
            })
            .ToListAsync(), 100);
    }

    private static List<MetricPoint> Downsample(List<MetricPoint> points, int maxPoints)
    {
        if (points.Count <= maxPoints) return points;

        var step = (double)points.Count / maxPoints;
        var result = new List<MetricPoint>(maxPoints);

        for (int i = 0; i < maxPoints; i++)
        {
            var start = (int)(i * step);
            var end = (int)((i + 1) * step);
            if (end > points.Count) end = points.Count;

            var bucket = points.Skip(start).Take(end - start).ToList();
            if (bucket.Any())
            {
                result.Add(new MetricPoint
                {
                    Timestamp = bucket[bucket.Count / 2].Timestamp,
                    Value = bucket.Average(p => p.Value)
                });
            }
        }

        return result;
    }
}
