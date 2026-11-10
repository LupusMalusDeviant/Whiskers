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

    // Contract 0014 (D1): the ids the automation runners always used, kept byte for byte and registered here.
    public const string TaskPrefix = "task:";
    public const string CheckPrefix = "check:";
    public const string GitDeployPrefix = "gitdeploy:";
    public const string WebhookPrefix = "webhook:";
    /// <summary>An automation item without an owner: <c>unowned:&lt;runner id&gt;</c>.</summary>
    public const string UnownedPrefix = "unowned:";
    /// <summary>Contract 0014 (D6): a component of Whiskers itself, <c>system:&lt;component&gt;</c>.</summary>
    public const string SystemPrefix = "system:";

    public const string AutoUpdate = "autoupdate";
    public const string AutoPatch = "autopatch";
    public const string AutoPatchPreview = "autopatch-preview";
    public const string VpnBootstrap = "vpn-bootstrap";

    /// <summary>The automations that exist once per instance; their id is the runner's name.</summary>
    public static readonly IReadOnlySet<string> AutomationSingletons =
        global::System.Collections.Frozen.FrozenSet.ToFrozenSet([AutoUpdate, AutoPatch, AutoPatchPreview, VpnBootstrap], StringComparer.Ordinal);

    /// <summary>The <c>host-cli:</c> component prefix: <c>system:host-cli:&lt;command&gt;</c> (contract 0014, Q6).</summary>
    public const string HostCliComponent = "host-cli:";

    /// <summary>Contract 0014 (D6): the closed list of system components. A new one needs a contract change; the
    /// host CLIs are <c>host-cli:&lt;command&gt;</c> with the commands in <see cref="HostCliCommands"/>.</summary>
    public static readonly IReadOnlySet<string> SystemComponents = global::System.Collections.Frozen.FrozenSet.ToFrozenSet(
        ["audit-retention", "startup", "approval-expiry", "authority-check"], StringComparer.Ordinal);

    /// <summary>The host CLIs (S-10, S-11, S-15), named by their flag without the dashes.</summary>
    public static readonly IReadOnlySet<string> HostCliCommands = global::System.Collections.Frozen.FrozenSet.ToFrozenSet(
        ["restore-backup", "recover-local-account", "confirm-identity-binding"], StringComparer.Ordinal);

    public static string Task(string taskId) => Prefixed(TaskPrefix, taskId);
    public static string Check(string checkId) => Prefixed(CheckPrefix, checkId);
    public static string GitDeploy(string appId) => Prefixed(GitDeployPrefix, appId);
    public static string Webhook(string webhookId) => Prefixed(WebhookPrefix, webhookId);

    /// <summary><c>unowned:&lt;runner id&gt;</c>: the id an automation item without an owner is recorded under.</summary>
    public static string Unowned(string runnerId) => Prefixed(UnownedPrefix, runnerId);

    /// <summary><c>system:&lt;component&gt;</c>; throws for a component outside <see cref="SystemComponents"/> and
    /// <see cref="HostCliCommands"/>.</summary>
    public static string System(string component)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        if (!IsSystemComponent(component))
            throw new ArgumentException($"'{component}' is not a registered system component (contract 0014, D6).",
                nameof(component));
        return SystemPrefix + component;
    }

    /// <summary><c>system:host-cli:&lt;command&gt;</c> for a host CLI.</summary>
    public static string HostCli(string command) => System(HostCliComponent + command);

    private static bool IsSystemComponent(string component) =>
        SystemComponents.Contains(component)
        || (component.StartsWith(HostCliComponent, StringComparison.Ordinal)
            && HostCliCommands.Contains(component[HostCliComponent.Length..]));

    /// <summary>The kind an id of the scheme belongs to, or <c>null</c> for an id outside it. A webhook's id is
    /// <see cref="MutationPrincipalKind.Webhook"/> also when it runs without an owner.</summary>
    public static MutationPrincipalKind? KindOf(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id == Anonymous) return MutationPrincipalKind.Human;
        if (EmailOf(id) is not null) return MutationPrincipalKind.Human;
        if (KeyIdOf(id) is not null) return MutationPrincipalKind.ApiKey;
        if (Rest(id, AgentPrefix) is not null) return MutationPrincipalKind.Agent;
        if (Rest(id, WebhookPrefix) is not null) return MutationPrincipalKind.Webhook;
        if (Rest(id, SystemPrefix) is { } component) return IsSystemComponent(component) ? MutationPrincipalKind.System : null;
        if (Rest(id, UnownedPrefix) is { } runner)
            return KindOf(runner) is MutationPrincipalKind.Webhook ? MutationPrincipalKind.Webhook
                : KindOf(runner) is MutationPrincipalKind.Automation ? MutationPrincipalKind.Automation : null;
        if (Rest(id, TaskPrefix) is not null || Rest(id, CheckPrefix) is not null || Rest(id, GitDeployPrefix) is not null
            || Rest(id, TriggerPrefix) is not null || AutomationSingletons.Contains(id))
            return MutationPrincipalKind.Automation;
        return null;
    }

    private static string Prefixed(string prefix, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return prefix + id;
    }

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
            // Contract 0014 (D3): a webhook delivery is seen by the guardrails as the automation it used to be; its
            // id, not its secret generation, stands in for the key.
            MutationPrincipalKind.Webhook => new AgentPrincipal(AgentPrincipalKind.McpKey, p.DisplayName, level,
                p.AllowedTools, McpKeyId: p.Id),
            _ => new AgentPrincipal(AgentPrincipalKind.WebUser, p.DisplayName, level, p.AllowedTools,
                UserEmail: PrincipalIds.EmailOf(p.Id)),
        };
    }
}
