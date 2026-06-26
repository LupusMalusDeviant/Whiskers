using Microsoft.EntityFrameworkCore;
using ServerWatch.Services.Docker;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Metrics;

public class MetricsCollectorService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MetricsCollectorService> _logger;

    public MetricsCollectorService(IServiceProvider services, ILogger<MetricsCollectorService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Metrics collector started (interval: 30s)");
        await Task.Delay(TimeSpan.FromSeconds(10), ct); // startup delay

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CollectMetricsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Metrics collection failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    private async Task CollectMetricsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var docker = scope.ServiceProvider.GetRequiredService<IDockerService>();
        var metricsSource = scope.ServiceProvider.GetRequiredService<IMetricsSource>();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var now = DateTime.UtcNow;
        // Container listing still goes through Docker (an inventory call, not a metric); the metric
        // reads below go through IMetricsSource so Prometheus-configured servers bypass SSH.
        var containers = await docker.ListAllContainersAsync(all: false);

        // Collect container stats in parallel
        var statsTasks = containers.Select(async c =>
        {
            try
            {
                var stats = await metricsSource.GetContainerStatsAsync(c.ServerId, c.Id, c.Name);
                if (stats != null)
                {
                    return new ContainerMetricEntity
                    {
                        ContainerId = c.Id,
                        ContainerName = c.Name,
                        ServerId = c.ServerId,
                        Timestamp = now,
                        CpuPercent = stats.CpuPercent,
                        MemoryUsageBytes = stats.MemoryUsageBytes,
                        MemoryLimitBytes = stats.MemoryLimitBytes,
                        NetworkRxBytes = stats.NetworkRxBytes,
                        NetworkTxBytes = stats.NetworkTxBytes,
                        BlockReadBytes = stats.BlockReadBytes,
                        BlockWriteBytes = stats.BlockWriteBytes
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to collect stats for container {ContainerId} on {ServerId}", c.Id, c.ServerId);
            }
            return null;
        });

        var metrics = (await Task.WhenAll(statsTasks))
            .Where(m => m != null)
            .ToList();

        if (metrics.Any())
        {
            db.ContainerMetrics.AddRange(metrics!);
        }

        // Collect server metrics
        try
        {
            var serverInfos = await metricsSource.GetAllServerSystemInfoAsync();
            foreach (var (serverId, info) in serverInfos)
            {
                if (!info.IsReachable) continue;

                db.ServerMetrics.Add(new ServerMetricEntity
                {
                    ServerId = serverId,
                    ServerName = info.ServerName,
                    Timestamp = now,
                    CpuPercent = info.CpuUsagePercent,
                    MemoryUsedBytes = info.MemoryUsedBytes,
                    MemoryTotalBytes = info.MemoryTotalBytes
                    // DiskUsed/DiskTotal will be populated later when we add disk monitoring
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect server metrics");
        }

        await db.SaveChangesAsync(ct);

        // Prune old data (keep 7 days)
        var cutoff = now.AddDays(-7);
        await db.ContainerMetrics.Where(m => m.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        await db.ServerMetrics.Where(m => m.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        await db.AlertHistory.Where(a => a.Timestamp < cutoff).ExecuteDeleteAsync(ct);

        _logger.LogDebug("Collected metrics for {ContainerCount} containers; pruned data before {Cutoff}",
            metrics.Count, cutoff);
    }
}
