using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models;
using ServerWatch.Services.Docker;
using ServerWatch.Services.Notifications;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Metrics;

public class MetricsCollectorService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<MetricAlertSettings> _alertSettings;
    private readonly ILogger<MetricsCollectorService> _logger;
    private readonly ConcurrentDictionary<string, AlertState> _alert = new();

    public MetricsCollectorService(
        IServiceProvider services,
        IOptionsMonitor<MetricAlertSettings> alertSettings,
        ILogger<MetricsCollectorService> logger)
    {
        _services = services;
        _alertSettings = alertSettings;
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
        var notify = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var alertCfg = _alertSettings.CurrentValue;

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
                    if (alertCfg.Enabled)
                    {
                        try { await EvaluateAlertsAsync(c, stats, alertCfg, notify); }
                        catch (Exception aex) { _logger.LogDebug(aex, "Metric alert evaluation failed for {Container}", c.Name); }
                    }

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

        // Audit log has a longer retention (90 days). Timestamp is indexed.
        var cutoff90d = DateTime.UtcNow.AddDays(-90);
        await db.AuditLog.Where(e => e.Timestamp < cutoff90d).ExecuteDeleteAsync(ct);

        _logger.LogDebug("Collected metrics for {ContainerCount} containers; pruned data before {Cutoff}",
            metrics.Count, cutoff);
    }

    /// <summary>Per-container threshold (sustained high CPU/RAM) + simple anomaly (rolling z-score).
    /// Emits NotificationEvents that flow through the pipeline and can drive AI triggers.</summary>
    private async Task EvaluateAlertsAsync(ContainerInfo c, ContainerStats stats, MetricAlertSettings cfg, INotificationService notify)
    {
        var st = _alert.GetOrAdd(c.Id, _ => new AlertState());
        var now = DateTime.UtcNow;
        var cpu = stats.CpuPercent;
        var mem = stats.MemoryLimitBytes > 0 ? stats.MemoryUsageBytes * 100.0 / stats.MemoryLimitBytes : 0;
        var sustained = Math.Max(1, cfg.SustainedMinutes * 2); // 30s sampling interval

        // --- Sustained-threshold ---
        st.CpuOver = cpu >= cfg.CpuPercent ? st.CpuOver + 1 : 0;
        if (st.CpuOver >= sustained && now >= st.CpuCooldown)
        {
            st.CpuOver = 0;
            st.CpuCooldown = now.AddMinutes(cfg.CooldownMinutes);
            await Emit(notify, c, "high_cpu", $"CPU {cpu:F0}% seit ≥{cfg.SustainedMinutes} Min (Schwelle {cfg.CpuPercent:F0}%).");
        }

        st.MemOver = mem >= cfg.MemoryPercent ? st.MemOver + 1 : 0;
        if (st.MemOver >= sustained && now >= st.MemCooldown)
        {
            st.MemOver = 0;
            st.MemCooldown = now.AddMinutes(cfg.CooldownMinutes);
            await Emit(notify, c, "high_memory", $"RAM {mem:F0}% des Limits seit ≥{cfg.SustainedMinutes} Min (Schwelle {cfg.MemoryPercent:F0}%).");
        }

        // --- Simple anomaly (rolling z-score over previous window) ---
        if (cfg.AnomalyEnabled)
        {
            if (Anomalous(st.CpuWin, cpu, cfg) && now >= st.AnomCooldown)
            {
                st.AnomCooldown = now.AddMinutes(cfg.CooldownMinutes);
                await Emit(notify, c, "metric_anomaly", $"CPU-Ausreißer: {cpu:F0}% (Baseline-Mittel der letzten {cfg.AnomalyWindow} Samples deutlich niedriger).");
            }
            else if (Anomalous(st.MemWin, mem, cfg) && now >= st.AnomCooldown)
            {
                st.AnomCooldown = now.AddMinutes(cfg.CooldownMinutes);
                await Emit(notify, c, "metric_anomaly", $"RAM-Ausreißer: {mem:F0}% (Baseline-Mittel der letzten {cfg.AnomalyWindow} Samples deutlich niedriger).");
            }
            Push(st.CpuWin, cpu, cfg.AnomalyWindow);
            Push(st.MemWin, mem, cfg.AnomalyWindow);
        }
    }

    private static bool Anomalous(Queue<double> window, double value, MetricAlertSettings cfg)
    {
        if (window.Count < cfg.AnomalyWindow || value < cfg.AnomalyFloorPercent) return false;
        var mean = window.Average();
        var variance = window.Select(v => (v - mean) * (v - mean)).Average();
        var std = Math.Sqrt(variance);
        return std > 0.001 && value > mean + cfg.AnomalySigma * std;
    }

    private static void Push(Queue<double> window, double value, int max)
    {
        window.Enqueue(value);
        while (window.Count > max) window.Dequeue();
    }

    private static Task Emit(INotificationService notify, ContainerInfo c, string type, string info) =>
        notify.SendAsync(new NotificationEvent
        {
            EventType = type,
            ContainerId = c.Id,
            ContainerName = c.Name,
            Image = c.Image,
            ImageName = c.Image,
            ImageInfo = info,
        });

    private sealed class AlertState
    {
        public int CpuOver;
        public int MemOver;
        public DateTime CpuCooldown;
        public DateTime MemCooldown;
        public DateTime AnomCooldown;
        public readonly Queue<double> CpuWin = new();
        public readonly Queue<double> MemWin = new();
    }
}
