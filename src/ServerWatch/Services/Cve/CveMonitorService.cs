using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Hubs;
using ServerWatch.Models;
using ServerWatch.Models.Cve;
using ServerWatch.Services.Docker;
using ServerWatch.Services.Notifications;
using ServerWatch.Services.ServerConfig;

namespace ServerWatch.Services.Cve;

/// <summary>
/// Background service that periodically scans each registered server for CVEs in
/// both the host OS and each running container's image. Mirrors the style of
/// <c>ImageUpdateChecker</c>. New findings (vs. the previous scan for the same
/// target) above the configured severity threshold are sent as a single aggregated
/// Mattermost notification per target.
/// </summary>
public class CveMonitorService : BackgroundService, ICveMonitorService
{
    private readonly IServiceProvider _services;
    private readonly ICveFindingsStore _store;
    private readonly IOptionsMonitor<CveMonitorSettings> _settings;
    private readonly ILogger<CveMonitorService> _logger;

    public CveMonitorService(
        IServiceProvider services,
        ICveFindingsStore store,
        IOptionsMonitor<CveMonitorSettings> settings,
        ILogger<CveMonitorService> logger)
    {
        _services = services;
        _store = store;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("CVE monitor started (enabled={Enabled}, interval={Hours}h)",
            _settings.CurrentValue.Enabled, _settings.CurrentValue.CheckIntervalHours);

        // Initial delay so the app finishes warming up before we hit Docker on every server.
        await Task.Delay(TimeSpan.FromSeconds(45), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_settings.CurrentValue.Enabled)
                {
                    // Scan ONLY when actually due — never on every restart. Persisted results from a
                    // previous run are kept until the interval elapses (or a manual scan is triggered).
                    var interval = TimeSpan.FromHours(Math.Max(1, _settings.CurrentValue.CheckIntervalHours));
                    if (_store.LastScanAt is { } last && DateTime.UtcNow - last < interval)
                        _logger.LogInformation("CVE scan not due yet (last {Last:u}, every {H}h) — using persisted results",
                            last, _settings.CurrentValue.CheckIntervalHours);
                    else
                        await RunOneCycleAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CVE monitor cycle failed");
            }

            // Sleep until the next scan is due (based on the last scan time), not a flat interval from start.
            var iv = TimeSpan.FromHours(Math.Max(1, _settings.CurrentValue.CheckIntervalHours));
            var wait = _store.LastScanAt is { } l ? iv - (DateTime.UtcNow - l) : iv;
            if (wait < TimeSpan.FromMinutes(1)) wait = iv;
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Atomic scan gate (0 = idle, 1 = scanning). A manual trigger and the background loop must never run
    // overlapping full scans; the bool _store.IsScanning is kept only as the UI indicator.
    private int _scanning;

    /// <summary>Run a single scan cycle across all enabled servers. Public for manual triggers.</summary>
    public async Task RunOneCycleAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
        {
            _logger.LogInformation("CVE scan cycle already in progress — skipping");
            return;
        }
        _store.IsScanning = true;
        var settings = _settings.CurrentValue;
        try
        {
            using var scope = _services.CreateScope();
            var sp = scope.ServiceProvider;
            var serverConfig = sp.GetRequiredService<IServerConfigService>();
            var docker = sp.GetRequiredService<IDockerService>();
            var osScanner = sp.GetRequiredService<IOsCveScanner>();
            var trivyScanner = sp.GetRequiredService<ITrivyScanner>();
            var notification = sp.GetRequiredService<INotificationService>();
            var ageStore = sp.GetRequiredService<ICveAgeStore>();
            var hub = sp.GetRequiredService<IHubContext<ContainerHub>>();
            var serverNames = serverConfig.GetServers().ToDictionary(s => s.Id, s => s.Name);

            // Real host OS per server (so OS findings carry the actual OS they apply to).
            var osByServer = new Dictionary<string, string>();
            try
            {
                var metricsSource = sp.GetRequiredService<ServerWatch.Services.Metrics.IMetricsSource>();
                foreach (var (sid, info) in await metricsSource.GetAllServerSystemInfoAsync())
                {
                    var os = string.Join(' ', new[] { info.OperatingSystem, info.OsVersion }
                        .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                    if (!string.IsNullOrWhiteSpace(os)) osByServer[sid] = os;
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not resolve server OS info for CVE context"); }

            var servers = serverConfig.GetEnabledServers();
            var threshold = ParseSeverity(settings.NotifySeverity);

            // Aggregate "new since last scan" per scan target, used for notifications.
            var newPerTarget = new ConcurrentBag<(CveScanResult Target, List<CveFinding> News)>();

            foreach (var server in servers)
            {
                if (ct.IsCancellationRequested) break;

                if (settings.ScanOs)
                {
                    try
                    {
                        var prev = _store.Get(server.Id, null);
                        var osResult = await osScanner.ScanAsync(server.Id, ct);
                        if (osByServer.TryGetValue(server.Id, out var hostOs))
                            foreach (var f in osResult.Findings)
                                f.OsContext ??= hostOs;
                        // Only overwrite stored results when the scan actually succeeded. A failed scan
                        // returns an empty Findings list with Error set; storing it would wipe the good
                        // previous results (target shows "clean") and make the next successful scan re-report
                        // every existing CVE as new.
                        if (osResult.Error is null)
                        {
                            var news = DiffFindings(prev, osResult);
                            _store.Set(osResult);
                            if (news.Count > 0) newPerTarget.Add((osResult, news));
                        }
                        else
                        {
                            _logger.LogWarning("OS CVE scan on {Server} failed ({Error}) — keeping previous results",
                                server.Id, osResult.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "OS CVE scan failed on {Server}", server.Id);
                    }
                }

                if (settings.ScanContainers)
                {
                    List<ContainerInfo> containers;
                    try
                    {
                        containers = (await docker.ListAllContainersAsync(all: false))
                            .Where(c => c.ServerId == server.Id)
                            .ToList();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to list containers on {Server}", server.Id);
                        continue;
                    }

                    var semaphore = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrentScans));
                    var tasks = containers.Select(async c =>
                    {
                        await semaphore.WaitAsync(ct);
                        try
                        {
                            var prev = _store.Get(server.Id, c.Id);
                            var result = await trivyScanner.ScanContainerImageAsync(
                                server.Id, c.Id, c.Name, c.Image, ct);
                            // Keep previous results on scan failure (see OS branch above) to avoid a false
                            // "clean" state and a re-notification storm on the next successful scan.
                            if (result.Error is null)
                            {
                                var news = DiffFindings(prev, result);
                                _store.Set(result);
                                if (news.Count > 0) newPerTarget.Add((result, news));
                            }
                            else
                            {
                                _logger.LogWarning("Container CVE scan for {Container} on {Server} failed ({Error}) — keeping previous results",
                                    c.Name, server.Id, result.Error);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Container CVE scan failed for {Container} on {Server}",
                                c.Name, server.Id);
                        }
                        finally { semaphore.Release(); }
                    });
                    await Task.WhenAll(tasks);

                    // Drop phantom entries for containers that no longer exist (recreated/deleted). The live
                    // set is authoritative because the listing above succeeded; the OS key is preserved.
                    var liveKeys = containers.Select(c => CveFindingsStore.Key(server.Id, c.Id)).ToHashSet();
                    _store.PruneServer(server.Id, liveKeys);
                }
            }

            _store.LastScanAt = DateTime.UtcNow;

            // Persist first-seen for every current finding so the "open for N days" age survives restarts.
            try
            {
                var allIdentities = _store.GetAll()
                    .SelectMany(r => r.Findings)
                    .Select(f => (f.IdentityKey, f.CveId));
                await ageStore.RecordSeenAsync(allIdentities, ct);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Recording CVE first-seen failed"); }

            // Persist results + last-scan time so a restart doesn't trigger a re-scan or re-notify.
            await _store.SaveAsync();

            // Aggregated notifications — one per target with new findings >= threshold.
            if (settings.NotifyOnFinding)
            {
                foreach (var (target, news) in newPerTarget)
                {
                    var relevant = news.Where(f => f.Severity >= threshold).ToList();
                    if (relevant.Count == 0) continue;
                    await TrySendAggregateNotificationAsync(notification, target, relevant, serverNames);
                }
            }

            // SignalR broadcast for the UI (Phase 2 will hook this).
            try { await hub.Clients.All.SendAsync("CveFindingsChanged", ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "SignalR broadcast failed"); }

            _logger.LogInformation(
                "CVE scan cycle done: {Servers} server(s), {Targets} target(s) with new findings",
                servers.Count, newPerTarget.Count);
        }
        finally
        {
            _store.IsScanning = false;
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private async Task TrySendAggregateNotificationAsync(
        INotificationService notification,
        CveScanResult target,
        List<CveFinding> relevant,
        IDictionary<string, string> serverNames)
    {
        var serverName = serverNames.TryGetValue(target.ServerId, out var n) ? n : target.ServerId;
        var label = target.Source == CveSource.Os
            ? $"OS on {serverName}"
            : $"container `{target.ContainerName}` on {serverName}";
        var top = string.Join(", ",
            relevant.OrderByDescending(f => f.Severity).Take(8)
                .Select(f => $"{f.CveId} ({f.Severity})"));
        if (relevant.Count > 8) top += $" +{relevant.Count - 8} more";

        try
        {
            await notification.SendAsync(new NotificationEvent
            {
                // ContainerId is used as part of the Mattermost throttler key — using
                // server:containerId|os gives reasonable per-target granularity.
                ContainerId = $"{target.ServerId}:{target.ContainerId ?? "os"}",
                ContainerName = label,
                Image = target.Image ?? "",
                EventType = "cve_finding",
                ImageName = $"{relevant.Count} new {(relevant.Count == 1 ? "finding" : "findings")}",
                ImageInfo = top
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send CVE notification for {Label}", label);
        }
    }

    private static List<CveFinding> DiffFindings(CveScanResult? prev, CveScanResult curr)
    {
        if (prev == null) return curr.Findings.ToList();
        var prevKeys = new HashSet<string>(prev.Findings.Select(f => f.IdentityKey));
        return curr.Findings.Where(f => !prevKeys.Contains(f.IdentityKey)).ToList();
    }

    private static CveSeverity ParseSeverity(string? s) => s?.ToLowerInvariant() switch
    {
        "critical" => CveSeverity.Critical,
        "high" => CveSeverity.High,
        "medium" => CveSeverity.Medium,
        "low" => CveSeverity.Low,
        _ => CveSeverity.High
    };
}
