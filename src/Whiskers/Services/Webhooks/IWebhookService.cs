using Whiskers.Models;

namespace Whiskers.Services.Webhooks;

/// <summary>Manages CI/CD webhooks and processes incoming webhook triggers.
///
/// Secrets are mandatory (HOCH-12 part 2): <see cref="CreateWebhookAsync"/> generates a strong HMAC
/// secret when none is supplied, and <see cref="TriggerAsync"/> rejects webhooks without one
/// fail-closed. The secret is returned exactly once (on create / regenerate) and never displayed
/// again — callers show it one-time and discard it.</summary>
public interface IWebhookService
{
    Task<List<WebhookEntity>> GetWebhooksAsync();

    /// <summary>Creates the webhook. An empty <see cref="WebhookEntity.Secret"/> is replaced with a
    /// freshly generated 256-bit secret; the returned entity carries it for one-time display.</summary>
    Task<WebhookEntity> CreateWebhookAsync(WebhookEntity webhook);

    Task DeleteWebhookAsync(string webhookId);

    /// <summary>Replaces the webhook's HMAC secret with a freshly generated one and returns it
    /// (one-time display — it is not retrievable afterwards).</summary>
    Task<string> RegenerateSecretAsync(string webhookId);

    /// <summary>Enables/disables a webhook. Enabling a webhook without a secret is rejected
    /// (legacy pre-F11 rows) — regenerate its secret first.</summary>
    Task SetEnabledAsync(string webhookId, bool enabled);

    /// <summary>Processes an incoming trigger. Requires a valid <c>X-Hub-Signature-256</c>-style HMAC
    /// signature over the raw body; webhooks without a secret are rejected fail-closed.</summary>
    Task<(bool Success, string Output)> TriggerAsync(string webhookId, string? signature = null, string? body = null, string? sourceIp = null);

    /// <summary>Fires a genuine end-to-end test trigger: builds a test payload, signs it with the
    /// stored secret and runs it through the same validation path as an external caller.</summary>
    Task<(bool Success, string Output)> TriggerSignedTestAsync(string webhookId);
}
