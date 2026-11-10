namespace Whiskers.Services.Mutations;

/// <summary>
/// Contract 0014: the ids of the credentials that authenticate a principal (<see cref="MutationPrincipal.CredentialId"/>).
/// Each names a credential — a binding, a local account, a webhook secret's generation — never a value derived from a
/// secret. An MCP key's credential id stays its key id.
/// </summary>
public static class PrincipalCredentials
{
    public const string BindingPrefix = "binding:";
    public const string LocalAccountPrefix = "local:";
    public const string WebhookSecretPrefix = "webhook-secret:";

    /// <summary>A person signed in with <c>Auth:Disabled</c>: no credential was checked.</summary>
    public const string AuthDisabled = "auth-disabled";

    /// <summary>A federated sign-in: <c>binding:&lt;binding id&gt;</c> (contract 0003).</summary>
    public static string Binding(string bindingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);
        return BindingPrefix + bindingId;
    }

    /// <summary>A local password sign-in: <c>local:&lt;Identity user id&gt;</c>.</summary>
    public static string LocalAccount(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return LocalAccountPrefix + userId;
    }

    /// <summary>The webhook secret generation a delivery's HMAC was checked against:
    /// <c>webhook-secret:&lt;webhook id&gt;@&lt;version&gt;</c>.</summary>
    public static string WebhookSecret(string webhookId, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookId);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        return $"{WebhookSecretPrefix}{webhookId}@{version}";
    }
}
