using ServerWatch.Models;

namespace ServerWatch.Services.Notifications;

/// <summary>Per-container notification preferences (which events should notify).</summary>
public interface IContainerNotificationPrefsService
{
    Task InitializeAsync();
    ContainerNotifEntry GetPrefs(string containerName);
    bool ShouldNotify(string containerName, string eventType);
    Task SavePrefsAsync(string containerName, ContainerNotifEntry entry);
}
