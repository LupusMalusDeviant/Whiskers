using Whiskers.Models;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>The handles <see cref="WebhookPrincipalTests"/> drives this harness with (contract 0014).</summary>
public sealed partial class WebhookMutationTests
{
    internal WebhookEntity SeedWebhook(string action, string target) => Seed(action, target);

    internal Task<(bool Success, string Output)> DeliverSignedAsync(string webhookId) =>
        Service().TriggerSignedTestAsync(webhookId);

    internal Task<string> RegenerateSecretAsync(string webhookId) => Service().RegenerateSecretAsync(webhookId);

    internal int StoredSecretVersion(string webhookId) => Stored(webhookId).SecretVersion;

    internal IReadOnlyList<MutationRequest> GatewayRequests => _audit.Requests;

    internal bool RanAction(string action) => Ran(action);
}
