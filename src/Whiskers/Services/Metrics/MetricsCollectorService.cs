using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Metrics;

public class MetricsCollectorService : Whiskers.Services.FleetBackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<MetricAlertSettings> _alertSettings;
    private readonly IOptionsMonitor<MetricsSettings> _metricsSettings;
    private readonly ILogger<MetricsCollectorService> _logger;
    private readonly Whiskers.Services.Observability.SelfMetrics.ISelfMetrics _selfMetrics;
    private readonly ConcurrentDictionary<string, AlertState> _alert = new();

    // Host-level rules (Plan-0004). Held here rather than injected: the breach state is this loop's state,
    // exactly like _alert above, and it advances on sample time so it can also be driven by a replay.
    // Injected rather than owned: /metrics reads its open-findings list (Plan-0004 WP5.4), and a private
    // field would have made the "is the closing path working?" number the one number nobody can see.
    private readonly Whiskers.Services.Metrics.HostLoad.HostLoadEvaluator _hostLoad;
    private readonly Whiskers.Services.Metrics.HostLoad.ApiLatencyEvaluator _apiLatency;
    private readonly Whiskers.Services.Metrics.HostLoad.RollingBaseline _baseline;
    private DateTime _lastPrune;                 // OPT-2: prune at most hourly, not every 30s cycle
    private DateTime _lastAuditPrune;            // R1-23: the audit trail daily — each run verifies the whole trail first
    private const int MaxStatsConcurrency = 8;   // OPT-11.2: bound the per-container stats fan-out

    public MetricsCollectorService(
        IServiceProvider services,
        IOptionsMonitor<MetricAlertSettings> alertSettings,
        Whiskers.Services.Metrics.HostLoad.HostLoadEvaluator hostLoad,
        Whiskers.Services.Metrics.HostLoad.ApiLatencyEvaluator apiLatency,
        Whiskers.Services.Metrics.HostLoad.RollingBaseline baseline,
        IOptionsMonitor<MetricsSettings> metricsSettings,
        ILogger<MetricsCollectorService> logger,
        Whiskers.Services.Observability.SelfMetrics.ISelfMetrics selfMetrics)
    {
        _services = services;
        _alertSettings = alertSettings;
        _hostLoad = hostLoad;
        _apiLatency = apiLatency;
        _baseline = baseline;
        _metricsSettings = metricsSettings;
        _logger = logger;
        _selfMetrics = selfMetrics;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        var startup = _metricsSettings.CurrentValue;
        _logger.LogInformation("Metrics collector started (interval: {Interval}s, retention: {Retention}d, enabled: {Enabled})",
            startup.CollectionIntervalSeconds, startup.RetentionDays, startup.Enabled);
        await Task.Delay(TimeSpan.FromSeconds(10), ct); // startup delay

        while (!ct.IsCancellationRequested)
        {
            // Re-read every cycle so reload-on-change settings take effect without a restart.
            var cfg = _metricsSettings.CurrentValue;
            if (cfg.Enabled)
            {
                var cycleStart = DateTime.UtcNow;
                var cycleOk = false;
                try
                {
                    await CollectMetricsAsync(ct);
                    cycleOk = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Metrics collection failed");
                }

                // One record per Docker server: the age of the last success is what reveals a collector that
                // has quietly stopped, since a stalled loop produces no failures either.
                foreach (var server in SelfMetricsServers())
                    _selfMetrics.RecordCycle(
                        Whiskers.Services.Observability.SelfMetrics.SelfMetricsFleetExtensions.Loops.Metrics,
                        server, DateTime.UtcNow - cycleStart, cycleOk,
                        interval: TimeSpan.FromSeconds(Math.Max(5, cfg.CollectionIntervalSeconds)));
            }

            // Floor the interval so a 0/negative misconfiguration cannot spin a hot loop.
            var intervalSeconds = Math.Max(5, cfg.CollectionIntervalSeconds);
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }
    }

    private async Task CollectMetricsAsync(CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        using var scope = _services.CreateScope();
        var docker = scope.ServiceProvider.GetRequiredService<IDockerService>();

        // Kubernetes servers are filtered out further down the stack; record the skip so they do not simply
        // vanish from the metrics and read as "nothing to report" (Plan-0003 WP2).
        var servers = scope.ServiceProvider.GetRequiredService<Whiskers.Services.ServerConfig.IServerConfigService>()
            .GetEnabledServers();
        Whiskers.Services.Observability.SelfMetrics.SelfMetricsFleetExtensions.RecordKubernetesSkips(_selfMetrics, Whiskers.Services.Observability.SelfMetrics.SelfMetricsFleetExtensions.Loops.Metrics, servers);
        var metricsSource = scope.ServiceProvider.GetRequiredService<IMetricsSource>();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var notify = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var executor = scope.ServiceProvider.GetRequiredService<Whiskers.Services.Server.IHostCommandExecutor>();
        var alertCfg = _alertSettings.CurrentValue;

        var now = DateTime.UtcNow;
        // Container listing still goes through Docker (an inventory call, not a metric); the metric
        // reads below go through IMetricsSource so Prometheus-configured servers bypass SSH.
        var containers = await docker.ListAllContainersAsync(all: false);

        // Collect container stats in parallel, but bounded so a large fleet can't fan out into
        // hundreds of concurrent stats calls at once.
        using var statsGate = new SemaphoreSlim(MaxStatsConcurrency);
        var statsTasks = containers.Select(async c =>
        {
            await statsGate.WaitAsync(ct);
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
            finally
            {
                statsGate.Release();
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

                // Host root-filesystem usage via df (the metrics sources don't carry disk yet).
                var (diskUsed, diskTotal) = await GetDiskUsageAsync(executor, serverId, ct);

                db.ServerMetrics.Add(new ServerMetricEntity
                {
                    ServerId = serverId,
                    ServerName = info.ServerName,
                    Timestamp = now,
                    CpuPercent = info.CpuUsagePercent,
                    MemoryUsedBytes = info.MemoryUsedBytes,
                    MemoryTotalBytes = info.MemoryTotalBytes,
                    DiskUsedBytes = diskUsed,
                    DiskTotalBytes = diskTotal,
                });

                if (alertCfg.Enabled && diskTotal > 0)
                {
                    try { await EvaluateServerDiskAsync(serverId, info.ServerName, diskUsed, diskTotal, alertCfg, notify); }
                    catch (Exception aex) { _logger.LogDebug(aex, "Disk alert evaluation failed for {ServerId}", serverId); }
                }

                // Host CPU and memory, and the part of the host load no container accounts for (Plan-0004
                // WP1/WP2). This is the gap the 2026-08-26 incident fell through: alerts were evaluated per
                // container and for disk, and dockerd runs in no container — so 8,900 measurements above 98%
                // were recorded and none of them judged. The same evaluator is driven by the incident replay
                // in HostLoadReplayTests, so what runs here is what was shown to catch it.
                if (alertCfg.Enabled)
                {
                    try
                    {
                        var containerCpuSum = metrics
                            .Where(m => m!.ServerId == serverId)
                            .Sum(m => m!.CpuPercent);

                        var sample = new Whiskers.Services.Metrics.HostLoad.HostSample(
                            now, serverId, info.ServerName,
                            info.CpuUsagePercent, containerCpuSum,
                            info.MemoryUsedBytes, info.MemoryTotalBytes,
                            info.CpuCount);

                        var findings = _hostLoad.Evaluate(sample).ToList();

                        // Plan-0004 WP4: how fast the daemon itself is answering. A separate signal from host
                        // CPU on purpose — it sees an overloaded daemon whoever is overloading it, including
                        // the case where the load is not on this host at all but on the link to it.
                        if (_selfMetrics.ApiLatencies().TryGetValue(serverId, out var latencies))
                        {
                            var slow = _apiLatency.Evaluate(now, serverId, info.ServerName, latencies);
                            if (slow is not null) findings.Add(slow);
                        }

                        // Plan-0004 WP3: deviation from this host's own normal, plus the guard that says so
                        // when the "normal" has itself drifted past the fixed threshold. The absolute limit is
                        // handed in rather than duplicated — one definition of "too high", used by both rules.
                        findings.AddRange(_baseline.Observe(
                            now, serverId, info.ServerName, "host_cpu", info.CpuUsagePercent,
                            Whiskers.Services.Metrics.HostLoad.HostLoadEvaluator.CpuThresholdOf(_hostLoad)));

                        foreach (var finding in findings)
                        {
                            _logger.Log(
                                finding.What == Whiskers.Services.Metrics.HostLoad.FindingKind.Cleared
                                    ? LogLevel.Information : LogLevel.Warning,
                                "Host {What} on {Server}: {Summary}",
                                finding.What, finding.ServerName, finding.Summary);
                            await notify.SendAsync(new NotificationEvent
                            {
                                EventType = finding.EventType,
                                ServerId = finding.ServerId,
                                ServerName = finding.ServerName,
                                ContainerName = finding.ServerName,
                                ImageInfo = finding.Summary,
                                Timestamp = finding.AtUtc
                            });
                        }
                    }
                    catch (Exception aex) { _logger.LogDebug(aex, "Host load evaluation failed for {ServerId}", serverId); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect server metrics");
        }

        await db.SaveChangesAsync(ct);

        // Prune old data at most once an hour — the retention windows are days, so pruning on every
        // 30s cycle just burns ExecuteDelete round-trips for nothing.
        if (now - _lastPrune > TimeSpan.FromHours(1))
        {
            _lastPrune = now;
            // Retention window is config-driven; floored so a 0/negative value cannot wipe live data.
            var retentionDays = Math.Max(1, _metricsSettings.CurrentValue.RetentionDays);
            var cutoff = now.AddDays(-retentionDays);
            await db.ContainerMetrics.Where(m => m.Timestamp < cutoff).ExecuteDeleteAsync(ct);
            await db.ServerMetrics.Where(m => m.Timestamp < cutoff).ExecuteDeleteAsync(ct);
            await db.AlertHistory.Where(a => a.Timestamp < cutoff).ExecuteDeleteAsync(ct);

        }

        _lastAuditPrune = await PruneAuditIfDueAsync(_services, db, now, _lastAuditPrune, ct);

        // Bound the in-memory alert-state map: drop per-container entries whose container is gone.
        // "disk:{server}" keys are kept — one per server, stable.
        var liveIds = containers.Select(c => c.Id).ToHashSet();
        foreach (var kv in _alert.ToArray())
            if (!kv.Key.StartsWith("disk:", StringComparison.Ordinal) && !liveIds.Contains(kv.Key))
                _alert.TryRemove(kv.Key, out _);

        _logger.LogDebug("Collected metrics for {ContainerCount} containers", metrics.Count);
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

    /// <summary>Server-level sustained disk-usage threshold (root filesystem) → high_disk event.</summary>
    private async Task EvaluateServerDiskAsync(string serverId, string serverName, long used, long total, MetricAlertSettings cfg, INotificationService notify)
    {
        var pct = used * 100.0 / total;
        var st = _alert.GetOrAdd($"disk:{serverId}", _ => new AlertState());
        var now = DateTime.UtcNow;
        var sustained = Math.Max(1, cfg.SustainedMinutes * 2); // 30s sampling interval

        st.DiskOver = pct >= cfg.DiskPercent ? st.DiskOver + 1 : 0;
        if (st.DiskOver >= sustained && now >= st.DiskCooldown)
        {
            st.DiskOver = 0;
            st.DiskCooldown = now.AddMinutes(cfg.CooldownMinutes);
            await notify.SendAsync(new NotificationEvent
            {
                EventType = "high_disk",
                ContainerName = serverName,
                ImageInfo = $"Festplatte {pct:F0}% voll auf {serverName} (Schwelle {cfg.DiskPercent:F0}%).",
            });
        }
    }

    /// <summary>Root-filesystem (used, total) bytes via df. Best-effort: returns (0,0) on any failure.</summary>
    private static async Task<(long Used, long Total)> GetDiskUsageAsync(Whiskers.Services.Server.IHostCommandExecutor executor, string serverId, CancellationToken ct)
    {
        try
        {
            var res = await executor.ExecuteAsync(serverId, "df -PB1 / | tail -n1", TimeSpan.FromSeconds(10), ct);
            if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.Output)) return (0, 0);
            // df -PB1 columns: Filesystem  1B-blocks(total)  Used  Available  Use%  Mounted-on
            var parts = res.Output.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && long.TryParse(parts[1], out var total) && long.TryParse(parts[2], out var used))
                return (used, total);
            return (0, 0);
        }
        catch { return (0, 0); }
    }

    private sealed class AlertState
    {
        public int CpuOver;
        public int MemOver;
        public int DiskOver;
        public DateTime CpuCooldown;
        public DateTime MemCooldown;
        public DateTime DiskCooldown;
        public DateTime AnomCooldown;
        public readonly Queue<double> CpuWin = new();
        public readonly Queue<double> MemWin = new();
    }

    /// <summary>The Docker servers this collector is responsible for — the ones a cycle record applies to.</summary>
    private IReadOnlyList<string> SelfMetricsServers()
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<Whiskers.Services.ServerConfig.IServerConfigService>()
            .GetEnabledServers()
            .Where(s => s.ConnectionType != Whiskers.Models.ConnectionType.Kubernetes)
            .Select(s => s.Id)
            .ToList();
    }

    /// <summary>Audit log + MCP tool-call history: their own, configurable retention (R1-23), once a day. Both go
    /// through the chain — the trail is verified first, the removal is an event of its own and the chain's new start is
    /// signed — never a plain delete. Returns when it last ran.</summary>
    internal static async Task<DateTime> PruneAuditIfDueAsync(IServiceProvider services, MetricsDbContext db, DateTime now,
        DateTime last, CancellationToken ct)
    {
        if (now - last <= TimeSpan.FromDays(1)) return last;
        var auditDays = services.GetService<IOptionsMonitor<AuditSettings>>()?.CurrentValue.EffectiveRetentionDays
                        ?? new AuditSettings().EffectiveRetentionDays;
        var auditCutoff = now.AddDays(-auditDays);
        if (services.GetService<Whiskers.Services.AuditLog.IAuditChain>() is { } chain)
            await chain.PruneAsync(auditCutoff, auditDays, ct);
        else
            await Whiskers.Services.AuditLog.AuditChain.Shared.PruneAsync(db, auditCutoff, auditDays, ct);
        return now;
    }
}
