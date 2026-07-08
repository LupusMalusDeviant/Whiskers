using Whiskers.Models;

namespace Whiskers.Services.Webhooks;

/// <summary>Manages CI/CD webhooks and processes incoming webhook triggers.</summary>
public interface IWebhookService
{
    Task<List<WebhookEntity>> GetWebhooksAsync();
    Task<WebhookEntity> CreateWebhookAsync(WebhookEntity webhook);
    Task DeleteWebhookAsync(string webhookId);
    Task<(bool Success, string Output)> TriggerAsync(string webhookId, string? signature = null, string? body = null, string? sourceIp = null);
}
