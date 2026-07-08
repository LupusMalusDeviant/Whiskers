namespace Whiskers.Services.Notifications;

/// <summary>Matrix notification channel (distinct from the Mattermost channel so the composite
/// can address each independently).</summary>
public interface IMatrixNotificationService : INotificationService
{
}
