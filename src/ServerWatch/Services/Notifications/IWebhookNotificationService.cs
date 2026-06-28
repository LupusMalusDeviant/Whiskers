namespace ServerWatch.Services.Notifications;

/// <summary>Generic outbound webhook notification channel (POSTs a JSON event). Distinct from the
/// inbound Webhooks feature in Services/Webhooks.</summary>
public interface IWebhookNotificationService : INotificationService
{
}
