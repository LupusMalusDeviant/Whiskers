using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Hubs;
using ServerWatch.Models;
using ServerWatch.Services.Docker;
using ServerWatch.Services.Notifications;

namespace ServerWatch.Services.HealthMonitor;

public class ContainerHealthMonitor : BackgroundService
{
    private readonly IDockerService _docker;
    private readonly IHealthStore _healthStore;
    private readonly INotificationService _notifications;
    private readonly ContainerNotificationPrefsService _notifPrefs;
    private readonly IHubContext<ContainerHub> _hubContext;
    private readonly HealthMonitorSettings _settings;
    private readonly ILogger<ContainerHealthMonitor> _logger;

    private readonly ConcurrentDictionary<string, string> _previousStates = new();
    private readonly ConcurrentDictionary<string, string> _previousHealth = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _restartTimestamps = new();

    public ContainerHealthMonitor(
        IDockerService docker,
        IHealthStore healthStore,
        INotificationService notifications,
        ContainerNotificationPrefsService notifPrefs,
        IHubContext<ContainerHub> hubContext,
        IOptions<HealthMonitorSettings> settings,
        ILogger<ContainerHealthMonitor> logger)
    {
        _docker = docker;
        _healthStore = healthStore;
        _notifications = notifications;
        _notifPrefs = notifPrefs;
        _hubContext = hubContext;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Container health monitor started (interval: {Interval}s)",
            _settings.CheckIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var containers = await _docker.ListAllContainersAsync(all: true);

                foreach (var container in containers)
                {
                    await ProcessContainer(container);
                }

                await _hubContext.Clients.All.SendAsync("ContainerListUpdated", containers, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.CheckIntervalSeconds), ct);
        }
    }

    private static string CompositeKey(ContainerInfo container)
        => $"{container.ServerId}:{container.Id}";

    private async Task ProcessContainer(ContainerInfo container)
    {
        var key = CompositeKey(container);
        var (state, exitCode, oomKilled) = await SafeInspect(container.Id, container.ServerId);

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
                await SendNotificationIfAllowed(new NotificationEvent
                {
                    ContainerId = container.Id,
                    ContainerName = container.Name,
                    Image = container.Image,
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
                    await SendNotificationIfAllowed(new NotificationEvent
                    {
                        ContainerId = container.Id,
                        ContainerName = container.Name,
                        Image = container.Image,
                        EventType = "oom_killed"
                    });
                }
                else if (exitCode != 0)
                {
                    await SendNotificationIfAllowed(new NotificationEvent
                    {
                        ContainerId = container.Id,
                        ContainerName = container.Name,
                        Image = container.Image,
                        EventType = "stopped",
                        ExitCode = exitCode
                    });
                }
            }

            // Detect restart loops
            if (prevState != "running" && state == "running")
            {
                var timestamps = _restartTimestamps.GetOrAdd(key, _ => new List<DateTime>());
                timestamps.Add(DateTime.UtcNow);

                var windowStart = DateTime.UtcNow.AddMinutes(-_settings.RestartLoopWindowMinutes);
                timestamps.RemoveAll(t => t < windowStart);

                if (timestamps.Count >= _settings.RestartLoopThreshold)
                {
                    await SendNotificationIfAllowed(new NotificationEvent
                    {
                        ContainerId = container.Id,
                        ContainerName = container.Name,
                        Image = container.Image,
                        EventType = "restart_loop",
                        RestartCount = timestamps.Count,
                        WindowMinutes = _settings.RestartLoopWindowMinutes
                    });
                    timestamps.Clear();
                }
            }
        }
        _previousStates[key] = state;
    }

    /// <summary>Send notification only if per-container prefs allow it.</summary>
    private async Task SendNotificationIfAllowed(NotificationEvent evt)
    {
        if (_notifPrefs.ShouldNotify(evt.ContainerName, evt.EventType))
            await _notifications.SendAsync(evt);
        else
            _logger.LogDebug("Notification suppressed for {Container} ({EventType}) — muted by prefs", evt.ContainerName, evt.EventType);
    }

    private async Task<(string State, int ExitCode, bool OomKilled)> SafeInspect(string containerId, string serverId)
    {
        try
        {
            return await _docker.InspectContainerStateAsync(containerId, serverId);
        }
        catch
        {
            return ("unknown", 0, false);
        }
    }
}
