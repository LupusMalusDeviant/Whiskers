using Whiskers.Models;

namespace Whiskers.Services.Webhooks;

/// <summary>The Core's default <see cref="IWebhookService"/> for when the Webhooks module is off. The inbound
/// webhook endpoint (<c>POST /api/webhooks/{id}</c>) lives in Program.cs and can't move into a module's
/// <c>ConfigureServices</c>, so it stays in Core and resolves <see cref="IWebhookService"/> per request —
/// this default keeps that resolution working when the module is disabled. The real
/// <see cref="WebhookService"/> is registered afterwards and wins (last registration). Soft-dependency-via-
/// no-op-Core-contract pattern (RoadToSAP §2.1).
///
/// <see cref="AcceptAsync"/> rejects every delivery (the endpoint then answers 400, not 500), reads
/// return empty, and <see cref="CreateWebhookAsync"/> throws rather than pretend a webhook was created.</summary>
public sealed class NoopWebhookService : IWebhookService
{
    private const string Disabled =
        "The Webhooks module is disabled (set Features:webhooks:Enabled=true to enable webhooks).";

    public Task<List<WebhookEntity>> GetWebhooksAsync() => Task.FromResult(new List<WebhookEntity>());

    public Task<WebhookEntity?> GetWebhookAsync(string webhookId) => Task.FromResult<WebhookEntity?>(null);

    public Task<bool> SetOwnerAsync(string webhookId, string ownerId, string? expectedOwnerId)
        => throw new InvalidOperationException(Disabled);

    public Task<WebhookEntity> CreateWebhookAsync(WebhookEntity webhook)
        => throw new InvalidOperationException(Disabled);

    public Task DeleteWebhookAsync(string webhookId) => Task.CompletedTask;

    public Task<string> RegenerateSecretAsync(string webhookId)
        => throw new InvalidOperationException(Disabled);

    public Task SetEnabledAsync(string webhookId, bool enabled)
        => throw new InvalidOperationException(Disabled);

    public Task<WebhookAcceptance> AcceptAsync(string webhookId, WebhookRequest request, CancellationToken ct = default)
        => Task.FromResult(new WebhookAcceptance(WebhookReply.Rejected));

    public WebhookReply? Admit(string webhookId) => null;

    public Task<(bool Success, string Output)> TriggerSignedTestAsync(string webhookId)
        => Task.FromResult((false, "Webhooks are disabled."));
}
