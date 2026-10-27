using Microsoft.Extensions.Options;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Mcp;

namespace Whiskers.Services.Mutations;

/// <summary>Settings for unattended automation (PC-D6).</summary>
public sealed class AutomationOptions
{
    public const string Section = "Automation";

    /// <summary>
    /// <c>false</c> (default until 1.0): an item created before owners were recorded keeps running, audited as
    /// "no owner", until an admin takes it over. <c>true</c>: an item without an owner is refused on every run.
    /// </summary>
    public bool RequireOwner { get; set; }
}

/// <summary>
/// Who an unattended run belongs to. <paramref name="RunnerId"/> names the item (<c>task:&lt;id&gt;</c>,
/// <c>autoupdate</c>, <c>autopatch</c>); <paramref name="OwnerId"/> is the recorded owner (an email, or
/// <c>key:&lt;id&gt;</c> for an MCP key), or <c>null</c> for an item from before owners were recorded.
/// </summary>
public sealed record AutomationActor(string RunnerId, string DisplayName, string? OwnerId);

/// <summary>The owner's authority right now. Re-read on every run (INV-04).</summary>
public interface IAutomationOwnerResolver
{
    /// <summary>The owner as a principal with the level it holds now; a removed user or a deleted / disabled key
    /// comes back with <c>read</c>, so every mutation is refused.</summary>
    MutationPrincipal Resolve(string ownerId);
}

public static class AutomationOwners
{
    public const string KeyPrefix = "key:";

    /// <summary>The owner id to record for whoever created an item: <c>key:&lt;id&gt;</c> for an MCP key, the email
    /// for a user, <c>null</c> when neither is known.</summary>
    public static string? IdOf(string? keyId, string? email) =>
        !string.IsNullOrWhiteSpace(keyId) ? KeyPrefix + keyId
        : !string.IsNullOrWhiteSpace(email) ? email
        : null;

    /// <summary>The owner to record for an item created by <paramref name="caller"/> (the gateway's principal):
    /// the user or key an agent acts for, the user or key itself otherwise. <c>null</c> for anything without a
    /// human or key behind it (an AI trigger, another automation), whose item would otherwise run unowned.</summary>
    public static string? OfCaller(MutationPrincipal? caller)
    {
        var source = caller?.OnBehalfOf ?? caller;
        return source?.Kind switch
        {
            MutationPrincipalKind.Human => string.IsNullOrWhiteSpace(source.Id) ? null : source.Id,
            MutationPrincipalKind.ApiKey => IdOf(source.CredentialId, null),
            _ => null,
        };
    }
}

public sealed class AutomationOwnerResolver : IAutomationOwnerResolver
{
    private readonly IRoleService _roles;
    private readonly IWhitelistService _whitelist;
    private readonly IMcpPermissionService _keys;

    public AutomationOwnerResolver(IRoleService roles, IWhitelistService whitelist, IMcpPermissionService keys)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _whitelist = whitelist ?? throw new ArgumentNullException(nameof(whitelist));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    public MutationPrincipal Resolve(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (ownerId.StartsWith(AutomationOwners.KeyPrefix, StringComparison.Ordinal))
        {
            var keyId = ownerId[AutomationOwners.KeyPrefix.Length..];
            var key = _keys.GetPermissionData().ApiKeys.FirstOrDefault(k => k.Id == keyId);
            var level = key is { Enabled: true } ? McpPermissionLevels.Normalize(key.PermissionLevel) : McpPermissionLevels.Read;
            return new MutationPrincipal(MutationPrincipalKind.ApiKey, ownerId,
                key is null ? $"{ownerId} (deleted)" : key.Enabled ? key.Name : $"{key.Name} (disabled)",
                level, CredentialId: keyId, AllowedTools: key?.AllowedTools);
        }

        // A user who can no longer sign in has no authority left, whatever role entry remains.
        var allowed = _whitelist.IsEmailAllowed(ownerId);
        var userLevel = !allowed ? McpPermissionLevels.Read : _roles.GetRole(ownerId) switch
        {
            AppRole.Admin => McpPermissionLevels.Admin,
            AppRole.Operator => McpPermissionLevels.Write,
            _ => McpPermissionLevels.Read,
        };
        return new MutationPrincipal(MutationPrincipalKind.Human, ownerId,
            allowed ? ownerId : $"{ownerId} (no longer allowed to sign in)", userLevel);
    }
}

/// <summary>
/// The entry to the Mutation Gateway for unattended runs (ADR-0005 step 2.3, PC-D6): scheduled tasks, AutoUpdate,
/// AutoPatch. Every call re-reads the owner's authority and runs as an <see cref="MutationPrincipalKind.Automation"/>
/// principal acting <c>OnBehalfOf</c> the owner, so the owner's level is the ceiling.
/// <para>As with <see cref="IUiMutationRunner"/>, the capability id is a string literal and the action a lambda
/// written in the call; the architecture test recognises exactly that shape.</para>
/// </summary>
public interface IAutomationMutationRunner
{
    [MutationEntryPoint]
    Task<MutationOutcome<T>> RunAsync<T>(AutomationActor actor, string capabilityId, object arguments,
        Func<CancellationToken, Task<T>> action, CancellationToken ct = default);

    [MutationEntryPoint]
    Task<MutationOutcome<bool>> RunAsync(AutomationActor actor, string capabilityId, object arguments,
        Func<CancellationToken, Task> action, CancellationToken ct = default);
}

public sealed class AutomationMutationRunner : IAutomationMutationRunner
{
    private readonly IMutationGateway _gateway;
    private readonly ICapabilityCatalog _catalog;
    private readonly IAutomationOwnerResolver _owners;
    private readonly IOptionsMonitor<AutomationOptions> _options;

    public AutomationMutationRunner(IMutationGateway gateway, ICapabilityCatalog catalog,
        IAutomationOwnerResolver owners, IOptionsMonitor<AutomationOptions> options)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<MutationOutcome<T>> RunAsync<T>(AutomationActor actor, string capabilityId, object arguments,
        Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(action);

        var args = UiMutationRunner.ToArguments(arguments);
        var capability = _catalog.Find(capabilityId);
        var target = capability is null
            ? new MutationTarget("unknown", "")
            : MutationTargets.FromArguments(capability, args);
        var request = new MutationRequest(capabilityId, PrincipalOf(actor, capability), MutationSurface.Automation,
            target, args);
        return _gateway.ExecuteAsync(request, (_, token) => action(token), ct);
    }

    public Task<MutationOutcome<bool>> RunAsync(AutomationActor actor, string capabilityId, object arguments,
        Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync<bool>(actor, capabilityId, arguments, async token =>
        {
            await action(token);
            return true;
        }, ct);
    }

    private MutationPrincipal PrincipalOf(AutomationActor actor, Capability? capability)
    {
        if (!string.IsNullOrWhiteSpace(actor.OwnerId))
        {
            var owner = _owners.Resolve(actor.OwnerId);
            return new MutationPrincipal(MutationPrincipalKind.Automation, actor.RunnerId,
                $"{actor.DisplayName} (owner {owner.DisplayName})", owner.PermissionLevel,
                OnBehalfOf: owner, AllowedTools: owner.AllowedTools);
        }

        // An item from before owners were recorded. During the grace period it keeps the authority it always had,
        // for exactly the capability it runs — no more — and the audit row says it has no owner.
        var level = !_options.CurrentValue.RequireOwner && capability is not null
            ? capability.RequiredLevel
            : McpPermissionLevels.Read;
        return new MutationPrincipal(MutationPrincipalKind.Automation, "unowned:" + actor.RunnerId,
            $"{actor.DisplayName} (no owner)", level);
    }
}
