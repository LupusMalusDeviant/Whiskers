using Whiskers.Models;

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

    /// <summary>R1-54: where the channel connects while it is enabled and configured — scheme, host and port only, for
    /// the outbound guard's listing; empty otherwise.</summary>
    IReadOnlyList<OutboundEndpoint> Targets => [];

    /// <summary>
    /// Delivers one event (R1-40). Returns <c>true</c> when it was sent, <c>false</c> when there was nothing to send
    /// (the channel is off or not configured, or the event is throttled). A failed delivery <b>throws</b> — the caller
    /// retries, keeps a critical event for redelivery and reports what was exhausted; a channel never swallows its own
    /// failure. <paramref name="ct"/> carries the caller's deadline and reaches the network call.
    /// </summary>
    Task<bool> DeliverAsync(NotificationEvent evt, CancellationToken ct);

    /// <summary>One delivery without a deadline of the caller's; it throws on failure like <see cref="DeliverAsync"/>.
    /// The composite never uses it.</summary>
    Task INotificationService.SendAsync(NotificationEvent evt) => DeliverAsync(evt, CancellationToken.None);

    /// <summary>One delivery whose cancellation reaches the network call (R1-41).</summary>
    Task INotificationService.SendAsync(NotificationEvent evt, CancellationToken ct) => DeliverAsync(evt, ct);
}
