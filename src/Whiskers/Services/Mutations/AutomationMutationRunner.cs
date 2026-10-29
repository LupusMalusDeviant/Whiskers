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
    /// <c>true</c> (the 1.0 default, R1-04): an item without an owner is refused on every run and suspended.
    /// <c>false</c>, set explicitly (<c>Automation__RequireOwner=false</c>): the grace period — an item created before
    /// owners were recorded keeps running, audited as "no owner", until an admin takes it over.
    /// </summary>
    public bool RequireOwner { get; set; } = true;
}

/// <summary>
/// Who an unattended run belongs to. <paramref name="RunnerId"/> names the item (<c>task:&lt;id&gt;</c>,
/// <c>autoupdate</c>, <c>autopatch</c>); <paramref name="OwnerId"/> is the recorded owner (an email, or
/// <c>key:&lt;id&gt;</c> for an MCP key), or <c>null</c> for an item from before owners were recorded.
/// </summary>
public sealed record AutomationActor(string RunnerId, string DisplayName, string? OwnerId, string? CreatedVia = null)
{
    /// <summary>The operation id of the run (R1-18), when the item has one of its own — a webhook delivery's run id.
    /// Otherwise the gateway assigns one per call.</summary>
    public string? OperationId { get; init; }
}

/// <summary>The owner's authority right now. Re-read on every run (INV-04).</summary>
public interface IAutomationOwnerResolver
{
    /// <summary>The owner as a principal with the level it holds now; a removed user or a deleted / disabled key
    /// comes back with <c>read</c>, so every mutation is refused.</summary>
    MutationPrincipal Resolve(string ownerId);

    /// <summary>The owner for display: its name and why it may no longer act, for the UI to localize.</summary>
    OwnerDescription Describe(string ownerId) => new(ownerId, OwnerState.Active);
}

/// <summary>Why an owner may or may not act; the UI shows it through <c>AutomationOwners_State_&lt;state&gt;</c>.</summary>
public enum OwnerState { Active, NotAllowedToSignIn, AccountDeleted, KeyDeleted, KeyDisabled, Unrecognised, Unverified }

/// <summary>An owner as the UI shows it: an email, a key name or the raw stored value.</summary>
public sealed record OwnerDescription(string Name, OwnerState State);

public static class AutomationOwners
{
    public const string KeyPrefix = PrincipalIds.KeyPrefix;

    /// <summary>The owner id to record for whoever created an item: <c>key:&lt;id&gt;</c> for an MCP key,
    /// <c>user:&lt;email&gt;</c> for a user, <c>null</c> when neither is known.</summary>
    public static string? IdOf(string? keyId, string? email) =>
        !string.IsNullOrWhiteSpace(keyId) ? PrincipalIds.Key(keyId)
        : !string.IsNullOrWhiteSpace(email) ? PrincipalIds.User(email)
        : null;

    /// <summary>The explicit migration of an owner id recorded before the <see cref="PrincipalIds"/> scheme: a bare
    /// email becomes <c>user:&lt;email&gt;</c>; an id in the scheme stays. Only an unambiguous email is migrated: one
    /// <c>@</c> with something on both sides and nothing but ASCII letters, digits and <c>. _ % + -</c> — the same rule
    /// as the <c>PrincipalIdsForTaskOwners</c> database migration, so both lower-case it alike. Anything else is
    /// returned unchanged and the resolver keeps it suspended.</summary>
    public static string? MigrateLegacy(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return stored;
        if (PrincipalIds.EmailOf(stored) is not null || PrincipalIds.KeyIdOf(stored) is not null) return stored;
        var at = stored.IndexOf('@');
        var plainEmail = at > 0 && at == stored.LastIndexOf('@') && at < stored.Length - 1
                         && stored.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '%' or '+' or '-' or '@');
        return plainEmail ? PrincipalIds.User(stored) : stored;
    }

    /// <summary>One owner's authority for the audit row of a takeover: <c>&lt;id&gt; (&lt;state&gt;, &lt;level&gt;)</c>, or
    /// <c>-</c> for none. Records what the item ran on before and what it runs on after.</summary>
    public static string AuthorityOf(IAutomationOwnerResolver resolver, string? ownerId)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (string.IsNullOrWhiteSpace(ownerId)) return "-";
        return $"{ownerId} ({resolver.Describe(ownerId).State}, {resolver.Resolve(ownerId).PermissionLevel})";
    }

    /// <summary>What to record as <see cref="AutomationActor.CreatedVia"/> for an item created by
    /// <paramref name="caller"/>: the caller's own id (for an agent, its session).</summary>
    public static string? ViaOf(MutationPrincipal? caller) =>
        caller is null || string.IsNullOrWhiteSpace(caller.Id) ? null : caller.Id;

    /// <summary>The owner to record for an item created by <paramref name="caller"/> (the gateway's principal):
    /// the user or key an agent acts for, the user or key itself otherwise. <c>null</c> for anything without a
    /// human or key behind it (an AI trigger, another automation), whose item would otherwise run unowned.</summary>
    public static string? OfCaller(MutationPrincipal? caller)
    {
        var source = caller?.OnBehalfOf ?? caller;
        return source?.Kind switch
        {
            MutationPrincipalKind.Human => PrincipalIds.EmailOf(source.Id) is not null ? source.Id : null,
            MutationPrincipalKind.ApiKey => IdOf(source.CredentialId ?? PrincipalIds.KeyIdOf(source.Id), null),
            _ => null,
        };
    }
}

public sealed class AutomationOwnerResolver : IAutomationOwnerResolver
{
    private readonly IRoleService _roles;
    private readonly IWhitelistService _whitelist;
    private readonly IMcpPermissionService _keys;

    private readonly IOwnerAccounts? _accounts;

    public AutomationOwnerResolver(IRoleService roles, IWhitelistService whitelist, IMcpPermissionService keys,
        IOwnerAccounts? accounts = null)
    {
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _whitelist = whitelist ?? throw new ArgumentNullException(nameof(whitelist));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _accounts = accounts;
    }

    /// <summary>The state of a user owner: an account that is gone outranks a whitelist entry left behind.</summary>
    private OwnerState UserState(string email) =>
        _accounts?.StateOf(email) switch
        {
            OwnerAccountState.Deleted => OwnerState.AccountDeleted,
            OwnerAccountState.Unverified => OwnerState.Unverified,
            _ => _whitelist.IsEmailAllowed(email) ? OwnerState.Active : OwnerState.NotAllowedToSignIn,
        };

    public OwnerDescription Describe(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (PrincipalIds.KeyIdOf(ownerId) is { } keyId)
        {
            var key = _keys.GetPermissionData().ApiKeys.FirstOrDefault(k => k.Id == keyId);
            return key is null ? new(ownerId, OwnerState.KeyDeleted)
                : new(key.Name, key.Enabled ? OwnerState.Active : OwnerState.KeyDisabled);
        }
        if (PrincipalIds.EmailOf(ownerId) is not { } email) return new(ownerId, OwnerState.Unrecognised);
        return new(email, UserState(email));
    }

    public MutationPrincipal Resolve(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (PrincipalIds.KeyIdOf(ownerId) is { } keyId)
        {
            var key = _keys.GetPermissionData().ApiKeys.FirstOrDefault(k => k.Id == keyId);
            var level = key is { Enabled: true } ? McpPermissionLevels.Normalize(key.PermissionLevel) : McpPermissionLevels.Read;
            return new MutationPrincipal(MutationPrincipalKind.ApiKey, ownerId,
                key is null ? $"{ownerId} (deleted)" : key.Enabled ? key.Name : $"{key.Name} (disabled)",
                level, CredentialId: keyId, AllowedTools: key?.AllowedTools);
        }

        // An id outside the scheme (a legacy value the migration could not map unambiguously) is never guessed at:
        // it stays suspended until an admin takes the item over.
        if (PrincipalIds.EmailOf(ownerId) is not { } email)
            return new MutationPrincipal(MutationPrincipalKind.Human, ownerId,
                $"{ownerId} (unrecognised owner, suspended)", McpPermissionLevels.Read);

        // A user who can no longer sign in — removed from the whitelist, or whose local account was deleted — has no
        // authority left, whatever role entry remains.
        var state = UserState(email);
        var userLevel = state != OwnerState.Active ? McpPermissionLevels.Read : _roles.GetRole(email) switch
        {
            AppRole.Admin => McpPermissionLevels.Admin,
            AppRole.Operator => McpPermissionLevels.Write,
            _ => McpPermissionLevels.Read,
        };
        return new MutationPrincipal(MutationPrincipalKind.Human, ownerId, state switch
        {
            OwnerState.AccountDeleted => $"{email} (account deleted)",
            OwnerState.NotAllowedToSignIn => $"{email} (no longer allowed to sign in)",
            OwnerState.Unverified => $"{email} (sign-in not verified yet)",
            _ => email,
        }, userLevel);
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

    /// <summary>Why the item can no longer run on its owner's authority at all — no owner while
    /// <see cref="AutomationOptions.RequireOwner"/> is set, an owner who lost their account, sign-in or key, or a
    /// level below <paramref name="capabilityId"/> — or <c>null</c> when the owner still holds it. A refusal for
    /// this reason suspends the item (PC-D6); a refusal by policy (kill switch, protected resource) does not.</summary>
    string? LostAuthority(AutomationActor actor, string capabilityId) => null;
}

public sealed class AutomationMutationRunner : IAutomationMutationRunner
{
    private readonly IMutationGateway _gateway;
    private readonly ICapabilityCatalog _catalog;
    private readonly IAutomationOwnerResolver _owners;
    private readonly IOptionsMonitor<AutomationOptions> _options;
    private readonly ICapabilityOverrides? _overrides;

    public AutomationMutationRunner(IMutationGateway gateway, ICapabilityCatalog catalog,
        IAutomationOwnerResolver owners, IOptionsMonitor<AutomationOptions> options,
        ICapabilityOverrides? overrides = null)
    {
        _overrides = overrides;
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
            target, args, OperationId: actor.OperationId);
        // A failure answered as a value (false, a failed command, (false, …)) is recorded as Failed.
        return _gateway.ExecuteAsync(request, async (_, token) => MutationResults.Report(await action(token)), ct);
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

    public string? LostAuthority(AutomationActor actor, string capabilityId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var capability = _catalog.Find(capabilityId);
        if (capability is null) return null;
        if (string.IsNullOrWhiteSpace(actor.OwnerId))
            return _options.CurrentValue.RequireOwner ? "it has no owner, and owners are required" : null;
        var owner = _owners.Resolve(actor.OwnerId);
        var state = _owners.Describe(actor.OwnerId).State;
        // Unverified is refused but not suspended: it runs again once the owner signs in (or an admin takes over).
        if (state == OwnerState.Unverified) return null;
        if (state != OwnerState.Active)
            return $"its owner {owner.DisplayName} has no authority left";
        // The same requirement the gateway enforces: a level raised in Settings → MCP counts. A switched-off
        // capability is a policy, not a lost authority, and an unreadable setting is refused there anyway.
        var required = capability.RequiredLevel;
        if (_overrides is not null)
        {
            try
            {
                var availability = _overrides.Resolve(capability);
                if (availability.Enabled) required = availability.RequiredLevel;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the catalog level; the run itself was refused fail-closed.
            }
        }
        if (!McpPermissionLevels.HasAccess(owner.PermissionLevel, required))
            return $"its owner {owner.DisplayName} no longer holds '{required}' for {capability.Id}";
        // A key owner limited to a tool list holds only those tools.
        var tool = capability.McpTool ?? capability.Id;
        return owner.AllowedTools is { } allowed && !allowed.Contains(tool)
            ? $"its owner {owner.DisplayName} may no longer use '{tool}'"
            : null;
    }

    private MutationPrincipal PrincipalOf(AutomationActor actor, Capability? capability)
    {
        if (!string.IsNullOrWhiteSpace(actor.OwnerId))
        {
            var owner = _owners.Resolve(actor.OwnerId);
            return new MutationPrincipal(MutationPrincipalKind.Automation, actor.RunnerId,
                $"{actor.DisplayName} (owner {owner.DisplayName})", owner.PermissionLevel,
                OnBehalfOf: owner, AllowedTools: owner.AllowedTools, DelegatedBy: actor.CreatedVia);
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
