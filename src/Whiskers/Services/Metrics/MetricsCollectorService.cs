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
    // R1-41: a reload with an out-of-range interval keeps the last valid one instead of throwing in the loop — the shared
    // EffectiveInterval, so the cadence and what the self-metrics report against it are one value.
    private readonly EffectiveInterval<MetricsSettings> _interval;
    private int IntervalSeconds() => _interval.Value;
    private readonly Whiskers.Services.Observability.SelfMetrics.ISelfMetrics _selfMetrics;
    private readonly ConcurrentDictionary<string, AlertState> _alert = new();

    // Host-level rules (Plan-0004). Held here rather than injected: the breach state is this loop's state,
    // exactly like _alert above, and it advances on sample time so it can also be driven by a replay.
    // Injected rather than owned: /metrics reads its open-findings list (Plan-0004 WP5.4), and a private
    // field would have made the "is the closing path working?" number the one number nobody can see.
    private readonly Whiskers.Services.Metrics.HostLoad.HostLoadEvaluator _hostLoad;
    private readonly Whiskers.Services.Metrics.HostLoad.ApiLatencyEvaluator _apiLatency;
    private readonly Whiskers.Services.Metrics.HostLoad.RollingBaseline _baseline;
    private readonly IMetricRetentionService? _retention;
    private readonly IMetricRollupService _rollup;
    // R1-56: the container and disk threshold alerts in the one duration model (BreachTracker), and the series that stopped
    // delivering readings. This loop's state, like _alert; the rules come from MetricAlertSettings with every reading.
    private readonly Whiskers.Services.Metrics.HostLoad.BreachTracker _thresholds = new(new Whiskers.Services.Metrics.HostLoad.HostLoadThresholds());
    private readonly Whiskers.Services.Metrics.HostLoad.SeriesGapTracker _gaps = new();
    // R1-56: the injected clock — sample times, cool-downs and the maintenance schedules all read it.
    private readonly TimeProvider _time;
    private DateTime Now() => _time.GetUtcNow().UtcDateTime;
    private DateTime _lastPrune;                 // OPT-2: prune at most hourly, not every 30s cycle
    private DateTime _nextRollup;                // R1-55: when the next 10-minute window closes (or now, while catching up)
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
        Whiskers.Services.Observability.SelfMetrics.ISelfMetrics selfMetrics,
        EffectiveInterval<MetricsSettings>? interval = null,
        IMetricRetentionService? retention = null,
        IMetricRollupService? rollup = null,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _retention = retention;
        _rollup = rollup ?? new MetricRollupService();
        _interval = interval ?? IntervalValidation.MetricsCollection(metricsSettings, logger);
        _services = services;
        _alertSettings = alertSettings;
        _hostLoad = hostLoad;
        _apiLatency = apiLatency;
        _baseline = baseline;
        _metricsSettings = metricsSettings;
        _logger = logger;
        _selfMetrics = selfMetrics;
    }

    /// <summary>Tests only: the pause before the first cycle.</summary>
    internal TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(10);

    private MetricsSettings? _lastSettings;
    private bool _warnedUnreadableSettings;

    /// <summary>R1-41 (review round 9): the section as last read successfully. A hand edit of app-settings.json that
    /// does not bind (<c>"30s"</c>) makes <c>CurrentValue</c> throw until the file is fixed; read outside the cycle's
    /// try, that ended the loop and stopped Whiskers. The last valid settings stay in use, with one warning.</summary>
    private MetricsSettings Settings()
    {
        try
        {
            var current = _metricsSettings.CurrentValue;
            _lastSettings = current;
            _warnedUnreadableSettings = false;
            return current;
        }
        catch (Exception ex)
        {
            // R1-55 (R1-41 leftover): also when the very first read fails there is something to go on — the defaults —
            // instead of an exception before the loop has started.
            if (!_warnedUnreadableSettings)
            {
                _warnedUnreadableSettings = true;
                _logger.LogWarning(_lastSettings is null
                        ? "Metrics settings cannot be read ({Error}); using the defaults until app-settings.json is fixed"
                        : "Metrics settings cannot be read ({Error}); keeping the last valid ones until app-settings.json is fixed",
                    ex.Message);
            }
            return _lastSettings ?? new MetricsSettings();
        }
    }

    /// <summary>R1-55: the tiers' retention as the cycle reads it — never throws (the service keeps the last valid one).</summary>
    private MetricRetention Retention()
    {
        try
        {
            return (_retention ?? _services.GetService<IMetricRetentionService>())?.Current ?? MetricRetention.Default;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metric retention cannot be read; the defaults apply to this run");
            return MetricRetention.Default;
        }
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        var startup = Settings();
        var startupRetention = Retention();
        _logger.LogInformation("Metrics collector started (interval: {Interval}s, retention raw/10m/1h/8h: {Raw}/{TenMinutes}/{Hour}/{EightHours}d, enabled: {Enabled})",
            startup.CollectionIntervalSeconds, startupRetention.RawDays, startupRetention.TenMinuteDays,
            startupRetention.HourDays, startupRetention.EightHourDays, startup.Enabled);
        await Task.Delay(StartupDelay, ct);

        while (!ct.IsCancellationRequested)
        {
            // Re-read every cycle so reload-on-change settings take effect without a restart.
            var cfg = Settings();
            if (cfg.Enabled)
            {
                var cycleStart = Now();
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
                // The server list is read outside the cycle's try: a failure there (servers.json unreadable) must not
                // end the loop either (review round 9).
                try
                {
                    foreach (var server in SelfMetricsServers())
                        _selfMetrics.RecordCycle(
                            Whiskers.Services.Observability.SelfMetrics.SelfMetricsFleetExtensions.Loops.Metrics,
                            server, Now() - cycleStart, cycleOk,
                            interval: _interval.Interval);
                }
                catch (Exception ex) when (!IsHostStopping(ex, ct))
                {
                    _logger.LogWarning(ex, "Could not record the metrics cycle");
                }

                // R1-55: roll-up and retention, after this cycle's samples are saved (so no later sample can fall into a
                // window it closes) and on the settings this cycle read — never re-read here, where an unreadable
                // app-settings.json used to throw every hour and stop every prune (R1-41 leftover). Its own try: a failed
                // roll-up or prune is logged and the next cycle runs; a failed collection does not hold up retention.
                try
                {
                    await MaintainAsync(Now(), Retention(), ct);
                }
                catch (Exception ex) when (!IsHostStopping(ex, ct))
                {
                    _logger.LogError(ex, "Metric roll-up or retention failed; the next cycle tries again");
                }
            }

            // Never a hot loop, never a delay past what Task.Delay takes (R1-41).
            var intervalSeconds = IntervalSeconds();
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
        }
    }

    /// <summary>One collection cycle. Internal for tests, which drive it with a fake clock.</summary>
    internal async Task CollectMetricsAsync(CancellationToken ct)
    {
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

        var now = Now();
        // R1-56: the tolerance follows the interval (a series sampled hourly cannot have a three-minute tolerance); the
        // durations of the alerts do not — they are elapsed time.
        var tolerance = Whiskers.Services.Metrics.HostLoad.BreachRules.GapToleranceFor(_interval.Interval);
        var rules = Whiskers.Services.Metrics.HostLoad.BreachRules.Threshold(
            TimeSpan.FromMinutes(Math.Max(1, alertCfg.SustainedMinutes)),
            TimeSpan.FromMinutes(Math.Max(0, alertCfg.CooldownMinutes)), tolerance);
        // R1-56 (review MAJOR-4): the breaches open before a restart, once, before the first reading judges them.
        await RestoreBreachesAsync(now);
        // R1-56 (review MINOR-6): a server whose checks are paused has no readings by design; loops_paused says so.
        var suspension = scope.ServiceProvider.GetService<Whiskers.Services.Observability.ILoopSuspensionService>();
        bool Paused(string serverId)
        {
            try { return suspension?.IsSuspended(serverId) == true; }
            catch (Exception ex) { _logger.LogDebug(ex, "Pause state of {ServerId} unknown", serverId); return false; }
        }

        // R1-56 (review MAJOR-3): a cycle that starts more than the tolerance after the last one was held up — every series
        // went without readings, none through a fault of its own. Never one message per series: each open alert it resets
        // is closed by the gap rule (BreachTracker, unconfirmed), which says why; the cycle itself is a log line, and the
        // scan supervisor reports a stalled loop (review round 3: no separate notice, no close without a raise).
        if (_lastCycleAt is { } previous && now - previous > tolerance)
            _logger.LogWarning("Metrics collection was held up: {Held} without a cycle (tolerance {Tolerance}); open alerts it " +
                "resets are closed as unconfirmed", now - previous, tolerance);
        _lastCycleAt = now;

        // Container listing still goes through Docker (an inventory call, not a metric); the metric
        // reads below go through IMetricsSource so Prometheus-configured servers bypass SSH.
        // R1-56 (review MAJOR-2): with which servers answered — a server whose listing failed this cycle is not a server
        // whose containers are gone, and its containers' state is kept.
        var listing = await docker.ListAllContainersDetailedAsync(false, ct);
        var containers = listing.Containers;
        foreach (var c in containers) _containerServer[c.Id] = (c.ServerId, c.ServerName, c.Name);

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
                        try { await EvaluateAlertsAsync(c, stats, alertCfg, rules, now, tolerance, notify, ct); }
                        catch (Exception aex) when (!IsHostStopping(aex, ct)) { _logger.LogDebug(aex, "Metric alert evaluation failed for {Container}", c.Name); }
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
                if (alertCfg.Enabled) await ContainerMissingAsync(c, now, tolerance, "the stats call returned no reading", notify, ct);
            }
            catch (Exception ex) when (!IsHostStopping(ex, ct))
            {
                _logger.LogWarning(ex, "Failed to collect stats for container {ContainerId} on {ServerId}", c.Id, c.ServerId);
                // R1-56: never only a log line — past the gap tolerance the series is reported as a metrics_gap.
                if (alertCfg.Enabled) await ContainerMissingAsync(c, now, tolerance, "the stats call failed", notify, ct);
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

        // Collect server metrics. R1-56: the servers this loop should have a reading of — a host without one (its
        // node_exporter down, the system-info call failing) is a gap of its own, not merely a warning in the log.
        var expected = servers.Where(s => s.ConnectionType != ConnectionType.Kubernetes && !Paused(s.Id)).ToList();
        var pausedServers = servers.Where(s => s.ConnectionType != ConnectionType.Kubernetes && Paused(s.Id))
            .Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var readHosts = new HashSet<string>(StringComparer.Ordinal);
        string hostFailure = "the server reported no system information";
        try
        {
            var serverInfos = await metricsSource.GetAllServerSystemInfoAsync();
            foreach (var (serverId, info) in serverInfos)
            {
                if (!info.IsReachable) continue;
                readHosts.Add(serverId);

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

                if (alertCfg.Enabled)
                {
                    try
                    {
                        if (diskTotal > 0)
                        {
                            _diskRead.Add(serverId);
                            await EvaluateServerDiskAsync(serverId, info.ServerName, diskUsed, diskTotal, alertCfg, rules, now, tolerance, notify, ct);
                        }
                        else if (_diskRead.Contains(serverId))
                            await NotifyFindingAsync(notify, ServerEvent(serverId, info.ServerName), _gaps.Missing(now, DiskSeries(serverId),
                                info.ServerName, $"the root filesystem of {info.ServerName}", tolerance, "df returned no reading",
                                OpenAlerts(_thresholds, DiskSeries(serverId))), ct);
                        // R1-56 (review MINOR-5): a df that never answered since the start (no command channel, a proxy
                        // that refuses the one-shot container) has no series that stopped — said once in the log, never a gap.
                        else if (_diskNeverRead.Add(serverId))
                            _logger.LogInformation("No disk reading for {Server}: df has not answered since the start, so no disk alert " +
                                "is evaluated for it (a gap is reported only for a df that stopped answering)", info.ServerName);
                    }
                    catch (Exception aex) when (!IsHostStopping(aex, ct)) { _logger.LogDebug(aex, "Disk alert evaluation failed for {ServerId}", serverId); }
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

                        var findings = new List<Whiskers.Services.Metrics.HostLoad.HostLoadFinding>();
                        // R1-56: readings are back — the end of a reported gap, or a gap the loop itself caused.
                        if (_gaps.Seen(now, HostSeries(serverId), info.ServerName, $"{info.ServerName}'s CPU and memory", tolerance) is { } back)
                            findings.Add(back);
                        findings.AddRange(_hostLoad.Evaluate(sample, tolerance));

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
                            await NotifyFindingAsync(notify, ServerEvent(serverId, info.ServerName), finding, ct);
                    }
                    catch (Exception aex) when (!IsHostStopping(aex, ct)) { _logger.LogDebug(aex, "Host load evaluation failed for {ServerId}", serverId); }
                }
            }
        }
        catch (Exception ex) when (!IsHostStopping(ex, ct))
        {
            _logger.LogWarning(ex, "Failed to collect server metrics");
            hostFailure = "reading the servers' system information failed";
        }

        // R1-56: every expected host without a reading this cycle. Its disk has no reading either; that is part of this
        // one gap (the disk series is marked as covered, so readings coming back do not report it a second time).
        if (alertCfg.Enabled)
        {
            foreach (var server in expected.Where(s => !readHosts.Contains(s.Id)))
            {
                try
                {
                    _gaps.Covered(now, DiskSeries(server.Id));
                    var open = OpenAlerts(_hostLoad.OpenFindings(), server.Id).Concat(OpenAlerts(_thresholds, DiskSeries(server.Id))).ToList();
                    await NotifyFindingAsync(notify, ServerEvent(server.Id, server.Name),
                        _gaps.Missing(now, HostSeries(server.Id), server.Name, $"{server.Name}'s CPU, memory and disk", tolerance,
                            hostFailure, open), ct);
                }
                catch (Exception aex) when (!IsHostStopping(aex, ct)) { _logger.LogDebug(aex, "Gap evaluation failed for {ServerId}", server.Id); }
            }

            // R1-56 (review MAJOR-2): a server whose container listing failed this cycle. Its containers were not asked for
            // readings, so their series go without — one gap for the server's containers, not one per container (each is
            // covered by it), and their breach state is kept, not dropped as if they were gone.
            var listingFailed = listing.FailedServers
                .Where(f => !pausedServers.Contains(f.ServerId) && expected.Any(s => s.Id == f.ServerId))
                .GroupBy(f => f.ServerId).Select(g => g.First()).ToList();
            foreach (var failure in listingFailed)
            {
                try
                {
                    var known = KnownContainersOf(failure.ServerId);
                    foreach (var id in known) _gaps.Covered(now, id);
                    var open = known.SelectMany(id => OpenAlerts(_thresholds, id).Select(k => $"{k} ({_containerServer[id].Name})")).ToList();
                    await NotifyFindingAsync(notify, ServerEvent(failure.ServerId, failure.ServerName),
                        _gaps.Missing(now, ListingSeries(failure.ServerId), failure.ServerName, $"the containers of {failure.ServerName}",
                            tolerance, "listing its containers failed", open), ct);
                }
                catch (Exception aex) when (!IsHostStopping(aex, ct)) { _logger.LogDebug(aex, "Gap evaluation failed for {ServerId}", failure.ServerId); }
            }
            // A listing that answered again ends that gap.
            foreach (var server in expected.Where(s => listing.RespondedServerIds.Contains(s.Id)))
                if (_gaps.Tracked().Contains(ListingSeries(server.Id)))
                    await NotifyFindingAsync(notify, ServerEvent(server.Id, server.Name),
                        _gaps.Seen(now, ListingSeries(server.Id), server.Name, $"the containers of {server.Name}", tolerance), ct);

            // R1-56 (review MINOR-6): a paused server's series wait for its resume without a gap of their own.
            foreach (var serverId in pausedServers)
            {
                _gaps.Covered(now, HostSeries(serverId));
                _gaps.Covered(now, DiskSeries(serverId));
                _gaps.Covered(now, ListingSeries(serverId));
                foreach (var id in KnownContainersOf(serverId)) _gaps.Covered(now, id);
            }
        }

        await db.SaveChangesAsync(ct);

        // Bound the in-memory alert-state map: drop per-container entries whose container is gone.
        // "disk:{server}" keys are kept — one per server, stable.
        // R1-56 (review MAJOR-2): gone means absent from a listing its server answered. A container of a server whose
        // listing failed (or that is paused) is kept with its open breach, so a transient failure neither forgets a
        // reported breach nor raises it a second time. Review round 3: a server that is configured but disabled is not
        // asked for its containers at all — they are kept too (re-enabled, their breaches meet the gap rule and are closed).
        var configured = scope.ServiceProvider.GetRequiredService<Whiskers.Services.ServerConfig.IServerConfigService>().GetServers()
            .Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var enabledIds = servers.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        bool Kept(string serverId) => !listing.MayPruneStateFor(serverId) || pausedServers.Contains(serverId)
            || (configured.Contains(serverId) && !enabledIds.Contains(serverId));
        var liveIds = containers.Select(c => c.Id).ToHashSet();
        bool Gone(string id) => !liveIds.Contains(id)
            && (!_containerServer.TryGetValue(id, out var where) || !Kept(where.ServerId));
        foreach (var kv in _alert.ToArray())
            if (!kv.Key.StartsWith("disk:", StringComparison.Ordinal) && Gone(kv.Key))
                _alert.TryRemove(kv.Key, out _);

        // Review round 3 (MINOR-3): what is dropped is closed — an open raise and an open gap of a container that is gone
        // (stopped, removed or recreated) and of a server that was removed are closed as unconfirmed, never left open.
        foreach (var id in _thresholds.Tracked().Concat(_gaps.Tracked()).Distinct().ToList())
            if (!id.StartsWith(DiskPrefix, StringComparison.Ordinal) && !id.StartsWith(HostPrefix, StringComparison.Ordinal)
                && !id.StartsWith(ListingPrefix, StringComparison.Ordinal) && Gone(id))
            {
                var owner = _containerServer.TryGetValue(id, out var w) ? w : (ServerId: "", ServerName: "", Name: id);
                var template = new NotificationEvent
                {
                    ContainerId = id, ContainerName = owner.Name, ServerId = owner.ServerId, ServerName = owner.ServerName,
                };
                // Follow-up (2026-11-03, review R1-56 NIT-5): a container of a server that was removed says so.
                var reason = !string.IsNullOrEmpty(owner.ServerId) && !configured.Contains(owner.ServerId)
                    ? $"the server of container {owner.Name} ({owner.ServerName}) was removed from the configuration"
                    : string.IsNullOrEmpty(owner.ServerName)
                    ? $"container {owner.Name} is no longer running"
                    : $"container {owner.Name} is no longer running on {owner.ServerName}";
                await CloseDroppedAsync(notify, _ => template, _thresholds.Close(id, now, reason), _gaps.Close(now, id, reason), ct);
            }
        static string? ServerOfSeries(string id) =>
            id.StartsWith(DiskPrefix, StringComparison.Ordinal) ? id[DiskPrefix.Length..]
            : id.StartsWith(HostPrefix, StringComparison.Ordinal) ? id[HostPrefix.Length..]
            : id.StartsWith(ListingPrefix, StringComparison.Ordinal) ? id[ListingPrefix.Length..]
            : null;
        const string removed = "its server was removed from the configuration";
        foreach (var id in _thresholds.Tracked().Concat(_gaps.Tracked()).Distinct().ToList())
            if (ServerOfSeries(id) is { } server && !configured.Contains(server))
                await CloseDroppedAsync(notify, f => ServerEvent(server, f.ServerName), _thresholds.Close(id, now, removed),
                    _gaps.Close(now, id, removed), ct);
        foreach (var id in _hostLoad.Tracked())
            if (!configured.Contains(id))
                await CloseDroppedAsync(notify, f => ServerEvent(id, f.ServerName), _hostLoad.Close(id, now, removed), null, ct);
        // Follow-up (2026-11-03, review R1-56 MINOR-2): the API-latency and baseline rules raise and close too — the open
        // alerts of a removed server are closed the same way, unconfirmed.
        foreach (var id in _apiLatency.Tracked())
            if (!configured.Contains(id))
                await CloseDroppedAsync(notify, f => ServerEvent(id, f.ServerName), _apiLatency.Close(id, now, removed), null, ct);
        foreach (var id in _baseline.Tracked())
            if (!configured.Contains(id))
                await CloseDroppedAsync(notify, f => ServerEvent(id, f.ServerName), _baseline.Close(id, now, removed), null, ct);
        foreach (var id in _containerServer.Keys.ToList())
            if (Gone(id) || !configured.Contains(_containerServer[id].ServerId)) _containerServer.TryRemove(id, out _);

        await SaveBreachesAsync();
        _logger.LogDebug("Collected metrics for {ContainerCount} containers", metrics.Count);
    }

    private const string ListingPrefix = "containers:";
    private static string ListingSeries(string serverId) => ListingPrefix + serverId;

    // R1-56 (review MAJOR-2): the server and name of every container seen, so its state outlives a failed listing.
    private readonly ConcurrentDictionary<string, (string ServerId, string ServerName, string Name)> _containerServer = new(StringComparer.Ordinal);
    private DateTime? _lastCycleAt;                                          // review MAJOR-3: when the last cycle ran
    private readonly HashSet<string> _diskRead = new(StringComparer.Ordinal);       // review MINOR-5: df answered once
    private readonly HashSet<string> _diskNeverRead = new(StringComparer.Ordinal);  // … or was said not to, once
    private bool _breachesRestored;
    private string? _savedBreaches;

    /// <summary>Review round 3: sends the closes of state that is dropped. A failed send is logged; the state is gone either
    /// way (it would otherwise be closed again every cycle).</summary>
    private async Task CloseDroppedAsync(INotificationService notify,
        Func<Whiskers.Services.Metrics.HostLoad.HostLoadFinding, NotificationEvent> template,
        IEnumerable<Whiskers.Services.Metrics.HostLoad.HostLoadFinding> closes,
        Whiskers.Services.Metrics.HostLoad.HostLoadFinding? gap, CancellationToken ct)
    {
        foreach (var finding in gap is null ? closes : closes.Append(gap))
        {
            try { await NotifyFindingAsync(notify, template(finding), finding, ct); }
            catch (Exception ex) when (!IsHostStopping(ex, ct))
            {
                _logger.LogWarning(ex, "The close of {EventType} on {Target} could not be sent", finding.EventType, finding.ServerName);
            }
        }
    }

    private List<string> KnownContainersOf(string serverId) =>
        _containerServer.Where(kv => kv.Value.ServerId == serverId).Select(kv => kv.Key).ToList();

    /// <summary>R1-56 (review MAJOR-4): takes over the breaches open when Whiskers last stopped. A file that cannot be read
    /// is logged and ignored — the alerts are then raised again if the breach goes on (delivered rather than lost).</summary>
    private async Task RestoreBreachesAsync(DateTime now)
    {
        if (_breachesRestored) return;
        _breachesRestored = true;
        if (_services.GetService<IAlertBreachStore>() is not { } store) return;
        try
        {
            var saved = await store.LoadAsync();
            _thresholds.Restore(saved.Thresholds, now);
            // Review round 2 (MINOR-2): the server of each restored container breach, so a listing that fails in the first
            // cycle keeps it like any other (not forgotten as a container of no known server).
            foreach (var owner in saved.Containers ?? [])
                if (!string.IsNullOrEmpty(owner.Id) && !string.IsNullOrEmpty(owner.ServerId))
                    _containerServer.TryAdd(owner.Id, (owner.ServerId, owner.ServerName ?? owner.ServerId, owner.Name ?? owner.Id));
            // Review round 2 (MINOR-3): a disk with a restored breach was read before — a df that stopped answering since
            // is a gap, not "never answered since the start".
            foreach (var s in saved.Thresholds)
                if (s.Series.StartsWith(DiskPrefix, StringComparison.Ordinal)) _diskRead.Add(s.Series[DiskPrefix.Length..]);
            _hostLoad.Restore(saved.HostLoad, now);
            // Follow-up (2026-11-03, review R1-56 MINOR-1): the reported gaps, so each is closed once and never raised twice.
            // A file written before has none.
            var gaps = (saved.Gaps ?? []).Where(g => g is not null).ToList();
            _gaps.Restore(gaps);
            _savedBreaches = Serialize(saved);
            var open = saved.Thresholds.Concat(saved.HostLoad).Count(s => s.Reported is not null);
            if (open > 0) _logger.LogInformation("Resumed {Open} threshold alerts that were open when Whiskers stopped", open);
            if (gaps.Count > 0) _logger.LogInformation("Resumed {Open} missing-readings alerts that were open when Whiskers stopped", gaps.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The open threshold alerts of the last run cannot be read; an alert still going on is raised again");
        }
    }

    /// <summary>R1-56 (review MAJOR-4): writes the open breaches when they changed. A failed write is logged; the next cycle
    /// tries again.</summary>
    private async Task SaveBreachesAsync()
    {
        if (_services.GetService<IAlertBreachStore>() is not { } store) return;
        var thresholds = _thresholds.Snapshot();
        var gaps = _gaps.Snapshot();
        var state = new AlertBreachFile
        {
            Thresholds = [.. thresholds],
            HostLoad = [.. _hostLoad.Snapshot()],
            Gaps = [.. gaps],
            // The server of each container with a kept breach or a kept gap (follow-up 2026-11-03).
            Containers = thresholds.Select(s => s.Series).Concat(gaps.Select(g => g.Series)).Distinct()
                .Where(id => _containerServer.ContainsKey(id))
                .Select(id => new ContainerOwner(id, _containerServer[id].ServerId, _containerServer[id].ServerName, _containerServer[id].Name))
                .OrderBy(o => o.Id, StringComparer.Ordinal).ToList(),
        };
        var json = Serialize(state);
        if (json == _savedBreaches) return;
        try
        {
            await store.SaveAsync(state);
            _savedBreaches = json;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The open threshold alerts could not be saved; a restart now would raise them again");
        }
    }

    private static string Serialize(AlertBreachFile state) => System.Text.Json.JsonSerializer.Serialize(state);

    private const string DiskPrefix = "disk:", HostPrefix = "host:";
    private static string DiskSeries(string serverId) => DiskPrefix + serverId;
    private static string HostSeries(string serverId) => HostPrefix + serverId;

    private static IEnumerable<string> OpenAlerts(IEnumerable<Whiskers.Services.Metrics.HostLoad.OpenFinding> open, string seriesId) =>
        open.Where(f => f.ServerId == seriesId).Select(f => f.Kind);

    private static IReadOnlyCollection<string> OpenAlerts(Whiskers.Services.Metrics.HostLoad.BreachTracker tracker, string seriesId) =>
        OpenAlerts(tracker.OpenFindings(), seriesId).ToList();

    private static NotificationEvent ServerEvent(string serverId, string serverName) => new()
    {
        ServerId = serverId,
        ServerName = serverName,
        ContainerName = serverName,
    };

    /// <summary>R1-56: one finding, as an event built on <paramref name="template"/> (its target fields). Nothing to send for
    /// null — the normal case.</summary>
    private async Task NotifyFindingAsync(INotificationService notify, NotificationEvent template,
        Whiskers.Services.Metrics.HostLoad.HostLoadFinding? finding, CancellationToken ct)
    {
        if (finding is null) return;
        _logger.Log(
            finding.What == Whiskers.Services.Metrics.HostLoad.FindingKind.Cleared ? LogLevel.Information : LogLevel.Warning,
            "{EventType} on {Target}: {Summary}", finding.EventType, finding.ServerName, finding.Summary);
        var evt = template.Copy();
        evt.EventType = finding.EventType;
        evt.RecoveryUnconfirmed = finding.Unconfirmed;         // review round 3: titled and graded as such
        evt.ImageInfo = finding.Summary;
        evt.Timestamp = finding.AtUtc;
        await notify.SendAsync(evt, ct);
    }

    private Task ContainerMissingAsync(ContainerInfo c, DateTime now, TimeSpan tolerance, string reason, INotificationService notify,
        CancellationToken ct) =>
        NotifyFindingAsync(notify, ContainerEvent(c),
            _gaps.Missing(now, c.Id, c.Name, $"container {c.Name}", tolerance, reason, OpenAlerts(_thresholds, c.Id)), ct);

    private static NotificationEvent ContainerEvent(ContainerInfo c) => new()
    {
        ContainerId = c.Id,
        ContainerName = c.Name,
        ServerId = c.ServerId,
        ServerName = c.ServerName,
        Image = c.Image,
        ImageName = c.Image,
    };

    /// <summary>R1-55: rolls up the windows that closed (each 10 minutes, every cycle while an upgrade catches up), applies
    /// the tiers' retention at most hourly, and the audit retention daily (R1-23). A failed roll-up is retried next cycle
    /// and never holds up retention, which deletes nothing a coarser tier has not taken over anyway.</summary>
    internal async Task MaintainAsync(DateTime now, MetricRetention retention, CancellationToken ct)
    {
        // R1-55 (review rounds 2–3): all three schedules were set under the clock of the last run. A clock set back since
        // then would hold them off for as long as it was ahead — the roll-up (and its clock warning), the hourly
        // retention and the daily audit retention (PruneAuditIfDueAsync) would wait; a schedule ahead of the clock
        // counts as due instead.
        static bool RollupDue(DateTime now, DateTime next) =>
            now >= next || now < next - MetricTiers.Size(MetricTiers.Minutes[0]);
        static bool PruneDue(DateTime now, DateTime last) => now - last > TimeSpan.FromHours(1) || now < last;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        if (RollupDue(now, _nextRollup))
        {
            try
            {
                var result = await _rollup.RollUpAsync(db, now, ct);
                var tenMinutes = MetricTiers.Size(MetricTiers.Minutes[0]);
                _nextRollup = result.MoreDue ? now : MetricTiers.Floor(now, tenMinutes) + tenMinutes;
                if (result.Windows > 0) _logger.LogDebug("Rolled up {Windows} metric windows", result.Windows);
            }
            catch (Exception ex) when (!IsHostStopping(ex, ct))
            {
                _logger.LogError(ex, "Metric roll-up failed; nothing it had not committed is deleted, the next cycle tries again");
            }
        }

        // Pruned at most once an hour — the retention windows are days, so pruning on every 30s cycle just burns
        // ExecuteDelete round-trips for nothing.
        if (PruneDue(now, _lastPrune))
        {
            _lastPrune = now;
            try
            {
                await _rollup.PruneAsync(db, now, retention, ct);
            }
            catch (Exception ex) when (!IsHostStopping(ex, ct))
            {
                _logger.LogError(ex, "Metric retention failed; the next run is in an hour");
            }
        }

        _lastAuditPrune = await PruneAuditIfDueAsync(_services, db, now, _lastAuditPrune, ct, _logger);
    }

    /// <summary>Per-container threshold (sustained high CPU/RAM) + simple anomaly (rolling z-score).
    /// Emits NotificationEvents that flow through the pipeline and can drive AI triggers.
    ///
    /// <para>R1-56: <c>high_cpu</c> and <c>high_memory</c> are breaches in the one duration model (<see cref="Whiskers.Services.Metrics.HostLoad.BreachTracker"/>):
    /// raised once when the readings stayed at or above the threshold for <c>SustainedMinutes</c> of elapsed time, closed
    /// with <c>&lt;kind&gt;_recovered</c> after five minutes below the threshold minus five points; <c>CooldownMinutes</c> is
    /// the least gap between two breaches of one series. They no longer re-fire while a breach lasts.
    /// <c>metric_anomaly</c> is a point-in-time outlier, not a breach: it keeps re-firing after <c>CooldownMinutes</c>.</para></summary>
    private async Task EvaluateAlertsAsync(ContainerInfo c, ContainerStats stats, MetricAlertSettings cfg,
        Whiskers.Services.Metrics.HostLoad.BreachRules rules, DateTime now, TimeSpan tolerance, INotificationService notify,
        CancellationToken ct)
    {
        var st = _alert.GetOrAdd(c.Id, _ => new AlertState());
        var cpu = stats.CpuPercent;
        var mem = stats.MemoryLimitBytes > 0 ? stats.MemoryUsageBytes * 100.0 / stats.MemoryLimitBytes : 0;
        var template = ContainerEvent(c);

        // --- Readings back after a gap (R1-56) ---
        await NotifyFindingAsync(notify, template, _gaps.Seen(now, c.Id, c.Name, $"container {c.Name}", tolerance), ct);

        // --- Sustained threshold, one raise per breach (R1-56) ---
        await NotifyFindingAsync(notify, template, _thresholds.Consider(now, c.Id, c.Name, NotificationEventTypes.HighCpu,
            cpu, cfg.CpuPercent,
            (v, open) => $"{c.Name}: CPU at {v:F0}% for {Whiskers.Services.Metrics.HostLoad.BreachTracker.Describe(open)} (threshold {cfg.CpuPercent:F0}%).",
            v => $"{c.Name}: CPU back to {v:F0}%.", rules), ct);

        await NotifyFindingAsync(notify, template, _thresholds.Consider(now, c.Id, c.Name, NotificationEventTypes.HighMemory,
            mem, cfg.MemoryPercent,
            (v, open) => $"{c.Name}: memory at {v:F0}% of its limit for {Whiskers.Services.Metrics.HostLoad.BreachTracker.Describe(open)} (threshold {cfg.MemoryPercent:F0}%).",
            v => $"{c.Name}: memory back to {v:F0}% of its limit.", rules), ct);

        // --- Simple anomaly (rolling z-score over previous window) ---
        if (cfg.AnomalyEnabled)
        {
            if (Anomalous(st.CpuWin, cpu, cfg) && now >= st.AnomCooldown)
            {
                st.AnomCooldown = now.AddMinutes(cfg.CooldownMinutes);
                await Emit(notify, c, NotificationEventTypes.MetricAnomaly, $"CPU outlier: {cpu:F0}% (the mean of the last {cfg.AnomalyWindow} samples is clearly lower).", now, ct);
            }
            else if (Anomalous(st.MemWin, mem, cfg) && now >= st.AnomCooldown)
            {
                st.AnomCooldown = now.AddMinutes(cfg.CooldownMinutes);
                await Emit(notify, c, NotificationEventTypes.MetricAnomaly, $"Memory outlier: {mem:F0}% (the mean of the last {cfg.AnomalyWindow} samples is clearly lower).", now, ct);
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

    private static Task Emit(INotificationService notify, ContainerInfo c, string type, string info, DateTime now, CancellationToken ct)
    {
        var evt = ContainerEvent(c);
        evt.EventType = type;
        evt.ImageInfo = info;
        evt.Timestamp = now;
        return notify.SendAsync(evt, ct);
    }

    /// <summary>Server-level sustained disk-usage threshold (root filesystem) → <c>high_disk</c>, one raise per breach and a
    /// <c>high_disk_recovered</c> (R1-56).</summary>
    private async Task EvaluateServerDiskAsync(string serverId, string serverName, long used, long total, MetricAlertSettings cfg,
        Whiskers.Services.Metrics.HostLoad.BreachRules rules, DateTime now, TimeSpan tolerance, INotificationService notify,
        CancellationToken ct)
    {
        var pct = used * 100.0 / total;
        var template = ServerEvent(serverId, serverName);
        await NotifyFindingAsync(notify, template,
            _gaps.Seen(now, DiskSeries(serverId), serverName, $"the root filesystem of {serverName}", tolerance), ct);
        await NotifyFindingAsync(notify, template, _thresholds.Consider(now, DiskSeries(serverId), serverName,
            NotificationEventTypes.HighDisk, pct, cfg.DiskPercent,
            (v, open) => $"Disk {v:F0}% full on {serverName} for {Whiskers.Services.Metrics.HostLoad.BreachTracker.Describe(open)} (threshold {cfg.DiskPercent:F0}%).",
            v => $"Disk on {serverName} back to {v:F0}% full.", rules), ct);
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

    /// <summary>The anomaly rule's window and cool-down per container; the threshold alerts keep theirs in the breach
    /// tracker (R1-56).</summary>
    private sealed class AlertState
    {
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
    /// signed — never a plain delete. Returns when it last ran: also after a failure, which is logged and waits for
    /// the next day (a full-trail verification every cycle would be load of its own, and must not stop the metrics).</summary>
    internal static async Task<DateTime> PruneAuditIfDueAsync(IServiceProvider services, MetricsDbContext db, DateTime now,
        DateTime last, CancellationToken ct, ILogger? logger = null)
    {
        // A last run ahead of the clock (the clock was set back) counts as due, like the metric schedules (R1-55).
        if (now - last <= TimeSpan.FromDays(1) && now >= last) return last;
        var auditDays = services.GetService<IOptionsMonitor<AuditSettings>>()?.CurrentValue.EffectiveRetentionDays
                        ?? new AuditSettings().EffectiveRetentionDays;
        var auditCutoff = now.AddDays(-auditDays);
        try
        {
            if (services.GetService<Whiskers.Services.AuditLog.IAuditChain>() is { } chain)
                await chain.PruneAsync(auditCutoff, auditDays, ct);
            else
                await Whiskers.Services.AuditLog.AuditChain.Shared.PruneAsync(db, auditCutoff, auditDays, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger?.LogError(ex, "AUDIT RETENTION FAILED — nothing removed; the next run is in a day");
        }
        return now;
    }
}
