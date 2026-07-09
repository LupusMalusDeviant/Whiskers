using Whiskers.Models;

namespace Whiskers.Services.Notifications;

/// <summary>The Core's default <see cref="INotificationService"/>: does nothing. Registered before the module
/// pipeline so that consumers (CVE, Health, ImageUpdate, AutoUpdate, Metrics, LogMonitor, AI triggers,
/// approvals) always resolve an INotificationService even when the Notifications module is disabled. When the
/// module is enabled it registers <see cref="CompositeNotificationService"/> afterwards, which wins
/// (last registration). This is the "soft dependency via a no-op Core contract" pattern (RoadToSAP §2.1).</summary>
public sealed class NoopNotificationService : INotificationService
{
    public Task SendAsync(NotificationEvent evt) => Task.CompletedTask;
    public Task SendTestAsync() => Task.CompletedTask;
}
