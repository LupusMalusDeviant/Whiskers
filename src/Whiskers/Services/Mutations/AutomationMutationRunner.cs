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
/// Who an unattended run belongs to. <see cref="RunnerId"/> names the item (<c>task:&lt;id&gt;</c>,
/// <c>autoupdate</c>, <c>autopatch</c>); <see cref="OwnerId"/> is the recorded owner (<c>user:&lt;email&gt;</c>, or
/// <c>key:&lt;id&gt;</c> for an MCP key), or <c>null</c> for an item from before owners were recorded.
/// <para>Contract 0014 (D1): built only by the named factories, so no runner id outside <see cref="PrincipalIds"/> can
/// be written; the ids are the ones the runners always used, byte for byte.</para>
/// </summary>
public sealed record AutomationActor
{
    internal AutomationActor(string RunnerId, string DisplayName, string? OwnerId, string? CreatedVia = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RunnerId);
        this.RunnerId = RunnerId;
        this.DisplayName = DisplayName ?? throw new ArgumentNullException(nameof(DisplayName));
        this.OwnerId = OwnerId;
        this.CreatedVia = CreatedVia;
    }

    public string RunnerId { get; init; }
    public string DisplayName { get; init; }
    public string? OwnerId { get; init; }
    public string? CreatedVia { get; init; }

    /// <summary>Contract 0014 (D3): <see cref="MutationPrincipalKind.Automation"/>, or
    /// <see cref="MutationPrincipalKind.Webhook"/> for a webhook delivery (set only by the webhook factories). A
    /// webhook runs on the <see cref="MutationSurface.Webhook"/> surface and decides as an automation
    /// (<see cref="Unattended"/>).</summary>
    public MutationPrincipalKind Kind { get; init; } = MutationPrincipalKind.Automation;

    /// <summary>For a webhook delivery: the secret generation its signature was checked against
    /// (<see cref="PrincipalCredentials.WebhookSecret"/>). Null for every other run: no credential authenticates an
    /// unattended run.</summary>
    public string? CredentialId { get; init; }

    /// <summary>The session the run belongs to (contract 0014): a webhook delivery's run id, also on its nested decisions,
    /// which have operation ids of their own. Falls back to <see cref="OperationId"/>.</summary>
    public string? SessionId { get; init; }

    /// <summary>A scheduled task, owned and set up by whoever created it.</summary>
    public static AutomationActor ForTask(string taskId, string name, string? ownerId, string? createdVia) =>
        new(PrincipalIds.Task(taskId), $"Scheduled task '{name}'", ownerId, createdVia);

    /// <summary>An endpoint check; only an Admin creates or takes it over in the UI, so its owner is its provenance.</summary>
    public static AutomationActor ForCheck(string checkId, string name, string? ownerId) =>
        new(PrincipalIds.Check(checkId), $"Endpoint check '{name}'", ownerId, ownerId);

    /// <summary>A Git deployment's unattended runs; its owner is its provenance.</summary>
    public static AutomationActor ForGitDeploy(string appId, string name, string? ownerId) =>
        new(PrincipalIds.GitDeploy(appId), $"Git deployment '{name}'", ownerId, ownerId);

    /// <summary>The nightly AutoUpdate run.</summary>
    public static AutomationActor ForAutoUpdate(string? ownerId) =>
        new(PrincipalIds.AutoUpdate, "AutoUpdate", ownerId, ownerId);

    /// <summary>The nightly AutoPatch run.</summary>
    public static AutomationActor ForAutoPatch(string? ownerId) =>
        new(PrincipalIds.AutoPatch, "AutoPatch", ownerId, ownerId);

    /// <summary>A preview of AutoPatch, run for the person or key that asked for it.</summary>
    public static AutomationActor ForAutoPatchPreview(string? onBehalfOf) =>
        new(PrincipalIds.AutoPatchPreview, "AutoPatch preview", onBehalfOf);

    /// <summary>A webhook delivery on its owner; the owner is its provenance. <paramref name="secretVersion"/> is the
    /// generation of the secret the delivery was signed with (contract 0014, D4).</summary>
    public static AutomationActor ForWebhook(string webhookId, string name, string? ownerId, int secretVersion) =>
        new(PrincipalIds.Webhook(webhookId), $"Webhook '{name}'", ownerId, ownerId)
        {
            Kind = MutationPrincipalKind.Webhook,
            CredentialId = PrincipalCredentials.WebhookSecret(webhookId, secretVersion),
        };

    /// <summary>A delivery for a webhook that was removed meanwhile: no owner, nothing runs.</summary>
    public static AutomationActor ForRemovedWebhook(string webhookId) =>
        new(PrincipalIds.Webhook(webhookId), $"Webhook {webhookId} (removed)", null) { Kind = MutationPrincipalKind.Webhook };

    /// <summary>The VPN bring-up at start-up.</summary>
    public static AutomationActor ForVpnBootstrap(string displayName, string? ownerId) =>
        new(PrincipalIds.VpnBootstrap, displayName, ownerId, ownerId);
    /// <summary>The operation id of the run (R1-18), when the item has one of its own — a webhook delivery's run id.
    /// Otherwise the gateway assigns one per call.</summary>
    public string? OperationId { get; init; }

    /// <summary>The key of the one event a refusal of this run may be (<see cref="MutationRequest.EventKey"/>): a webhook
    /// delivery that did not run is recorded under it from every path that can record it (review round 5).</summary>
    public string? EventKey { get; init; }

    /// <summary>Contract 0012 (D7, A7): what the run read its target as before it took its lock (a webhook delivery).
    /// Passed as <see cref="MutationRequest.ExpectedTarget"/>: a resolution that differs is refused before policy as
    /// <see cref="MutationReasonCodes.TargetChanged"/>. Null: no expectation.</summary>
    public MutationTarget? ExpectedTarget { get; init; }
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

    /// <summary>
    /// The owner of something an API key provisions to run later without anyone at the keyboard (contract 0001, a Git
    /// deployment and its webhook): a person, never the key — the person whose approval the operation used, or the
    /// caller when the caller is a person. A key's unattended runs are decided under the key's agent rules
    /// (confirmation, tool list), which a deployment fired by a push can never satisfy. <c>null</c> when no person
    /// stands behind the call: the caller then refuses.
    /// </summary>
    public static string? PersonFor(MutationScope? scope)
    {
        if (scope is null) return null;
        if (!string.IsNullOrWhiteSpace(scope.ApprovedBy)) return scope.ApprovedBy;
        var caller = OfCaller(scope.Principal);
        return PrincipalIds.EmailOf(caller) is not null ? caller : null;
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
        var userLevel = state != OwnerState.Active ? McpPermissionLevels.Read : CapabilityCatalog.LevelFor(_roles.GetRole(email));
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
    /// <summary>Contract 0012 (D3): runs <paramref name="action"/> on the object the gateway resolved and confirmed. A
    /// <typeparamref name="TTarget"/> that is not the capability's <see cref="Capability.TargetType"/> (or a base of
    /// it) is refused — and recorded — before the gateway decides anything.</summary>
    [MutationEntryPoint]
    Task<MutationOutcome<T>> RunAsync<TTarget, T>(AutomationActor actor, string capabilityId, object arguments,
        Func<TTarget, CancellationToken, Task<T>> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
        // A runner that cannot obtain a resolved object (a test double) refuses: only the resolver makes one (D4 a).
        throw new NotSupportedException($"{GetType().Name} cannot run on a resolved target.");

    /// <inheritdoc cref="RunAsync{TTarget, T}"/>
    [MutationEntryPoint]
    Task<MutationOutcome<bool>> RunAsync<TTarget>(AutomationActor actor, string capabilityId, object arguments,
        Func<TTarget, CancellationToken, Task> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
        throw new NotSupportedException($"{GetType().Name} cannot run on a resolved target.");

    /// <summary>Why the item can no longer run on its owner's authority at all — no owner while
    /// <see cref="AutomationOptions.RequireOwner"/> is set, an owner who lost their account, sign-in or key, or a
    /// level below <paramref name="capabilityId"/> — or <c>null</c> when the owner still holds it. A refusal for
    /// this reason suspends the item (PC-D6); a refusal by policy (kill switch, protected resource) does not.</summary>
    string? LostAuthority(AutomationActor actor, string capabilityId) => null;

    /// <summary>Records a run the item refused itself before asking the gateway — no owner, a blocked owner file, a
    /// setting that cannot run (R1-43) — as the gateway's Denied event for <paramref name="capabilityId"/>, so the
    /// attempt is on record exactly once. Nothing runs. An item without an owner is recorded with <c>read</c>,
    /// never with the grace period's level.</summary>
    Task<MutationOutcome<object?>> RefuseAsync(AutomationActor actor, string capabilityId, object arguments,
        string reason, CancellationToken ct = default) =>
        Task.FromResult(new MutationOutcome<object?>(actor.OperationId ?? "", MutationStatus.Denied, null, reason,
            VerificationStatus.NotRun));
}

public sealed class AutomationMutationRunner : IAutomationMutationRunner
{
    private readonly IMutationGateway _gateway;
    private readonly ICapabilityCatalog _catalog;
    private readonly IAutomationOwnerResolver _owners;
    private readonly IOptionsMonitor<AutomationOptions> _options;
    private readonly ICapabilityOverrides? _overrides;
    private readonly ILogger<AutomationMutationRunner> _logger;

    public AutomationMutationRunner(IMutationGateway gateway, ICapabilityCatalog catalog,
        IAutomationOwnerResolver owners, IOptionsMonitor<AutomationOptions> options,
        ICapabilityOverrides? overrides = null, ILogger<AutomationMutationRunner>? logger = null)
    {
        _overrides = overrides;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AutomationMutationRunner>.Instance;
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<MutationOutcome<T>> RunAsync<TTarget, T>(AutomationActor actor, string capabilityId, object arguments,
        Func<TTarget, CancellationToken, Task<T>> action, CancellationToken ct = default) where TTarget : ResolvedTarget
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunCoreAsync<T>(actor, capabilityId, arguments, typeof(TTarget),
            (context, token) => action(UiMutationRunner.ResolvedTargetOf<TTarget>(context), token), ct);
    }

    public Task<MutationOutcome<bool>> RunAsync<TTarget>(AutomationActor actor, string capabilityId, object arguments,
        Func<TTarget, CancellationToken, Task> action, CancellationToken ct = default) where TTarget : ResolvedTarget
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunCoreAsync<bool>(actor, capabilityId, arguments, typeof(TTarget), async (context, token) =>
        {
            await action(UiMutationRunner.ResolvedTargetOf<TTarget>(context), token);
            return true;
        }, ct);
    }

    /// <summary>Not async: argument checks throw synchronously, before anything reaches the gateway (the scheduler's
    /// backstop relies on it, INV-45 review round 12).</summary>
    private Task<MutationOutcome<T>> RunCoreAsync<T>(AutomationActor actor, string capabilityId, object arguments,
        Type? targetType, Func<MutationContext, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentNullException.ThrowIfNull(arguments);

        var args = UiMutationRunner.ToArguments(arguments);
        var capability = _catalog.Find(capabilityId);
        var target = capability is null
            ? new MutationTarget("unknown", "")
            : MutationTargets.FromArguments(capability, args);
        var request = new MutationRequest(capabilityId, Stamped(actor, PrincipalOf(actor, capability)), SurfaceOf(actor),
            target, args, OperationId: actor.OperationId) { ExpectedTarget = actor.ExpectedTarget };
        // Contract 0012 (D3): a lambda typed for another kind of object is refused before the gateway decides; the
        // refusal is the run's one event (under its EventKey, INV-45).
        if (UiMutationRunner.TargetTypeMismatch(capability, targetType) is { } mismatch)
            return RefuseMismatchAsync<T>(request with { EventKey = actor.EventKey }, mismatch, ct);
        // A failure answered as a value (false, a failed command, (false, …)) is recorded as Failed.
        return _gateway.ExecuteAsync(request,
            async (context, token) => MutationResults.Report(await action(context, token)), ct);
    }

    private async Task<MutationOutcome<T>> RefuseMismatchAsync<T>(MutationRequest request, string reason,
        CancellationToken ct)
    {
        var refused = await _gateway.RefuseAsync(request, reason, ct);
        return new MutationOutcome<T>(refused.OperationId, MutationStatus.Denied, default, reason, VerificationStatus.NotRun)
            { DecisionId = refused.DecisionId };
    }

    public Task<MutationOutcome<object?>> RefuseAsync(AutomationActor actor, string capabilityId, object arguments,
        string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var args = UiMutationRunner.ToArguments(arguments);
        var capability = _catalog.Find(capabilityId);
        var target = capability is null ? new MutationTarget("unknown", "") : MutationTargets.FromArguments(capability, args);
        var principal = string.IsNullOrWhiteSpace(actor.OwnerId)
            ? new MutationPrincipal(actor.Kind, PrincipalIds.Unowned(actor.RunnerId),
                $"{actor.DisplayName} (no owner)", McpPermissionLevels.Read)
            : PrincipalOf(actor, capability);
        return _gateway.RefuseAsync(new MutationRequest(capabilityId, Stamped(actor, principal), SurfaceOf(actor), target, args,
            OperationId: actor.OperationId) { EventKey = actor.EventKey }, reason, ct);
    }

    /// <summary>A webhook delivery comes in on its own surface; every other run on the automation surface.</summary>
    private static MutationSurface SurfaceOf(AutomationActor actor) =>
        actor.Kind == MutationPrincipalKind.Webhook ? MutationSurface.Webhook : MutationSurface.Automation;

    /// <summary>Contract 0014: the credential (a webhook's secret generation) and the run's own id as its session.</summary>
    private static MutationPrincipal Stamped(AutomationActor actor, MutationPrincipal principal) =>
        principal with { CredentialId = actor.CredentialId, SessionId = actor.SessionId ?? actor.OperationId };

    public string? LostAuthority(AutomationActor actor, string capabilityId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var capability = _catalog.Find(capabilityId);
        if (capability is null) return null;
        if (string.IsNullOrWhiteSpace(actor.OwnerId))
            return _options.CurrentValue.RequireOwner ? "it has no owner, and owners are required" : null;
        MutationPrincipal owner;
        OwnerState state;
        try
        {
            owner = _owners.Resolve(actor.OwnerId);
            state = _owners.Describe(actor.OwnerId).State;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown, not lost: the run was refused (PrincipalOf), the item is not suspended for a store that failed.
            _logger.LogWarning(ex, "The owner {OwnerId} of automation {Runner} could not be resolved; its authority is " +
                "unknown, it is not suspended",
                Whiskers.Utils.LogSafe.Value(actor.OwnerId), actor.RunnerId);
            return null;
        }
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
            MutationPrincipal owner;
            try
            {
                owner = _owners.Resolve(actor.OwnerId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Logged with its cause (review round 14): a fault in the owner lookup must not read as a refusal.
                _logger.LogError(ex, "The owner {OwnerId} of automation {Runner} could not be resolved; the run is refused",
                    Whiskers.Utils.LogSafe.Value(actor.OwnerId), actor.RunnerId);
                // The owner's authority cannot be read (INV-45 review round 13): the run is still one event — the
                // gateway refuses this principal for every capability (McpPermissionLevels.None, never normalised to
                // read) — and the audit row says why and names the stored owner, itself without authority. Nothing is
                // granted to anyone; no level is guessed.
                var unreadable = new MutationPrincipal(
                    PrincipalIds.KeyIdOf(actor.OwnerId) is not null ? MutationPrincipalKind.ApiKey : MutationPrincipalKind.Human,
                    actor.OwnerId, $"{actor.OwnerId} (unreadable: {ex.GetType().Name})", McpPermissionLevels.None);
                return new MutationPrincipal(actor.Kind, actor.RunnerId,
                    $"{actor.DisplayName} (owner unreadable)", McpPermissionLevels.None, OnBehalfOf: unreadable,
                    DelegatedBy: actor.CreatedVia);
            }
            return new MutationPrincipal(actor.Kind, actor.RunnerId,
                $"{actor.DisplayName} (owner {owner.DisplayName})", owner.PermissionLevel,
                OnBehalfOf: owner, AllowedTools: owner.AllowedTools, DelegatedBy: actor.CreatedVia);
        }

        // An item from before owners were recorded. During the grace period it keeps the authority it always had,
        // for exactly the capability it runs — no more — and the audit row says it has no owner.
        var level = !_options.CurrentValue.RequireOwner && capability is not null
            ? capability.RequiredLevel
            : McpPermissionLevels.Read;
        return new MutationPrincipal(actor.Kind, PrincipalIds.Unowned(actor.RunnerId),
            $"{actor.DisplayName} (no owner)", level);
    }
}
