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

    /// <summary>One stored webhook, or <c>null</c>.</summary>
    Task<WebhookEntity?> GetWebhookAsync(string webhookId);

    /// <summary>Creates the webhook. An empty <see cref="WebhookEntity.Secret"/> is replaced with a
    /// freshly generated 256-bit secret; the returned entity carries it for one-time display.
    /// <see cref="WebhookEntity.Owner"/> is the principal id an external trigger runs on (R1-09): the creator.</summary>
    Task<WebhookEntity> CreateWebhookAsync(WebhookEntity webhook);

    /// <summary>Takes the webhook over (R1-09): sets <paramref name="ownerId"/> as its owner, only while its owner is
    /// still <paramref name="expectedOwnerId"/>. <c>false</c> when it is gone or was taken over meanwhile.</summary>
    Task<bool> SetOwnerAsync(string webhookId, string ownerId, string? expectedOwnerId);

    Task DeleteWebhookAsync(string webhookId);

    /// <summary>Replaces the webhook's HMAC secret with a freshly generated one and returns it
    /// (one-time display — it is not retrievable afterwards).</summary>
    Task<string> RegenerateSecretAsync(string webhookId);

    /// <summary>Enables/disables a webhook. Enabling a webhook without a secret is rejected
    /// (legacy pre-F11 rows) — regenerate its secret first.</summary>
    Task SetEnabledAsync(string webhookId, bool enabled);

    /// <summary>Processes an incoming trigger. Requires a valid <c>X-Hub-Signature-256</c>-style HMAC
    /// signature over the raw body; webhooks without a secret are rejected fail-closed. The action then runs through
    /// the Mutation Gateway on the webhook owner's authority, re-checked now (R1-09): a webhook without an owner is
    /// refused, and one whose owner lost the authority is switched off and an admin is notified.</summary>
    Task<(bool Success, string Output)> TriggerAsync(string webhookId, string? signature = null, string? body = null, string? sourceIp = null);

    /// <summary>Fires a genuine end-to-end test trigger: builds a test payload, signs it with the
    /// stored secret and runs it through the same validation path as an external caller.</summary>
    Task<(bool Success, string Output)> TriggerSignedTestAsync(string webhookId);
}
