using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Hubs;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Notifications;

using Whiskers.Services.Observability.SelfMetrics;

namespace Whiskers.Services.HealthMonitor;

public class ContainerHealthMonitor : Whiskers.Services.FleetBackgroundService
{
    private readonly IDockerService _docker;
    private readonly IHealthStore _healthStore;
    private readonly INotificationService _notifications;
    private readonly IHubContext<ContainerHub> _hubContext;
    private readonly HealthMonitorSettings _settings;
    private readonly ILogger<ContainerHealthMonitor> _logger;
    private readonly Whiskers.Services.Observability.SelfMetrics.ISelfMetrics _selfMetrics;
    private readonly Whiskers.Services.Observability.ILoopSuspensionService _suspension;
    private readonly Whiskers.Services.ServerConfig.IServerConfigService _serverConfig;

    private readonly ConcurrentDictionary<string, string> _previousStates = new();
    private readonly ConcurrentDictionary<string, string> _previousHealth = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _restartTimestamps = new();
    // A host dropping off the fleet used to be silent everywhere; the tracker turns "did not answer"
    // into server_unreachable / server_recovered events (see ServerReachabilityTracker).
    private readonly ServerReachabilityTracker _reachability;
    private readonly EffectiveInterval<HealthMonitorSettings> _interval;

    public ContainerHealthMonitor(
        IDockerService docker,
        IHealthStore healthStore,
        INotificationService notifications,
        IHubContext<ContainerHub> hubContext,
        IOptions<HealthMonitorSettings> settings,
        ILogger<ContainerHealthMonitor> logger,
        Whiskers.Services.Observability.SelfMetrics.ISelfMetrics selfMetrics,
        Whiskers.Services.Observability.ILoopSuspensionService suspension,
        Whiskers.Services.ServerConfig.IServerConfigService serverConfig,
        EffectiveInterval<HealthMonitorSettings>? interval = null)
    {
        // R1-41: the interval in use — a configured value out of range is clamped (and warned about), never handed to
        // Task.Delay as it is; shared with everything that reports against it.
        _interval = interval ?? IntervalValidation.HealthCheck(settings.Value, logger);
        _docker = docker;
        _healthStore = healthStore;
        _notifications = notifications;
        _hubContext = hubContext;
        _settings = settings.Value;
        _logger = logger;
        _selfMetrics = selfMetrics;
        _suspension = suspension;
        _serverConfig = serverConfig;
        _reachability = new ServerReachabilityTracker(
            _settings.ServerUnreachableCycles, _settings.ServerUnreachableColdStartCycles);
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("Container health monitor started (interval: {Interval}s)",
            _interval.Value);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // R1-41: the cycle is owned, not abandoned with WaitAsync — an abandoned cycle went on after the stop and
                // recorded metrics, sent notifications and touched the per-container state once its listing answered.
                // The stop now reaches the listing, every inspect and every notification, and the cycle checks it before
                // each effect, so a stop ends the cycle without one.
                await RunHealthCycleAsync(ct);
            }
            catch (Exception ex) when (!IsHostStopping(ex, ct))
            {
                _logger.LogError(ex, "Health check cycle failed");
            }

            await Task.Delay(_interval.Interval, ct);
        }
    }

    private async Task RunHealthCycleAsync(CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var listing = await _docker.ListAllContainersDetailedAsync(all: true, ct);
        // A listing that answers after the stop (a dependency that ignored the token) changes nothing.
        ct.ThrowIfCancellationRequested();
        var containers = listing.Containers;

        // Every server this cycle touched gets a record, and every Kubernetes server it stepped over gets a
        // skip. Without the skip a K8s host produces no health metrics at all, which looks exactly like a
        // host with nothing wrong (Plan-0003 WP2).
        var interval = _interval.Interval;
        foreach (var responded in listing.RespondedServerIds)
            _selfMetrics.RecordCycle(SelfMetricsFleetExtensions.Loops.Health, responded, DateTime.UtcNow - startedAt, success: true,
                interval: interval);
        foreach (var failure in listing.FailedServers)
            _selfMetrics.RecordCycle(SelfMetricsFleetExtensions.Loops.Health, failure.ServerId, DateTime.UtcNow - startedAt, success: false,
                interval: interval);
        _selfMetrics.RecordKubernetesSkips(SelfMetricsFleetExtensions.Loops.Health, _serverConfig.GetEnabledServers());

        await ProcessServerReachabilityAsync(listing, ct);

        foreach (var container in containers)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessContainer(container, ct);
        }

        // Bound the per-container maps: drop entries for containers that no longer exist — but keep the
        // state of servers that did NOT answer this cycle. Their containers are missing from the list
        // because the host is silent, not because they are gone; dropping the state here means a real
        // stop during the outage is never reported and every container looks "new" on recovery.
        ct.ThrowIfCancellationRequested();
        var liveKeys = containers.Select(CompositeKey).ToHashSet();
        PruneToLive(_previousStates, liveKeys, listing);
        PruneToLive(_restartTimestamps, liveKeys, listing);
        PruneToLive(_previousHealth, liveKeys, listing);

        await _hubContext.Clients.All.SendAsync("ContainerListUpdated", containers, ct);
    }

    private async Task ProcessServerReachabilityAsync(FleetContainerListing listing, CancellationToken ct)
    {
        foreach (var evt in _reachability.Evaluate(listing))
        {
            // A paused server is silent on purpose. Reporting it as unreachable would page the operator about
            // the switch they just pressed, and would bury the pause announcement under its own consequence
            // (Plan-0005 WP1). The pause is not hidden — it was announced, and it carries its own reminder.
            if (_suspension.IsSuspended(evt.ServerId ?? string.Empty)) continue;

            if (evt.EventType == "server_unreachable")
                _logger.LogWarning("Server {ServerName} unreachable: {Detail}", evt.ServerName, evt.ImageInfo);
            else
                _logger.LogInformation("Server {ServerName} is reachable again", evt.ServerName);

            ct.ThrowIfCancellationRequested();
            await _notifications.SendAsync(evt, ct);
        }
    }

    private static string CompositeKey(ContainerInfo container)
        => $"{container.ServerId}:{container.Id}";

    private static void PruneToLive<TValue>(
        ConcurrentDictionary<string, TValue> map, IReadOnlySet<string> liveKeys, FleetContainerListing listing)
    {
        foreach (var key in map.Keys)
            if (!liveKeys.Contains(key) && listing.MayPruneStateFor(key.Split(':', 2)[0]))
                map.TryRemove(key, out _);
    }

    private async Task ProcessContainer(ContainerInfo container, CancellationToken ct)
    {
        var key = CompositeKey(container);
        var (state, exitCode, oomKilled) = await SafeInspect(container.Id, container.ServerId, ct);
        ct.ThrowIfCancellationRequested();

        var record = new HealthRecord
        {
            ContainerId = container.Id,
            ContainerName = container.Name,
            Timestamp = DateTime.UtcNow,
            State = state,
            HealthStatus = container.HealthStatus,
            ExitCode = exitCode,
            OomKilled = oomKilled
        };

        _healthStore.AddRecord(record);

        // Detect unhealthy transition
        if (_previousHealth.TryGetValue(key, out var prevHealth))
        {
            if (prevHealth != "unhealthy" && container.HealthStatus == "unhealthy")
            {
                await SendNotificationIfAllowed(ct, new NotificationEvent
                {
                    ContainerId = container.Id,
                    ContainerName = container.Name,
                    Image = container.Image,
                    ServerId = container.ServerId,
                    ServerName = container.ServerName,
                    EventType = "unhealthy"
                });
            }
        }
        _previousHealth[key] = container.HealthStatus;

        // Detect unexpected stop
        if (_previousStates.TryGetValue(key, out var prevState))
        {
            if (prevState == "running" && state == "exited")
            {
                if (oomKilled)
                {
                    await SendNotificationIfAllowed(ct, new NotificationEvent
                    {
                        ContainerId = container.Id,
                        ContainerName = container.Name,
                        Image = container.Image,
                        ServerId = container.ServerId,
                        ServerName = container.ServerName,
                        EventType = "oom_killed"
                    });
                }
                else if (exitCode != 0)
                {
                    await SendNotificationIfAllowed(ct, new NotificationEvent
                    {
                        ContainerId = container.Id,
                        ContainerName = container.Name,
                        Image = container.Image,
                        ServerId = container.ServerId,
                        ServerName = container.ServerName,
                        EventType = "stopped",
                        ExitCode = exitCode
                    });
                }
            }

            // Detect restart loops
            if (IsRestart(prevState, state))
            {
                var timestamps = _restartTimestamps.GetOrAdd(key, _ => new List<DateTime>());
                timestamps.Add(DateTime.UtcNow);

                var windowStart = DateTime.UtcNow.AddMinutes(-_settings.RestartLoopWindowMinutes);
                timestamps.RemoveAll(t => t < windowStart);

                if (timestamps.Count >= _settings.RestartLoopThreshold)
                {
                    await SendNotificationIfAllowed(ct, new NotificationEvent
                    {
                        ContainerId = container.Id,
                        ContainerName = container.Name,
                        Image = container.Image,
                        ServerId = container.ServerId,
                        ServerName = container.ServerName,
                        EventType = "restart_loop",
                        RestartCount = timestamps.Count,
                        WindowMinutes = _settings.RestartLoopWindowMinutes
                    });
                    timestamps.Clear();
                }
            }
        }
        // Don't overwrite the last known state with "unknown" (a transient inspect failure, e.g. a
        // flapping SSH tunnel) — otherwise the next real "running" reads as a restart and a real stop
        // is missed.
        if (state != "unknown")
            _previousStates[key] = state;
    }

    /// <summary>Send a container event. The per-container mute/prefs check that used to live here now runs
    /// centrally in <see cref="CompositeNotificationService"/>, so every producer honours it — not just this
    /// monitor.</summary>
    private Task SendNotificationIfAllowed(CancellationToken ct, NotificationEvent evt) => _notifications.SendAsync(evt, ct);

    private async Task<(string State, int ExitCode, bool OomKilled)> SafeInspect(string containerId, string serverId, CancellationToken ct)
    {
        try
        {
            return await _docker.InspectContainerStateAsync(containerId, serverId, ct);
        }
        // The stop is not an unknown state: it ends the cycle.
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            return ("unknown", 0, false);
        }
    }

    /// <summary>A restart is a container now <c>running</c> that was previously in a real stopped state.
    /// A prior <c>unknown</c> → <c>running</c> (e.g. a flapping SSH-tunnel inspect) is NOT a restart — that
    /// was the source of phantom restart-loop alerts.</summary>
    public static bool IsRestart(string? prevState, string state)
        => state == "running" && prevState is "exited" or "restarting" or "created" or "dead";
}
