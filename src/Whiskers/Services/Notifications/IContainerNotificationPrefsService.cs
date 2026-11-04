using Whiskers.Models;

namespace Whiskers.Services.Notifications;

/// <summary>Per-container notification preferences (which events should notify).</summary>
public interface IContainerNotificationPrefsService
{
    Task InitializeAsync(CancellationToken ct = default);
    ContainerNotifEntry GetPrefs(string containerName);
    bool ShouldNotify(string containerName, string eventType);
    [Whiskers.Services.Mutations.MutatingOperation("config.save")]
    Task SavePrefsAsync(string containerName, ContainerNotifEntry entry);
}
