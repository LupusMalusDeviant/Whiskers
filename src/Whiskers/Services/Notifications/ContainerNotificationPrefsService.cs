using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Notifications;

public class ContainerNotificationPrefsService : IContainerNotificationPrefsService, IInitializable
{
    private readonly JsonFileStore<ContainerNotificationPrefs> _store;
    private ContainerNotificationPrefs _data = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ContainerNotificationPrefsService(DataPathOptions? dataPaths = null)
    {
        _store = new JsonFileStore<ContainerNotificationPrefs>((dataPaths ?? DataPathOptions.Default).NotificationPrefsJson);
    }

    public int Order => 30;

    public async Task InitializeAsync(CancellationToken ct = default)
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
