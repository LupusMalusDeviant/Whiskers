using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Docker.Budget;
using Whiskers.Services.Notifications;
using Whiskers.Services.Observability.SelfMetrics;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.LogMonitor.Hygiene;

/// <summary>
/// Takes the log inventory once a day and reports what it finds (Plan-0007 WP3/WP4).
///
/// <para>Daily, not hourly: one Docker inspect and one <c>stat</c> per container per day is the whole cost,
/// and a monitor that polls the disk it is worried about would be the 2026-08-26 incident one level down. The
/// growth rate needs consecutive readings anyway, so a faster cadence buys nothing.</para>
///
/// <para>It reports and never repairs. Setting a rotation limit recreates the container.</para>
/// </summary>
public sealed class LogHygieneMonitor : Whiskers.Services.FleetBackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    /// <summary>An alert is not repeated until this has passed. The finding is a slow-moving fact; repeating
    /// it every day would train people to filter it, and a filtered alert is worse than none.</summary>
    private static readonly TimeSpan RepeatAfter = TimeSpan.FromDays(7);

    private readonly ILogInventory _inventory;
    private readonly IDockerService _docker;
    private readonly IServerConfigService _servers;
    private readonly INotificationService _notifications;
    private readonly IServerBudget _budget;
    private readonly ISelfMetrics _selfMetrics;
    private readonly ILogger<LogHygieneMonitor> _logger;
    private readonly Dictionary<string, DateTime> _lastAlerted = new(StringComparer.Ordinal);

    public LogHygieneMonitor(
        ILogInventory inventory,
        IDockerService docker,
        IServerConfigService servers,
        INotificationService notifications,
        IServerBudget budget,
        ISelfMetrics selfMetrics,
        ILogger<LogHygieneMonitor> logger)
    {
        _inventory = inventory;
        _docker = docker;
        _servers = servers;
        _notifications = notifications;
        _budget = budget;
        _selfMetrics = selfMetrics;
        _logger = logger;
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).WaitAsync(stoppingToken);
            }
            catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))
            {
                _logger.LogError(ex, "Log hygiene survey failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <summary>One pass over the fleet. Public so it can be driven from a test and from a scheduled task.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        // Background lane: this competes with the checks that actually matter, and it is never urgent.
        using var scope = _budget.BackgroundScope();

        foreach (var server in _servers.GetEnabledServers())
        {
            if (ct.IsCancellationRequested) break;
            if (server.ConnectionType == ConnectionType.Kubernetes)
            {
                // Pod logs are the cluster's business, not a file on a Docker host. Recorded as a skip rather
                // than passed over: a loop that silently produces nothing for a server looks like a healthy one.
                _selfMetrics.RecordSkip("loghygiene", server.Id, "kubernetes");
                continue;
            }

            var startedAt = DateTime.UtcNow;
            var success = true;

            try
            {
                var containers = (await _docker.ListContainersAsync(all: true, server.Id, ct)).ToList();
                var entries = await _inventory.SurveyAsync(server, containers, ct);
                await ReportAsync(server, containers, entries);
            }
            catch (Exception ex)
            {
                success = false;
                _logger.LogWarning(ex, "Log hygiene survey failed for {Server}", server.Name);
            }

            _selfMetrics.RecordCycle("loghygiene", server.Id, DateTime.UtcNow - startedAt, success, Interval);
        }
    }

    private async Task ReportAsync(
        Models.ServerConfig server, IReadOnlyList<ContainerInfo> containers, IReadOnlyList<LogInventoryEntry> entries)
    {
        foreach (var entry in entries)
        {
            var severity = LogHygieneAdvice.Severity(entry);

            // A note is an inventory entry, not a message (WP4.1). Whoever looks at the view sees it; nobody
            // is woken for a 40 MB log with room to spare.
            if (severity != LogHygieneSeverity.Alert) continue;

            var key = $"{entry.ServerId}|{entry.ContainerId}";
            if (_lastAlerted.TryGetValue(key, out var last) && DateTime.UtcNow - last < RepeatAfter) continue;
            _lastAlerted[key] = DateTime.UtcNow;

            var labels = containers.FirstOrDefault(c => c.Id == entry.ContainerId)?.Labels
                         ?? new Dictionary<string, string>();

            var detail = string.Join("\n\n",
                LogHygieneAdvice.Describe(entry, server.Name),
                LogHygieneAdvice.Remediation(entry, labels),
                LogHygieneAdvice.TriggerNotCause);

            _logger.LogWarning("Unbounded log on {Server}/{Container}: {Size}",
                server.Name, entry.ContainerName, LogHygieneAdvice.Humanise(entry.SizeBytes));

            await _notifications.SendAsync(new NotificationEvent
            {
                EventType = "log_rotation_missing",
                ServerId = entry.ServerId,
                ServerName = server.Name,
                ContainerName = entry.ContainerName,
                ImageInfo = detail,
                Timestamp = DateTime.UtcNow
            });
        }
    }
}
