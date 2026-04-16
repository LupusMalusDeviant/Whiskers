using ServerWatch.Models;

namespace ServerWatch.Services.Notifications;

public interface INotificationService
{
    Task SendAsync(NotificationEvent evt);
    Task SendTestAsync();
}
