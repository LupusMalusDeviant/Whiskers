namespace Whiskers.Services.Notifications;

/// <summary>A single outbound notification channel (Mattermost, Matrix, Telegram, …). All channels share
/// this shape so <see cref="CompositeNotificationService"/> can fan out over <c>IEnumerable&lt;INotificationChannel&gt;</c>
/// instead of hard-wiring each one (changeme C9 / RoadToSAP Phase 1). Each channel still does its own
/// enabled/disabled check internally.</summary>
public interface INotificationChannel : INotificationService
{
    /// <summary>Human-readable channel name for logging + the test report; defaults to the type name minus
    /// "NotificationService" (e.g. "Mattermost"). Never a secret. A channel may override it.</summary>
    string Name => GetType().Name.Replace("NotificationService", "");
}
