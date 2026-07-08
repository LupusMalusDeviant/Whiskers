using Whiskers.Models;

namespace Whiskers.Services.Notifications;

public interface INotificationService
{
    Task SendAsync(NotificationEvent evt);
    Task SendTestAsync();
}
