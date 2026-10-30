using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.LogMonitor;

/// <summary>
/// Background service that periodically checks container logs against alert rules.
/// Scans EVERY configured Docker server, not just the default one — a rule without a container filter
/// means "all containers of the fleet", the same scope <see cref="HealthMonitor.ContainerHealthMonitor"/>
/// has always used.
/// </summary>
public class LogMonitorService : Whiskers.Services.FleetBackgroundService, ILogMonitorService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDockerService _docker;
    private readonly IServerConfigService _serverConfig;
    private readonly INotificationService _notifications;
    private readonly ILogger<LogMonitorService> _logger;
    private readonly Docker.Budget.IServerBudget _budget;
    private readonly Observability.SelfMetrics.ISelfMetrics _selfMetrics;
    private readonly Hygiene.ILogScanExclusions _exclusions;
    private readonly Observability.Outcomes.IActionOutcomeService _outcomes;
    // R1-22: one rule writer at a time (create, toggle, delete), so each recorded change starts where the one before
    // ended and never describes a change another request already made.
    private readonly SemaphoreSlim _rules = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    // Per-container timestamp of the last log check, so we fetch only NEW lines and an old ERROR line
    // doesn't re-alert every cycle. Keyed by "{serverId}:{containerId}" (see CompositeKey): container ids
    // are only unique per host, so a fleet-wide scan must not let two hosts share one entry.
    private readonly ConcurrentDictionary<string, DateTime> _lastLogCheck = new();

    // Consecutive TIMEOUTS per "{serverId}:{containerId}" — only timeouts, because only they mean "these logs
    // cannot be read". Plus, per container, how long it is suspended and how far the backoff has climbed.
    private readonly ConcurrentDictionary<string, int> _consecutiveTimeouts = new();
    private readonly ConcurrentDictionary<string, DateTime> _suspendedUntil = new();
    private readonly ConcurrentDictionary<string, int> _backoffStep = new();

    // Container names, kept for the status view: a suspended container is no longer scanned, so its name
    // cannot reliably be looked up in the live list afterwards — and "badwolf:abc123f" is a riddle, not a report.
    private readonly ConcurrentDictionary<string, string> _suspendedNames = new();

    /// <summary>Timeouts in a row before a container is taken out of the scan (Plan-0002 WP3).</summary>
    private const int TimeoutsBeforeSuspension = 3;

    /// <summary>Backoff steps. Capped so a repaired container returns within the hour rather than never.</summary>
    private static readonly TimeSpan[] SuspensionBackoff =
    {
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)
    };

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    // A single wedged Docker connection must not stall the cycle forever: hosts are scanned in parallel,
    // but an unbounded fetch would still hold its own chain — and with it the start of the next cycle.
    private static readonly TimeSpan DefaultLogFetchTimeout = TimeSpan.FromSeconds(15);

    // Instance field only so a test can drive many cycles against a deliberately slow backend without
    // waiting 15 seconds each time. Production behaviour is unchanged: the constructor defaults to
    // DefaultLogFetchTimeout, and nothing outside the tests passes anything else.
    private readonly TimeSpan _logFetchTimeout;
    private readonly TimeSpan _maxLookback;

    /// <summary>The widest window a single log fetch may ask for (Plan-0002 WP1). Applying <c>since</c> costs
    /// the daemon the whole file — it decodes the JSON log from the start to find the cut-off — so an
    /// ever-widening window is genuinely more work, which is what turned a slow container into a permanently
    /// failing one on 2026-08-26. Lines older than this are lost after an outage; that is the deliberate side
    /// of the trade, because without the cap they were lost anyway, only permanently.</summary>
    /// <summary>The widest window a single fetch may ask for, whatever the watermark says (Plan-0002 WP1).
    ///
    /// <para>Overridable per instance for tests only. The cap engages solely when a watermark is older than
    /// this, which in production means minutes — a test cannot wait that long, and a test that pretends to
    /// exercise the cap with a 20-millisecond gap passes just as happily against a build with no cap at all.
    /// That version of this test existed and proved nothing.</para></summary>
    public static readonly TimeSpan MaxLookback = TimeSpan.FromMinutes(10);

    // Lines fetched per container per cycle. Since the Docker call now caps the transfer even with a
    // `since` filter, this is the real ceiling on how much of a burst we can still match in one cycle.
    private const int TailLines = 200;

    // Our own container must never be scanned for log alerts. Whiskers logs its own
    // "Log alert triggered: … {matchedLine}" and "Trivy scan failed … FATAL" lines; when an
    // "all containers" rule reads those back they re-match the pattern and create a
    // self-amplifying trigger loop (this is what ran the "Echte Fehler" rule up to 133×).
    // Self-monitoring, if ever wanted, must be a deliberate out-of-band mechanism, not this.
    // Override the excluded name(s) via WHISKERS_SELF_CONTAINERS (comma-separated; the legacy
    // SERVERWATCH_SELF_CONTAINERS is still read) — same source as the log-hygiene exclusions.
    private static readonly IReadOnlySet<string> SelfContainerNames =
        Hygiene.LogScanExclusions.ManualNamesFromEnvironment();

    public LogMonitorService(
        IServiceScopeFactory scopeFactory,
        IDockerService docker,
        IServerConfigService serverConfig,
        INotificationService notifications,
        ILogger<LogMonitorService> logger,
        Docker.Budget.IServerBudget budget,
        Observability.SelfMetrics.ISelfMetrics selfMetrics,
        Hygiene.ILogScanExclusions exclusions,
        Observability.Outcomes.IActionOutcomeService outcomes,
        TimeSpan? logFetchTimeout = null,
        TimeSpan? maxLookback = null)
    {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _serverConfig = serverConfig;
        _notifications = notifications;
        _logger = logger;
        _budget = budget;
        _selfMetrics = selfMetrics;
        _exclusions = exclusions;
        _outcomes = outcomes;
        _logFetchTimeout = logFetchTimeout ?? DefaultLogFetchTimeout;
        _maxLookback = maxLookback ?? MaxLookback;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Log monitor service started. Check interval: {Interval}s", CheckInterval.TotalSeconds);
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // initial delay

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The token now reaches the Docker calls themselves, so a stop ends the in-flight fetches
                // rather than merely abandoning them. WaitAsync stays as the backstop that bounds the cycle
                // even if some future step forgets to honour the token.
                await RunScanCycleAsync(stoppingToken).WaitAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Log monitor check failed");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    /// <summary>Runs one scan cycle over the whole fleet. Public so a test can drive a single cycle
    /// without the hosted-service loop (same test seam idea as ContainerHealthMonitor.IsRestart).</summary>
    public async Task RunScanCycleAsync(CancellationToken ct)
    {
        // Everything this cycle does is background work: it shares the background lane of the per-server
        // budget with the other loops and must never take slots from a waiting human (Plan-0001 WP3).
        using var background = _budget.BackgroundScope();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var rules = await db.LogAlertRules.Where(r => r.Enabled).ToListAsync(ct);
        if (rules.Count == 0) return;

        // Compile the regex rules once per cycle (keyed by pattern) instead of re-parsing each pattern
        // for every log line of every container. Invalid patterns are dropped here with a warning.
        var compiledRegexes = new Dictionary<string, Regex>();
        foreach (var r in rules.Where(r => r.IsRegex))
        {
            if (compiledRegexes.ContainsKey(r.Pattern)) continue;
            try { compiledRegexes[r.Pattern] = new Regex(r.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex) { _logger.LogWarning(ex, "Invalid log-alert regex '{Pattern}' — skipped", r.Pattern); }
        }

        // The fleet-wide list: ListContainersAsync() without a server id only ever returns the DEFAULT
        // server's containers, which silently limited every "all containers" rule to one host. The
        // detailed variant also reports which hosts answered — needed for the pruning below.
        var listing = await _docker.ListAllContainersDetailedAsync(all: false);
        var containers = listing.Containers;
        var selfServerIds = ResolveSelfServerIds();
        var hits = new ConcurrentQueue<LogAlertHit>();
        // Suspension/resume notices are collected here and sent on the cycle thread, next to the alerts, so
        // the order does not depend on which host answered first.
        var scanEvents = new ConcurrentQueue<NotificationEvent>();

        // One chain per server: hosts in parallel so remote latency doesn't add up over the fleet, the
        // containers of a host sequentially so we don't fan a burst of log requests at one connection.
        await Task.WhenAll(containers
            .GroupBy(c => c.ServerId, StringComparer.OrdinalIgnoreCase)
            .Select(g => ScanServerAsync(g.ToList(), rules, compiledRegexes, selfServerIds, hits, scanEvents, ct)));

        // Scan-health notices first: "this container is no longer being read" is context for any alert that
        // follows, and its absence is the thing an operator must not have to infer.
        foreach (var scanEvent in scanEvents)
            await _notifications.SendAsync(scanEvent);

        // Rule bookkeeping and notifications happen back here, on the cycle's own thread: MetricsDbContext
        // is not thread-safe, and the notification order shouldn't depend on which host answered first.
        foreach (var hit in hits)
        {
            hit.Rule.LastTriggered = DateTime.UtcNow;
            hit.Rule.TriggerCount++;

            await _notifications.SendAsync(new NotificationEvent
            {
                ContainerId = hit.Container.Id,
                ContainerName = hit.Container.Name,
                Image = hit.Container.Image,
                ServerId = hit.Container.ServerId,
                ServerName = hit.Container.ServerName,
                EventType = $"log_alert:{hit.Rule.Severity}",
                // The detail line every channel renders (NotificationFormatter.Detail): which rule fired,
                // on which host (container names repeat across a fleet) and the line that matched. The
                // matched line is third-party text — the Matrix HTML body escapes it (MatrixNotification-
                // Service.HtmlEscaped), the other channels are plain text.
                ImageInfo = $"{hit.Rule.Name} · {hit.Container.Name} @ {hit.Container.ServerName} — {hit.MatchedLine}",
                // Abuse RestartCount field for trigger count
                RestartCount = hit.Rule.TriggerCount
            });

            _logger.LogWarning("Log alert triggered: {RuleName} on {Container} ({Server}) — {Line}",
                hit.Rule.Name, hit.Container.Name, hit.Container.ServerName, hit.MatchedLine);
        }

        if (!hits.IsEmpty) await db.SaveChangesAsync(ct);

        // Bound the per-container maps: drop entries for containers no longer in the list — but ONLY for
        // servers that actually answered. An unreachable host contributes an empty list; dropping its
        // watermarks would re-baseline it to "now" on recovery, so every line written during the outage
        // would be silently skipped.
        var liveKeys = containers.Select(CompositeKey).ToHashSet();
        foreach (var kv in _cooldowns.ToArray())
        {
            var parts = kv.Key.Split(':', 2); // "ruleId:serverId:containerId"
            if (parts.Length == 2 && !liveKeys.Contains(parts[1]) && listing.MayPruneStateFor(ServerOfKey(parts[1])))
                _cooldowns.TryRemove(kv.Key, out _);
        }
        foreach (var key in _lastLogCheck.Keys)
            if (!liveKeys.Contains(key) && listing.MayPruneStateFor(ServerOfKey(key)))
                _lastLogCheck.TryRemove(key, out _);
    }

    /// <summary>Scans one server's containers sequentially and records the matches; the caller applies
    /// them to the database.</summary>
    private async Task ScanServerAsync(
        IReadOnlyList<ContainerInfo> containers,
        IReadOnlyList<LogAlertRuleEntity> rules,
        IReadOnlyDictionary<string, Regex> compiledRegexes,
        IReadOnlySet<string> selfServerIds,
        ConcurrentQueue<LogAlertHit> hits,
        ConcurrentQueue<NotificationEvent> events,
        CancellationToken ct)
    {
        int scanned = 0, noRules = 0, failed = 0;
        var serverId = containers.FirstOrDefault()?.ServerId ?? "?";
        var startedAt = DateTime.UtcNow;

        // Evaluated once per server per cycle rather than per container: the answer depends on the server's
        // configured access path, not on the container being looked at.
        var server = _serverConfig.GetServer(serverId);
        var skipped = server is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : _exclusions.Evaluate(server, containers).Select(e => e.ContainerId).ToHashSet(StringComparer.Ordinal);

        foreach (var container in containers)
        {
            if (ct.IsCancellationRequested) break;

            // Never scan the logs our own traffic writes — our own container (the self-amplifying alert
            // feedback loop) and the access-path container every Docker call passes through, whose log IS the
            // record of this scan (Plan-0007 WP1). Health, metrics and CVE keep covering them.
            if (skipped.Contains(container.Id)) continue;

            var applicableRules = rules.Where(r => RuleApplies(r, container)).ToList();
            if (applicableRules.Count == 0) { noRules++; continue; }

            var key = CompositeKey(container);

            // Suspended: its logs could not be read n times in a row, so asking again this cycle would only
            // repeat the cost. It is NOT silently skipped — the suspension was announced when it started, and
            // the container is marked as unmonitored rather than left looking unremarkable (Plan-0002 WP3).
            if (_suspendedUntil.TryGetValue(key, out var until) && DateTime.UtcNow < until) continue;

            scanned++;

            try
            {
                // Fetch only lines since our last check so an old ERROR line doesn't re-alert every cycle;
                // on first sight, baseline to now so historical logs aren't alerted. The watermark is taken
                // BEFORE the fetch: with remote hosts the round trip is long enough that lines written
                // during it would otherwise fall into neither window. A line seen twice at the window edge
                // is caught by the rule cooldown; a line lost is lost for good.
                var fetchedAt = DateTime.UtcNow;
                // Capped at MaxLookback. Without the cap a failure left `since` behind while `now` moved on,
                // so every failed cycle asked for a wider window than the last — failure made the next attempt
                // more expensive, and the state was self-sustaining (Plan-0002 WP1).
                var floor = fetchedAt - _maxLookback;
                var last = _lastLogCheck.TryGetValue(key, out var watermark) ? watermark : fetchedAt;
                var since = last > floor ? last : floor;
                var logs = await FetchLogsAsync(container, since, ct);
                _lastLogCheck[key] = fetchedAt;
                NoteReadable(container, key, events);
                var lines = logs.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                // Per-container trace of what the scan actually saw. Without it, "no alert" cannot be told
                // apart from "not scanned", "nothing new" or "read failed" — a bad place to be for the
                // component whose whole job is noticing things.
                _logger.LogDebug("Scanned {Container} on {Server}: {Lines} new line(s) since {Since:HH:mm:ss}, {Rules} rule(s)",
                    container.Name, container.ServerName, lines.Length, since, applicableRules.Count);

                foreach (var rule in applicableRules)
                {
                    // Cooldown check — per rule AND per container, and the container is only identified by
                    // server + id: two hosts running a same-named container must not share one cooldown.
                    var cooldownKey = $"{rule.RuleId}:{key}";
                    if (_cooldowns.TryGetValue(cooldownKey, out var lastTriggered) &&
                        DateTime.UtcNow - lastTriggered < TimeSpan.FromMinutes(rule.CooldownMinutes))
                        continue;

                    // Pattern match
                    string? matchedLine = null;

                    foreach (var line in lines)
                    {
                        bool hit = rule.IsRegex
                            ? compiledRegexes.TryGetValue(rule.Pattern, out var rx) && rx.IsMatch(line)
                            : line.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);

                        if (hit)
                        {
                            matchedLine = line.Length > 200 ? line[..200] : line;
                            break;
                        }
                    }

                    if (matchedLine != null)
                    {
                        _cooldowns[cooldownKey] = DateTime.UtcNow;
                        hits.Enqueue(new LogAlertHit(rule, container, matchedLine));
                    }
                }
            }
            catch (TimeoutException ex)
            {
                failed++;
                // The watermark advances even though nothing was read: the lines in this window are lost, and
                // that is the deliberate side of the trade (Plan-0002 WP1). Leaving it behind is what made the
                // next attempt more expensive and the failure permanent. The lost span is named in the alert
                // rather than quietly dropped.
                _lastLogCheck[key] = DateTime.UtcNow;
                _selfMetrics.Count("log_fetch_timeouts", container.ServerId);
                _logger.LogDebug(ex, "Log fetch for {Container} on {Server} timed out", container.Name, container.ServerName);
                NoteUnreadable(container, key, events);
            }
            catch (Exception ex)
            {
                failed++;
                // Everything else — container gone, host refused, malformed response — says nothing about the
                // logs being unreadable, so it must not count towards a suspension. Reporting a removed
                // container as a scan problem would page someone every time a job container finishes.
                _lastLogCheck[key] = DateTime.UtcNow;
                _logger.LogDebug(ex, "Failed to check logs for {Container} on {Server}", container.Name, container.ServerName);
            }
        }

        // A cycle counts as successful when it got through without every container failing. The point of the
        // record is the timestamp: a loop that has stopped writes nothing at all, and only the AGE of the last
        // success reveals that — failures are only counted while something still happens.
        _selfMetrics.RecordCycle("logmonitor", serverId, DateTime.UtcNow - startedAt,
            success: failed == 0 || scanned > failed, interval: CheckInterval);

        _logger.LogDebug("Scan of {Server} done: {Scanned} scanned, {NoRules} without a matching rule, {Failed} failed, of {Total}",
            containers.FirstOrDefault()?.ServerName ?? "?", scanned, noRules, failed, containers.Count);
    }

    /// <summary>Fetches a container's new log lines, bounded by <see cref="DefaultLogFetchTimeout"/>.
    ///
    /// <para>The bound is a linked <see cref="CancellationTokenSource"/>, not a race against a timer. The
    /// earlier <c>Task.WhenAny(fetch, Task.Delay(...))</c> ended the <em>wait</em> and left the <em>request</em>
    /// running: dockerd kept reading the log file until the proxy cut the connection 600 seconds later, while
    /// a new fetch started every cycle. Ten concurrent full-log scans per container was the stable end state —
    /// 184% of 200% CPU for six days (incident 2026-08-26). Cancelling the token instead ends the request on
    /// the server as well, which is the whole point.</para></summary>
    private async Task<string> FetchLogsAsync(ContainerInfo container, DateTime since, CancellationToken ct)
    {
        using var fetchTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fetchTimeout.CancelAfter(_logFetchTimeout);

        try
        {
            return await _docker.GetContainerLogsAsync(container.Id, TailLines, container.ServerId, since, fetchTimeout.Token);
        }
        // Only OUR timeout is turned into a failure for this container. A cancelled shutdown token belongs to
        // the caller and must keep propagating, or a stop would look like a fleet of broken containers.
        catch (OperationCanceledException) when (fetchTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Log fetch for {Container} on {Server} aborted after {Timeout}s — request cancelled on the server, not just abandoned here",
                container.Name, container.ServerName, _logFetchTimeout.TotalSeconds);
            throw new TimeoutException($"Log fetch for {container.Name} on {container.ServerName} timed out.");
        }
    }

    /// <summary>Counts a timeout and, once the run is long enough, takes the container out of the scan with a
    /// growing backoff — and says so. A container whose logs cannot be read is worth a message, not an endless
    /// retry: the fetch timeouts were already being written to the log on 2026-08-26 and nobody counted them,
    /// which is why the earliest and most precise signal of the whole incident went unused for six days.</summary>
    private void NoteUnreadable(ContainerInfo container, string key, ConcurrentQueue<NotificationEvent> events)
    {
        var timeouts = _consecutiveTimeouts.AddOrUpdate(key, 1, (_, n) => n + 1);
        if (timeouts < TimeoutsBeforeSuspension) return;
        if (_suspendedUntil.ContainsKey(key)) return;   // already suspended; the notice was sent once

        var step = _backoffStep.AddOrUpdate(key, 0, (_, n) => Math.Min(n + 1, SuspensionBackoff.Length - 1));
        var pause = SuspensionBackoff[step];
        _suspendedUntil[key] = DateTime.UtcNow + pause;
        _suspendedNames[key] = container.Name;

        // Plan-0006: an automatic action, so it gets checked. If the host is still slow when the window
        // closes, this container was not the problem and it is now unmonitored for nothing.
        _ = _outcomes.RecordAsync(
            Observability.Outcomes.AutomaticActionKind.LogScanLockout,
            container.ServerId, container.Id, container.Name, $"{timeouts} log fetches in a row timed out");

        _logger.LogWarning("Log scan for {Container} on {Server} suspended for {Pause} after {Timeouts} timeouts",
            container.Name, container.ServerName, pause, timeouts);

        events.Enqueue(new NotificationEvent
        {
            EventType = "log_scan_suspended",
            ContainerId = container.Id,
            ContainerName = container.Name,
            Image = container.Image,
            ServerId = container.ServerId,
            ServerName = container.ServerName,
            ImageInfo = $"{timeouts} log fetches in a row timed out. Whiskers is not reading this container's " +
                        $"logs for {pause.TotalMinutes:0} minutes — alert rules covering it produce nothing " +
                        "meanwhile, which is not the same as 'no problems'."
        });
    }

    /// <summary>A readable fetch clears the run. If the container had been suspended, its return is announced
    /// too — otherwise the operator is left believing it is still unmonitored.</summary>
    private void NoteReadable(ContainerInfo container, string key, ConcurrentQueue<NotificationEvent> events)
    {
        _consecutiveTimeouts.TryRemove(key, out _);
        _backoffStep.TryRemove(key, out _);
        if (!_suspendedUntil.TryRemove(key, out _)) return;

        _logger.LogInformation("Log scan for {Container} on {Server} resumed", container.Name, container.ServerName);
        events.Enqueue(new NotificationEvent
        {
            EventType = "log_scan_resumed",
            ContainerId = container.Id,
            ContainerName = container.Name,
            Image = container.Image,
            ServerId = container.ServerId,
            ServerName = container.ServerName,
            ImageInfo = "The container's logs are readable again and it is back under the alert rules."
        });
    }

    /// <summary>Identifies a container across the fleet. Container ids are unique per host only, so every
    /// per-container map has to be keyed by server + id (same scheme as ContainerHealthMonitor).</summary>
    /// <summary>The containers the scan is currently not reading, for the status view (Plan-0002 WP5).
    ///
    /// <para>Names are remembered alongside the key: once a container is suspended the scan stops looking at
    /// it, so the container list is not a reliable place to look the name up again — and "server:abc123f" in
    /// a UI is a riddle, not a report.</para></summary>
    public IReadOnlyList<SuspendedContainer> SuspendedContainers()
    {
        var now = DateTime.UtcNow;

        return _suspendedUntil
            .Where(kv => kv.Value > now)
            .Select(kv =>
            {
                var parts = kv.Key.Split(':', 2);
                var serverId = parts[0];
                var containerId = parts.Length > 1 ? parts[1] : string.Empty;
                return new SuspendedContainer(
                    serverId, containerId,
                    _suspendedNames.GetValueOrDefault(kv.Key, containerId),
                    kv.Value,
                    _consecutiveTimeouts.GetValueOrDefault(kv.Key));
            })
            .OrderBy(s => s.ServerId, StringComparer.Ordinal)
            .ThenBy(s => s.ContainerName, StringComparer.Ordinal)
            .ToList();
    }

    public static string CompositeKey(ContainerInfo container) => $"{container.ServerId}:{container.Id}";

    /// <summary>The server part of a <see cref="CompositeKey"/>.</summary>
    public static string ServerOfKey(string compositeKey) => compositeKey.Split(':', 2)[0];

    /// <summary>A rule applies to a container when it carries no filter at all ("all containers"), or when
    /// its filter matches by id or by name.
    /// <para>A NAME-only filter is the normal case — both the UI dialog and the MCP tool set ContainerName
    /// and leave ContainerId null — so the "no filter" test has to look at BOTH fields. Testing ContainerId
    /// alone (as this did) short-circuited every name-filtered rule into an all-containers rule; harmless
    /// while the scan saw one host, an alert storm once it sees the whole fleet.</para>
    /// <para>A name filter deliberately matches on EVERY server: the UI picker lists the containers of all
    /// servers but stores only the name, so a name rule means "this workload, wherever it runs". Pinning a
    /// rule to one server would need a new column on LogAlertRuleEntity.</para></summary>
    public static bool RuleApplies(LogAlertRuleEntity rule, ContainerInfo container) =>
        (rule.ContainerId == null && rule.ContainerName == null)
        || rule.ContainerId == container.Id
        || rule.ContainerName == container.Name;

    /// <summary>The self-log guard, restricted to the host we ourselves run on. A container that merely
    /// shares our name on a REMOTE host is a different process and must stay monitored.</summary>
    public static bool IsSelfContainer(ContainerInfo container, IReadOnlySet<string> selfServerIds) =>
        selfServerIds.Contains(container.ServerId) && SelfContainerNames.Contains(container.Name);

    /// <summary>The server(s) whose containers can be our OWN container: Whiskers reaches its own host
    /// through the <see cref="ConnectionType.Local"/> entry. If the fleet has no local server (an all-mTLS
    /// or Kubernetes setup, where our own container may not be visible at all), fall back to the default
    /// server — that is exactly the host the old single-server scan used to look at.</summary>
    public static IReadOnlySet<string> ResolveSelfServerIds(IServerConfigService serverConfig)
    {
        var ids = serverConfig.GetServers()
            .Where(s => s.ConnectionType == ConnectionType.Local)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (ids.Count == 0 && serverConfig.GetDefaultServer() is { } fallback)
            ids.Add(fallback.Id);

        return ids;
    }

    private IReadOnlySet<string> ResolveSelfServerIds() => ResolveSelfServerIds(_serverConfig);

    /// <summary>A pattern match found while scanning; applied to the rule stats and notifications
    /// afterwards, off the parallel scan.</summary>
    private sealed record LogAlertHit(LogAlertRuleEntity Rule, ContainerInfo Container, string? MatchedLine);

    // === Public API ===

    public async Task<List<LogAlertRuleEntity>> GetRulesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.LogAlertRules.OrderBy(r => r.Name).ToListAsync();
    }

    public async Task<LogAlertRuleEntity> CreateRuleAsync(LogAlertRuleEntity rule)
    {
        // Validate regex. The timeout is defense-in-depth only — this compiles (it does not match), and the
        // actual match paths (LogSearchService and the monitor loop) already run every pattern under a timeout.
        if (rule.IsRegex)
            _ = new Regex(rule.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); // throws on invalid

        await _rules.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.LogAlertRules.Add(rule);
            await db.SaveChangesAsync();
            // The versioned, redacted change is part of the gateway's event (R1-22); the pattern shows as text only.
            Whiskers.Services.Mutations.ConfigChange.Record($"log-alert:{rule.RuleId}", null, AuditView(rule));
            return rule;
        }
        finally { _rules.Release(); }
    }

    public async Task DeleteRuleAsync(string ruleId)
    {
        await _rules.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var rule = await db.LogAlertRules.FirstOrDefaultAsync(r => r.RuleId == ruleId);
            if (rule != null)
            {
                var before = AuditView(rule);
                db.LogAlertRules.Remove(rule);
                await db.SaveChangesAsync();
                Whiskers.Services.Mutations.ConfigChange.Record($"log-alert:{ruleId}", before, null);   // R1-22
            }
        }
        finally { _rules.Release(); }
    }

    public async Task ToggleRuleAsync(string ruleId, bool enabled)
    {
        await _rules.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var rule = await db.LogAlertRules.FirstOrDefaultAsync(r => r.RuleId == ruleId);
            if (rule != null)
            {
                var before = AuditView(rule);
                rule.Enabled = enabled;
                await db.SaveChangesAsync();
                Whiskers.Services.Mutations.ConfigChange.Record($"log-alert:{ruleId}", before, AuditView(rule));   // R1-22
            }
        }
        finally { _rules.Release(); }
    }

    /// <summary>What the audit compares and versions for a rule (R1-22): its configuration, not its trigger counters,
    /// which change with every match.</summary>
    private static object? AuditView(LogAlertRuleEntity rule)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(rule)!.AsObject();
        node.Remove(nameof(LogAlertRuleEntity.LastTriggered));
        node.Remove(nameof(LogAlertRuleEntity.TriggerCount));
        return System.Text.Json.JsonSerializer.SerializeToElement(node);
    }
}
