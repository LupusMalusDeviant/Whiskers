using Whiskers.Models;

namespace Whiskers.Services.Notifications;

public interface INotificationService
{
    Task SendAsync(NotificationEvent evt);

    /// <summary>
    /// R1-41: <see cref="SendAsync(NotificationEvent)"/> for a producer that can be stopped (a background loop when the host
    /// stops). <paramref name="ct"/> ends the delivery attempts and their retries and is awaited, not abandoned: when this
    /// returns or throws, nothing of the send runs on. A cancelled send throws an <see cref="OperationCanceledException"/>;
    /// it is not a failed delivery. The default implementation only stops waiting; <see cref="CompositeNotificationService"/>
    /// and every <see cref="INotificationChannel"/> cancel the delivery itself.
    /// </summary>
    Task SendAsync(NotificationEvent evt, CancellationToken ct) => SendAsync(evt).WaitAsync(ct);

    Task SendTestAsync();
}
