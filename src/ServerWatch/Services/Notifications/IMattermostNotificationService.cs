namespace ServerWatch.Services.Notifications;

/// <summary>Mattermost notification channel (distinct from the Matrix channel so the composite
/// can address each independently).</summary>
public interface IMattermostNotificationService : INotificationService
{
}
