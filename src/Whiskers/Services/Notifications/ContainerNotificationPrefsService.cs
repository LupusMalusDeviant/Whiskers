using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Notifications;

public class ContainerNotificationPrefsService : IContainerNotificationPrefsService
{
    private readonly JsonFileStore<ContainerNotificationPrefs> _store;
    private ContainerNotificationPrefs _data = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ContainerNotificationPrefsService()
    {
        _store = new JsonFileStore<ContainerNotificationPrefs>("/app/data/notification-prefs.json");
    }

    public async Task InitializeAsync()
    {
        if (_store.Exists())
            _data = await _store.LoadAsync();
    }

    public ContainerNotifEntry GetPrefs(string containerName)
    {
        return _data.Containers.GetValueOrDefault(containerName) ?? new ContainerNotifEntry();
    }

    public bool ShouldNotify(string containerName, string eventType)
    {
        var prefs = GetPrefs(containerName);
        if (prefs.Muted) return false;

        return eventType switch
        {
            "stopped" => prefs.NotifyOnDown,
            "unhealthy" => prefs.NotifyOnUnhealthy,
            "oom_killed" => prefs.NotifyOnOom,
            "image_update" or "auto_update_start" or "auto_update_failed" => prefs.NotifyOnUpdate,
            _ => true
        };
    }

    public async Task SavePrefsAsync(string containerName, ContainerNotifEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            _data.Containers[containerName] = entry;
            await _store.SaveAsync(_data);
        }
        finally { _lock.Release(); }
    }
}
