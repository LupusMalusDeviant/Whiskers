using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Hubs;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.Docker;
using Whiskers.Services.Notifications;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Cve;

/// <summary>
/// Background service that periodically scans each registered server for CVEs in
/// both the host OS and each running container's image. Mirrors the style of
/// <c>ImageUpdateChecker</c>. New findings (vs. the previous scan for the same
/// target) above the configured severity threshold are sent as a single aggregated
/// Mattermost notification per target.
/// </summary>
public class CveMonitorService : Whiskers.Services.FleetBackgroundService, ICveMonitorService
{
    private readonly ICveFindingsStore _store;
    private readonly IOptionsMonitor<CveMonitorSettings> _settings;
    private readonly ILogger<CveMonitorService> _logger;
    private readonly Whiskers.Services.Observability.SelfMetrics.ISelfMetrics _selfMetrics;

    // C8: the scan-cycle collaborators are injected directly instead of being pulled from a per-cycle
    // IServiceProvider scope (service-locator antipattern). All of them are singletons — verified: none is
    // registered AddScoped — so this BackgroundService can hold them, and ValidateScopes proves it at boot.
    // The CveAgeStore opens its own DbContext scope internally, so no outer scope is needed here.
    private readonly IServerConfigService _serverConfig;
    private readonly IDockerService _docker;
    private readonly IOsCveScanner _osScanner;
    private readonly ITrivyScanner _trivyScanner;
    private readonly INotificationService _notification;
    private readonly ICveAgeStore _ageStore;
    private readonly IHubContext<ContainerHub> _hub;
    private readonly Whiskers.Services.Metrics.IMetricsSource _metricsSource;

    public CveMonitorService(
        ICveFindingsStore store,
        IOptionsMonitor<CveMonitorSettings> settings,
        ILogger<CveMonitorService> logger,
        Whiskers.Services.Observability.SelfMetrics.ISelfMetrics selfMetrics,
        IServerConfigService serverConfig,
        IDockerService docker,
        IOsCveScanner osScanner,
        ITrivyScanner trivyScanner,
        INotificationService notification,
        ICveAgeStore ageStore,
        IHubContext<ContainerHub> hub,
        Whiskers.Services.Metrics.IMetricsSource metricsSource)
    {
        _store = store;
        _settings = settings;
        _logger = logger;
        _selfMetrics = selfMetrics;
        _serverConfig = serverConfig;
        _docker = docker;
        _osScanner = osScanner;
        _trivyScanner = trivyScanner;
        _notification = notification;
        _ageStore = ageStore;
        _hub = hub;
        _metricsSource = metricsSource;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("CVE monitor started (enabled={Enabled}, interval={Hours}h)",
            _settings.CurrentValue.Enabled, _settings.CurrentValue.CheckIntervalHours);

        // Initial delay so the app finishes warming up before we hit Docker on every server.
        await Task.Delay(TimeSpan.FromSeconds(45), ct);

        while (!ct.IsCancellationRequested)
        {
            var cycleFailed = false;
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CVE monitor cycle failed");
                cycleFailed = true;
            }

            var iv = TimeSpan.FromHours(Math.Max(1, _settings.CurrentValue.CheckIntervalHours));
            TimeSpan wait;
            if (cycleFailed)
                // After a failure, retry soon instead of waiting the full interval.
                wait = TimeSpan.FromMinutes(15);
            else
            {
                // Sleep until the next scan is due (based on the last scan time), not a flat interval from start.
                wait = _store.LastScanAt is { } l ? iv - (DateTime.UtcNow - l) : iv;
                if (wait < TimeSpan.FromMinutes(1)) wait = iv;
            }
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
            var serverNames = _serverConfig.GetServers().ToDictionary(s => s.Id, s => s.Name);

            // Real host OS per server (so OS findings carry the actual OS they apply to).
            var osByServer = new Dictionary<string, string>();
            try
            {
                foreach (var (sid, info) in await _metricsSource.GetAllServerSystemInfoAsync())
                {
                    var os = string.Join(' ', new[] { info.OperatingSystem, info.OsVersion }
                        .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                    if (!string.IsNullOrWhiteSpace(os)) osByServer[sid] = os;
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not resolve server OS info for CVE context"); }

            // Kubernetes clusters have no host shell / Docker API for the scanners — K8s image
            // scanning is a later Track B step (kubernetesImplement §B.3, explicitly not v1).
            var allServers = _serverConfig.GetEnabledServers();
            var servers = allServers
                .Where(s => s.ConnectionType != Whiskers.Models.ConnectionType.Kubernetes).ToList();

            // Record the ones stepped over, so a Kubernetes host does not simply vanish from the metrics —
            // absent, "no CVE scanning happens here" reads exactly like "no findings" (Plan-0003 WP2).
            Whiskers.Services.Observability.SelfMetrics.SelfMetricsFleetExtensions.RecordKubernetesSkips(
                _selfMetrics, Whiskers.Services.Observability.SelfMetrics.SelfMetricsFleetExtensions.Loops.Cve, allServers);
            var scanStartedAt = DateTime.UtcNow;
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
                        var osResult = await _osScanner.ScanAsync(server.Id, ct);
                        if (osByServer.TryGetValue(server.Id, out var hostOs))
                            foreach (var f in osResult.Findings)
                                f.OsContext ??= hostOs;
                        // Only overwrite stored results when the scan actually succeeded. A failed scan
                        // returns an empty Findings list with Error set; storing it would wipe the good
                        // previous results (target shows "clean") and make the next successful scan re-report
                        // every existing CVE as new.
                        if (CveScanOutcome.Decide(prev, osResult) == ScanResultAction.Store
                            && CveScanOutcome.ShouldNotify(osResult))
                        {
                            var news = DiffFindings(prev, osResult);
                            _store.Set(osResult);
                            if (news.Count > 0) newPerTarget.Add((osResult, news));
                        }
                        else if (CveScanOutcome.Decide(prev, osResult) == ScanResultAction.Store)
                        {
                            // Same reasoning as the container branch below: never scanned and not stored is
                            // indistinguishable from clean.
                            _store.Set(osResult);
                            _logger.LogWarning(
                                "OS CVE scan on {Server} failed ({Error}) and there is no earlier result — " +
                                "recording the failure so the target is not invisible", server.Id, osResult.Error);
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
                        containers = (await _docker.ListAllContainersAsync(all: false))
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
                            var result = await _trivyScanner.ScanContainerImageAsync(
                                server.Id, c.Id, c.Name, c.Image, ct);
                            // Keep previous results on scan failure (see OS branch above) to avoid a false
                            // "clean" state and a re-notification storm on the next successful scan.
                            if (CveScanOutcome.Decide(prev, result) == ScanResultAction.Store
                                && CveScanOutcome.ShouldNotify(result))
                            {
                                var news = DiffFindings(prev, result);
                                _store.Set(result);
                                if (news.Count > 0) newPerTarget.Add((result, news));
                            }
                            else if (CveScanOutcome.Decide(prev, result) == ScanResultAction.Store)
                            {
                                // A target that has NEVER been scanned successfully has no previous result to
                                // keep, so the old branch stored nothing at all — and a target that is absent
                                // reads, in every view, exactly like one with no findings. Found on
                                // 2026-08-28: two running containers on infomaniak had been invisible since
                                // their image layers went bad, and the only trace was a warning that the log
                                // rotated away within the hour. Store the failure instead, so the target
                                // exists and says why it is empty.
                                _store.Set(result);
                                _logger.LogWarning(
                                    "Container CVE scan for {Container} on {Server} failed ({Error}) and there " +
                                    "is no earlier result — recording the failure so the target is not invisible",
                                    c.Name, server.Id, result.Error);
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

            // Drop results for servers that are no longer configured at all. PruneServer above only runs for
            // servers that still exist, so a REMOVED server's findings survived every cycle — reported as
            // current vulnerabilities of a machine that is gone, and keeping their first-seen ages alive with
            // them, because a key that is still "live" is never stale. GetServers() rather than
            // GetEnabledServers(): switching a server off is not removing it, and a fortnight of maintenance
            // must not cost its findings.
            _store.PruneRemovedServers(_serverConfig.GetServers().Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));

            // Persist first-seen for every current finding so the "open for N days" age survives restarts,
            // then drop first-seen rows for vulnerabilities that are gone AND older than the retention window.
            try
            {
                var current = _store.GetAll()
                    .SelectMany(r => r.Findings)
                    .Select(f => (f.IdentityKey, f.CveId))
                    .ToList();
                await _ageStore.RecordSeenAsync(current, ct);
                var liveKeys = current.Select(x => x.IdentityKey).ToHashSet();
                await _ageStore.PruneStaleAsync(liveKeys, DateTime.UtcNow.AddDays(-30), ct);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Recording/pruning CVE first-seen failed"); }

            // Persist results + last-scan time so a restart doesn't trigger a re-scan or re-notify.
            await _store.SaveAsync();

            // Aggregated notifications — one per target with new findings >= threshold.
            if (settings.NotifyOnFinding)
            {
                foreach (var (target, news) in newPerTarget)
                {
                    var relevant = news.Where(f => f.Severity >= threshold).ToList();
                    if (relevant.Count == 0) continue;
                    await TrySendAggregateNotificationAsync(_notification, target, relevant, serverNames);
                }
            }

            // SignalR broadcast for the UI (Phase 2 will hook this).
            try { await _hub.Clients.All.SendAsync("CveFindingsChanged", ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "SignalR broadcast failed"); }

            _logger.LogInformation(
                "CVE scan cycle done: {Servers} server(s), {Targets} target(s) with new findings",
                servers.Count, newPerTarget.Count);

            // The scan runs hours apart, so its "last success" age is the only practical way to notice that
            // it has stopped — a failure counter says nothing about a loop that no longer runs at all.
            foreach (var server in servers)
                _selfMetrics.RecordCycle(
                    Whiskers.Services.Observability.SelfMetrics.SelfMetricsFleetExtensions.Loops.Cve,
                    server.Id, DateTime.UtcNow - scanStartedAt, success: true,
                    interval: TimeSpan.FromHours(Math.Max(1, settings.CheckIntervalHours)));
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
