using Whiskers.Models;
using Whiskers.Models.Agent;

namespace Whiskers.Services.Mutations;

/// <summary>
/// The stable identity scheme for principals (INV-01, INV-06): <c>user:&lt;email&gt;</c>, <c>key:&lt;key id&gt;</c>,
/// <c>trigger:&lt;trigger id&gt;</c>, <c>agent:&lt;session id&gt;</c>. An id is built from a principal's typed fields,
/// never from its display name, so renaming a key or a trigger keeps its identity and two triggers with the same name
/// stay apart. Anything outside the scheme is not an identity: <see cref="AutomationOwnerResolver"/> suspends it.
/// </summary>
public static class PrincipalIds
{
    public const string UserPrefix = "user:";
    public const string KeyPrefix = "key:";
    public const string TriggerPrefix = "trigger:";
    public const string AgentPrefix = "agent:";

    /// <summary>A principal with no identity (no email, no key): it can be checked, never owned or delegated.</summary>
    public const string Anonymous = "anonymous";

    /// <summary><c>user:&lt;email&gt;</c>, the email trimmed and lower-cased (sign-in compares it without case).</summary>
    public static string User(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return UserPrefix + email.Trim().ToLowerInvariant();
    }

    public static string Key(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        return KeyPrefix + keyId;
    }

    public static string Trigger(string triggerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        return TriggerPrefix + triggerId;
    }

    public static string Agent(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return AgentPrefix + sessionId;
    }

    /// <summary>The email of a <c>user:</c> id, or <c>null</c>.</summary>
    public static string? EmailOf(string? id) => Rest(id, UserPrefix);

    /// <summary>The key id of a <c>key:</c> id, or <c>null</c>.</summary>
    public static string? KeyIdOf(string? id) => Rest(id, KeyPrefix);

    /// <summary>The trigger id of a <c>trigger:</c> id, or <c>null</c>.</summary>
    public static string? TriggerIdOf(string? id) => Rest(id, TriggerPrefix);

    /// <summary>The identity of the user, key or trigger behind an agent principal, from its typed fields.</summary>
    public static string Of(AgentPrincipal p) => p.Kind switch
    {
        AgentPrincipalKind.McpKey when !string.IsNullOrWhiteSpace(p.McpKeyId) => Key(p.McpKeyId),
        AgentPrincipalKind.Trigger when !string.IsNullOrWhiteSpace(p.TriggerId) => Trigger(p.TriggerId),
        AgentPrincipalKind.WebUser when !string.IsNullOrWhiteSpace(p.UserEmail) => User(p.UserEmail),
        _ => Anonymous,
    };

    private static string? Rest(string? id, string prefix) =>
        id is not null && id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length
            ? id[prefix.Length..]
            : null;
}

/// <summary>Builds gateway principals from the agent-side principal. Every entry point that starts from an
/// <see cref="AgentPrincipal"/> goes through here, so the same caller gets the same identity on every surface.</summary>
public static class MutationPrincipals
{
    /// <summary>The user, key or trigger itself.</summary>
    public static MutationPrincipal Of(AgentPrincipal p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var id = PrincipalIds.Of(p);
        var level = McpPermissionLevels.Normalize(p.PermissionLevel);
        return p.Kind switch
        {
            AgentPrincipalKind.McpKey when PrincipalIds.KeyIdOf(id) is { } keyId =>
                new MutationPrincipal(MutationPrincipalKind.ApiKey, id, p.DisplayName, level, CredentialId: keyId,
                    AllowedTools: p.AllowedTools),
            AgentPrincipalKind.Trigger when PrincipalIds.TriggerIdOf(id) is not null =>
                new MutationPrincipal(MutationPrincipalKind.Automation, id, p.DisplayName, level,
                    AllowedTools: p.AllowedTools),
            _ => new MutationPrincipal(MutationPrincipalKind.Human, id, p.DisplayName, level,
                AllowedTools: p.AllowedTools),
        };
    }

    /// <summary>The agent session <paramref name="sessionId"/>, acting on behalf of <paramref name="p"/>: its own
    /// identity is the session, its rights and credential are those of the principal it acts for.</summary>
    public static MutationPrincipal Agent(AgentPrincipal p, string sessionId)
    {
        var source = Of(p) with { SessionId = sessionId };
        return new MutationPrincipal(MutationPrincipalKind.Agent, PrincipalIds.Agent(sessionId),
            $"Agent for {source.DisplayName}", source.PermissionLevel, source.CredentialId, OnBehalfOf: source,
            AllowedTools: source.AllowedTools, SessionId: sessionId);
    }

    /// <summary>The agent-side view of a gateway principal (for the guardrail engine): the principal whose rights
    /// bound the call, identified by its typed id.</summary>
    public static AgentPrincipal ToAgent(MutationPrincipal p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var level = McpPermissionLevels.Normalize(p.PermissionLevel);
        return p.Kind switch
        {
            MutationPrincipalKind.ApiKey => new AgentPrincipal(AgentPrincipalKind.McpKey, p.DisplayName, level,
                p.AllowedTools, McpKeyId: p.CredentialId ?? PrincipalIds.KeyIdOf(p.Id)),
            MutationPrincipalKind.Automation when PrincipalIds.TriggerIdOf(p.Id) is { } triggerId =>
                new AgentPrincipal(AgentPrincipalKind.Trigger, p.DisplayName, level, p.AllowedTools, TriggerId: triggerId),
            MutationPrincipalKind.Automation => new AgentPrincipal(AgentPrincipalKind.McpKey, p.DisplayName, level,
                p.AllowedTools, McpKeyId: p.CredentialId ?? p.Id),
            _ => new AgentPrincipal(AgentPrincipalKind.WebUser, p.DisplayName, level, p.AllowedTools,
                UserEmail: PrincipalIds.EmailOf(p.Id)),
        };
    }
}
