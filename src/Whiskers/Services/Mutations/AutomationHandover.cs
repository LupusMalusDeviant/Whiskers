using System.Runtime.ExceptionServices;
using Whiskers.Models;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.EndpointChecks;
using Whiskers.Services.GitDeploy;
using Whiskers.Services.Notifications;
using Whiskers.Services.Scheduler;
using Whiskers.Services.Webhooks;

namespace Whiskers.Services.Mutations;

/// <summary>Contract 0020: whether an item may be handed over — its current owner cannot run it.</summary>
public enum HandoverEligibility
{
    /// <summary>The owner's state is not <see cref="OwnerState.Active"/>, or <see cref="IAutomationMutationRunner.LostAuthority"/>
    /// is non-null for one of the item's capabilities.</summary>
    CannotRunOnOwner,
    /// <summary>The owner is Active and holds every capability: not offered (D4).</summary>
    RunsOnOwner,
    /// <summary>The state or the authority could not be judged: an exception, an unreadable store, an unknown
    /// capability id, or a runner without a real <c>LostAuthority</c>. Never offered; refuses a handover that names
    /// the item.</summary>
    CannotJudge,
}

/// <summary>The kinds of item that run on an owner's authority (INV-04).</summary>
public enum AutomationKind { ScheduledTask, Webhook, GitDeployApp, AiTrigger, EndpointCheck, FleetFeature }

/// <summary>One item: its kind and its id (task id, webhook id, app id, trigger id, check id, or the
/// <see cref="IAutomationOwnerStore"/> feature id).</summary>
public sealed record AutomationItemRef(AutomationKind Kind, string Id);

/// <summary>An item as the dialog shows it. <paramref name="Capabilities"/> are the catalog ids a run needs.
/// <paramref name="Why"/> is the reason text for <see cref="HandoverEligibility.CannotRunOnOwner"/> or
/// <see cref="HandoverEligibility.CannotJudge"/>.</summary>
public sealed record OwnedAutomation(AutomationItemRef Ref, string Name, bool On, string? SuspendedReason,
    IReadOnlyList<string> Capabilities, HandoverEligibility Eligibility, string? Why, bool KeyProvenance);

/// <summary>An owner who holds at least one item with <see cref="HandoverEligibility.CannotRunOnOwner"/> or
/// <see cref="HandoverEligibility.CannotJudge"/>; the second kind is listed so the Admin sees it, never offered.</summary>
public sealed record UnusableOwner(string OwnerId, OwnerDescription Description,
    IReadOnlyDictionary<AutomationKind, int> Counts);

public enum HandoverItemStatus
{
    /// <summary>The owner was replaced, the item is off (D2) and recorded in the ledger.</summary>
    HandedOver,
    /// <summary>Not changed: its owner is no longer <c>fromOwnerId</c> (someone took it over meanwhile).</summary>
    SkippedOwnerChanged,
    /// <summary>Not changed: the item was deleted meanwhile.</summary>
    SkippedGone,
    /// <summary>Its store write failed; the item is unchanged.</summary>
    Failed,
    /// <summary>Not attempted, because an earlier item's write failed and the handover stopped.</summary>
    NotAttempted,
}

public sealed record HandoverItemResult(AutomationItemRef Ref, string Name, bool WasOn, HandoverItemStatus Status);
public sealed record HandoverResult(string FromOwnerId, string ToOwnerId, IReadOnlyList<HandoverItemResult> Items);

/// <summary>Why the handover cannot go ahead: per item, and for the new owner. Empty when it can.</summary>
public sealed record HandoverCheck(IReadOnlyDictionary<AutomationItemRef, string> Refused, string? OwnerRefused)
{
    public bool Ok => Refused.Count == 0 && OwnerRefused is null;

    /// <summary>Every reason on one line, for a refusal's event.</summary>
    public string Describe() => string.Join("; ", (OwnerRefused is null ? [] : new[] { $"new owner: {OwnerRefused}" })
        .Concat(Refused.Select(r => $"{r.Key.Kind} {r.Key.Id}: {r.Value}")));
}

/// <summary>Thrown inside the decision when <see cref="IAutomationHandover.CheckAsync"/> fails on re-check. Nothing was
/// written.</summary>
public sealed class HandoverRefusedException(HandoverCheck check) : InvalidOperationException("Handover refused: " + check.Describe())
{
    public HandoverCheck Check { get; } = check;
}

/// <summary>What one store's handover or hold write did: <c>HandedOver</c> (also for a hold) or <c>Skipped*</c>.</summary>
public sealed record HandoverWrite(HandoverItemStatus Status, string Name, bool WasOn);

/// <summary>
/// Contract 0020: hands the automation of an owner who can no longer act over to another usable person (INV-66).
/// Read-only methods list and check; <see cref="HandOverAsync"/> runs inside the <c>automation.handover</c> decision.
/// <para>Deviation (amendment, implementation): the read methods are asynchronous — the stores they read are.</para>
/// </summary>
public interface IAutomationHandover
{
    /// <summary>Every owner with at least one item the predicate does not clear. Read-only.</summary>
    Task<IReadOnlyList<UnusableOwner>> UnusableOwnersAsync(CancellationToken ct = default);

    /// <summary>Every item recorded on <paramref name="ownerId"/>, with its eligibility. Read-only.</summary>
    Task<IReadOnlyList<OwnedAutomation>> OwnedByAsync(string ownerId, CancellationToken ct = default);

    /// <summary>The rules the handover applies (D3, D4, D12), for the dialog before the click and for the page's
    /// refusal. Read-only. Anything it cannot read or judge counts as refused.</summary>
    Task<HandoverCheck> CheckAsync(string fromOwnerId, string toOwnerId, IReadOnlyList<AutomationItemRef> items,
        CancellationToken ct = default);

    /// <summary>Under one handover lock: runs <see cref="CheckAsync"/> again and throws
    /// <see cref="HandoverRefusedException"/> when it fails (nothing written). Otherwise, one item after the other,
    /// calls the item's store method. A store exception stops the loop: that item is <c>Failed</c> and the rest are
    /// <c>NotAttempted</c>. The partial result is noted on the event and the exception is rethrown.</summary>
    [MutatingOperation("automation.handover")]
    Task<HandoverResult> HandOverAsync([MutationTarget] string fromOwnerId, string toOwnerId,
        IReadOnlyList<AutomationItemRef> items, CancellationToken ct = default);
}

/// <summary>Contract 0020 (D11 c): holds every automation still recorded on an address that gets a local account
/// again, inside the <c>account.create</c> decision (nested <c>automation.hold</c>), before the account row exists.</summary>
public interface IAutomationHold
{
    /// <summary>Switches off (Git app: held; fleet feature: blocked) every item recorded on <paramref name="ownerId"/>,
    /// leaving its owner as it is, records each in the ledger as a <see cref="HandoverEntryKind.Hold"/> and notes the
    /// list on the event. Throws when anything cannot be read or written: the caller then creates no account.</summary>
    [MutatingOperation("automation.hold")]
    Task<IReadOnlyList<HandoverItemResult>> HoldAllAsync(string ownerId, CancellationToken ct = default);
}

/// <summary>One stored item as the handover reads it.</summary>
internal sealed record InventoryItem(AutomationItemRef Ref, string Name, bool On, string? SuspendedReason,
    string OwnerId, string? CreatedVia, IReadOnlyList<string> Capabilities, AutomationActor? Actor)
{
    /// <summary>D12: owned by an MCP key, or (a task) created through one.</summary>
    public bool KeyProvenance => PrincipalIds.KeyIdOf(OwnerId) is not null || PrincipalIds.KeyIdOf(CreatedVia) is not null;
}

/// <summary>Reads every owned automation item of the six kinds from their stores. A store that is not registered (its
/// module is off) holds nothing; a store that cannot be read throws.</summary>
internal sealed class AutomationInventory(
    ISchedulerService? scheduler, IWebhookService? webhooks, IGitDeployService? gitDeploy, IAiTriggerStore? triggers,
    IEndpointCheckStore? checks, IAutomationOwnerStore? fleet)
{
    /// <summary>The capability each fleet feature runs.</summary>
    public static string CapabilityOfFeature(string feature) => feature switch
    {
        IAutomationOwnerStore.AutoPatch => "autopatch.run",
        IAutomationOwnerStore.AutoUpdate => "container.update",
        IAutomationOwnerStore.VpnBootstrap => "vpn.bootstrap",
        _ => "",
    };

    /// <summary>The capability a webhook's action runs, or empty for an unknown action.</summary>
    public static string CapabilityOfWebhookAction(string? action) => action switch
    {
        "restart" => "container.restart",
        "rebuild" => "container.update",
        "deploy" => "compose.deploy",
        "git-deploy" => "gitdeploy.deploy",
        _ => "",
    };

    public async Task<IReadOnlyList<InventoryItem>> ReadAsync(CancellationToken ct)
    {
        var items = new List<InventoryItem>();
        if (scheduler is not null)
            foreach (var t in await scheduler.GetTasksAsync())
                if (!string.IsNullOrWhiteSpace(t.CreatedBy))
                    items.Add(new(new(AutomationKind.ScheduledTask, t.TaskId), t.Name, t.Enabled, null, t.CreatedBy,
                        t.CreatedVia, [TaskExecutor.CapabilityOf(t.TaskType)],
                        AutomationActor.ForTask(t.TaskId, t.Name, t.CreatedBy, t.CreatedVia)));
        ct.ThrowIfCancellationRequested();
        if (webhooks is not null)
            foreach (var w in await webhooks.GetWebhooksAsync())
                if (!string.IsNullOrWhiteSpace(w.Owner))
                    items.Add(new(new(AutomationKind.Webhook, w.WebhookId), w.Name, w.Enabled, null, w.Owner, null,
                        [CapabilityOfWebhookAction(w.Action)],
                        AutomationActor.ForWebhook(w.WebhookId, w.Name, w.Owner, Math.Max(1, w.SecretVersion))));
        ct.ThrowIfCancellationRequested();
        if (gitDeploy is not null)
            foreach (var a in await gitDeploy.GetAppsAsync())
                if (!string.IsNullOrWhiteSpace(a.Owner))
                    items.Add(new(new(AutomationKind.GitDeployApp, a.Id), a.Name, a.HeldReason is null, a.HeldReason,
                        a.Owner, null, ["gitdeploy.deploy"], AutomationActor.ForGitDeploy(a.Id, a.Name, a.Owner)));
        if (triggers is not null)
            foreach (var t in triggers.Triggers)
                if (!string.IsNullOrWhiteSpace(t.OwnerId))
                    items.Add(new(new(AutomationKind.AiTrigger, t.Id), t.Name, t.Enabled, t.SuspendedReason, t.OwnerId,
                        null, t.WriteCapabilities.ToList(), null));
        if (checks is not null)
            foreach (var c in checks.List())
                if (!string.IsNullOrWhiteSpace(c.OwnerId))
                    items.Add(new(new(AutomationKind.EndpointCheck, c.Id), c.Name, c.Enabled, c.SuspendedReason, c.OwnerId,
                        null, ["endpointcheck.run"], AutomationActor.ForCheck(c.Id, c.Name, c.OwnerId)));
        if (fleet is not null)
            foreach (var feature in IAutomationOwnerStore.Features)
            {
                var blocked = fleet.GetBlocked(feature);
                var entry = blocked ?? fleet.Get(feature);
                if (entry is null || string.IsNullOrWhiteSpace(entry.OwnerId)) continue;
                var actor = feature switch
                {
                    IAutomationOwnerStore.AutoPatch => AutomationActor.ForAutoPatch(entry.OwnerId),
                    IAutomationOwnerStore.AutoUpdate => AutomationActor.ForAutoUpdate(entry.OwnerId),
                    _ => AutomationActor.ForVpnBootstrap("VPN bring-up", entry.OwnerId),
                };
                items.Add(new(new(AutomationKind.FleetFeature, feature), feature, blocked is null,
                    blocked?.SuspendedReason, entry.OwnerId, null, [CapabilityOfFeature(feature)], actor));
            }
        return items;
    }
}

/// <summary>
/// Contract 0020's predicate and new-owner rule, shared by the handover and its tests. The predicate takes its own
/// route on purpose: an <see cref="OwnerState.Unverified"/> owner cannot run its items (although
/// <c>LostAuthority</c> answers <c>null</c> for it), and anything <c>LostAuthority</c> answers with <c>null</c> for a
/// reason other than "holds it" — an unknown capability, a resolver exception, the interface default — is
/// <see cref="HandoverEligibility.CannotJudge"/>.
/// </summary>
internal sealed class HandoverJudge(IAutomationOwnerResolver resolver, IAutomationMutationRunner runner,
    ICapabilityCatalog catalog, ICapabilityOverrides? overrides)
{
    /// <summary>True when the runner implements <c>LostAuthority</c> itself (not the interface default).</summary>
    public bool RunnerJudges { get; } = Implements(runner);

    private static bool Implements(IAutomationMutationRunner runner)
    {
        var map = runner.GetType().GetInterfaceMap(typeof(IAutomationMutationRunner));
        var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IAutomationMutationRunner.LostAuthority));
        return index >= 0 && map.TargetMethods[index].DeclaringType != typeof(IAutomationMutationRunner);
    }

    public (HandoverEligibility Eligibility, string? Why) Judge(InventoryItem item)
    {
        if (!RunnerJudges) return (HandoverEligibility.CannotJudge, "the automation runner cannot judge the owner's authority");
        foreach (var id in item.Capabilities)
            if (catalog.Find(id) is null)
                return (HandoverEligibility.CannotJudge, $"unknown capability '{(id.Length == 0 ? "?" : id)}'");
        OwnerState state;
        MutationPrincipal owner;
        try
        {
            state = resolver.Describe(item.OwnerId).State;
            owner = resolver.Resolve(item.OwnerId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (HandoverEligibility.CannotJudge, $"its owner could not be read ({ex.GetType().Name})");
        }
        if (state != OwnerState.Active) return (HandoverEligibility.CannotRunOnOwner, state.ToString());
        foreach (var id in item.Capabilities)
        {
            string? lost;
            try
            {
                lost = item.Actor is { } actor ? runner.LostAuthority(actor, id) : LevelLacks(owner, catalog.Find(id)!, failClosed: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (HandoverEligibility.CannotJudge, $"its authority could not be judged ({ex.GetType().Name})");
            }
            if (lost is not null) return (HandoverEligibility.CannotRunOnOwner, lost);
        }
        return (HandoverEligibility.RunsOnOwner, null);
    }

    /// <summary>D3: why <paramref name="toOwnerId"/> cannot be the new owner at all, or <c>null</c>.</summary>
    public (string? Refused, MutationPrincipal? Principal) NewOwner(string fromOwnerId, string toOwnerId)
    {
        if (PrincipalIds.EmailOf(toOwnerId) is null) return ("the new owner must be a person, not a key", null);
        if (string.Equals(fromOwnerId, toOwnerId, StringComparison.Ordinal)) return ("the new owner is the old owner", null);
        if (!RunnerJudges) return ("the automation runner cannot judge the new owner's authority", null);
        try
        {
            if (resolver.Describe(toOwnerId).State != OwnerState.Active) return ("the new owner cannot act now", null);
            return (null, resolver.Resolve(toOwnerId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ($"the new owner could not be read ({ex.GetType().Name})", null);
        }
    }

    /// <summary>Why <paramref name="owner"/> does not hold <paramref name="capability"/> now (judged as a run judges it,
    /// a level raised in Settings → MCP included), or <c>null</c>. <paramref name="failClosed"/>: an override that
    /// cannot be read refuses (the new-owner check), instead of keeping the catalog level (a run's judgment).</summary>
    public string? LevelLacks(MutationPrincipal owner, Capability capability, bool failClosed)
    {
        var required = capability.RequiredLevel;
        if (overrides is not null)
        {
            try
            {
                var availability = overrides.Resolve(capability);
                if (availability.Enabled) required = availability.RequiredLevel;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (failClosed) return $"the level of {capability.Id} cannot be read";
            }
        }
        if (!McpPermissionLevels.HasAccess(owner.PermissionLevel, required))
            return $"needs {CapabilityCatalog.RoleFor(required)}";
        var tool = capability.McpTool ?? capability.Id;
        return owner.AllowedTools is { } allowed && !allowed.Contains(tool) ? $"may not use '{tool}'" : null;
    }
}

/// <summary>Contract 0020: the handover service over the existing stores (singleton).</summary>
public sealed class AutomationHandover : IAutomationHandover
{
    // One handover at a time: the re-check and the writes are one step.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly AutomationInventory _inventory;
    private readonly HandoverJudge _judge;
    private readonly IAutomationOwnerResolver _resolver;
    private readonly ICapabilityCatalog _catalog;
    private readonly IHandoverLedger _ledger;
    private readonly IHandoverNotices _notices;
    private readonly ISchedulerService? _scheduler;
    private readonly IWebhookService? _webhooks;
    private readonly IGitDeployService? _gitDeploy;
    private readonly IAiTriggerStore? _triggers;
    private readonly IEndpointCheckStore? _checks;
    private readonly IAutomationOwnerStore? _fleet;
    private readonly IServiceProvider? _services;
    private readonly ILogger<AutomationHandover>? _logger;

    public AutomationHandover(IAutomationOwnerResolver resolver, IAutomationMutationRunner runner,
        ICapabilityCatalog catalog, IHandoverLedger ledger, IHandoverNotices notices,
        ICapabilityOverrides? overrides = null, ISchedulerService? scheduler = null, IWebhookService? webhooks = null,
        IGitDeployService? gitDeploy = null, IAiTriggerStore? triggers = null, IEndpointCheckStore? checks = null,
        IAutomationOwnerStore? fleet = null, IServiceProvider? services = null, ILogger<AutomationHandover>? logger = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        ArgumentNullException.ThrowIfNull(runner);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _notices = notices ?? throw new ArgumentNullException(nameof(notices));
        _scheduler = scheduler;
        _webhooks = webhooks;
        _gitDeploy = gitDeploy;
        _triggers = triggers;
        _checks = checks;
        _fleet = fleet;
        _services = services;
        _logger = logger;
        _inventory = new AutomationInventory(scheduler, webhooks, gitDeploy, triggers, checks, fleet);
        _judge = new HandoverJudge(resolver, runner, catalog, overrides);
    }

    public async Task<IReadOnlyList<UnusableOwner>> UnusableOwnersAsync(CancellationToken ct = default)
    {
        var items = await _inventory.ReadAsync(ct);
        var owners = new List<UnusableOwner>();
        foreach (var group in items.GroupBy(i => i.OwnerId, StringComparer.Ordinal))
        {
            if (!group.Any(i => _judge.Judge(i).Eligibility != HandoverEligibility.RunsOnOwner)) continue;
            OwnerDescription description;
            try { description = _resolver.Describe(group.Key); }
            catch (Exception ex) when (ex is not OperationCanceledException) { description = new(group.Key, OwnerState.Unverified); }
            owners.Add(new UnusableOwner(group.Key, description,
                group.GroupBy(i => i.Ref.Kind).ToDictionary(g => g.Key, g => g.Count())));
        }
        return owners.OrderBy(o => o.Description.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<OwnedAutomation>> OwnedByAsync(string ownerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var items = await _inventory.ReadAsync(ct);
        return items.Where(i => string.Equals(i.OwnerId, ownerId, StringComparison.Ordinal))
            .Select(i =>
            {
                var (eligibility, why) = _judge.Judge(i);
                return new OwnedAutomation(i.Ref, i.Name, i.On, i.SuspendedReason, i.Capabilities, eligibility, why,
                    i.KeyProvenance);
            })
            .OrderBy(o => o.Ref.Kind).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<HandoverCheck> CheckAsync(string fromOwnerId, string toOwnerId, IReadOnlyList<AutomationItemRef> items,
        CancellationToken ct = default) => (await CheckCoreAsync(fromOwnerId, toOwnerId, items, ct)).Check;

    private async Task<(HandoverCheck Check, IReadOnlyList<InventoryItem> Inventory)> CheckCoreAsync(string fromOwnerId,
        string toOwnerId, IReadOnlyList<AutomationItemRef> items, CancellationToken ct)
    {
        var refused = new Dictionary<AutomationItemRef, string>();
        if (string.IsNullOrWhiteSpace(fromOwnerId) || string.IsNullOrWhiteSpace(toOwnerId) || items is null)
            return (new HandoverCheck(refused, "the old owner, the new owner and the items are required"), []);
        var (ownerRefused, newOwner) = _judge.NewOwner(fromOwnerId, toOwnerId);
        if (items.Count == 0) ownerRefused ??= "no item is selected";

        IReadOnlyList<InventoryItem> inventory;
        try
        {
            inventory = await _inventory.ReadAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A store that cannot be read refuses every item: nothing is handed over that could not be judged.
            _logger?.LogError(ex, "The automation stores could not be read for a handover check");
            foreach (var item in items.Distinct()) refused[item] = "its store could not be read";
            return (new HandoverCheck(refused, ownerRefused), []);
        }

        foreach (var item in items.Distinct())
        {
            var stored = inventory.FirstOrDefault(i => i.Ref == item);
            // Gone, or no longer the old owner's: skipped by the compare-and-set of the write, not refused (the rest
            // proceed). Nothing is written for it.
            if (stored is null || !string.Equals(stored.OwnerId, fromOwnerId, StringComparison.Ordinal)) continue;
            var (eligibility, why) = _judge.Judge(stored);
            if (eligibility == HandoverEligibility.CannotJudge) { refused[item] = $"cannot be judged: {why}"; continue; }
            if (eligibility == HandoverEligibility.RunsOnOwner) { refused[item] = "it still runs on its owner"; continue; }
            if (stored.KeyProvenance) { refused[item] = "it is owned or created by an MCP key"; continue; }
            if (newOwner is null) continue;
            foreach (var id in stored.Capabilities)
            {
                // Known: an unknown id made the item CannotJudge above.
                if (_judge.LevelLacks(newOwner, _catalog.Find(id)!, failClosed: true) is { } lacks)
                {
                    refused[item] = lacks;
                    break;
                }
            }
        }
        return (new HandoverCheck(refused, ownerRefused), inventory);
    }

    public async Task<HandoverResult> HandOverAsync(string fromOwnerId, string toOwnerId,
        IReadOnlyList<AutomationItemRef> items, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toOwnerId);
        ArgumentNullException.ThrowIfNull(items);
        var scope = MutationScope.Current
                    ?? throw new InvalidOperationException("A handover runs only inside its automation.handover decision.");
        var admin = scope.Principal?.Id;
        if (PrincipalIds.EmailOf(admin) is null)
            throw new UnauthorizedAccessException("A handover is decided by a signed-in Admin only.");
        var selected = items.Distinct().ToList();

        await _lock.WaitAsync(ct);
        try
        {
            var (check, inventory) = await CheckCoreAsync(fromOwnerId, toOwnerId, selected, ct);
            if (check.Ok && !_notices.IsReadable)
                check = new HandoverCheck(check.Refused, "the handover notices cannot be read");
            if (!check.Ok)
            {
                MutationScope.Note($"handover refused on re-check: {check.Describe()}", success: false);
                throw new HandoverRefusedException(check);
            }
            try
            {
                _ledger.RecordHandover(scope.OperationId, fromOwnerId, toOwnerId, admin!, selected);
            }
            catch (HandoverLedgerUnavailableException)
            {
                var refused = new HandoverCheck(new Dictionary<AutomationItemRef, string>(), "the handover ledger cannot be written");
                MutationScope.Note($"handover refused: {refused.Describe()}", success: false);
                throw new HandoverRefusedException(refused);
            }

            var results = new List<HandoverItemResult>(selected.Count);
            ExceptionDispatchInfo? failure = null;
            foreach (var item in selected)
            {
                var name = inventory.FirstOrDefault(i => i.Ref == item)?.Name ?? item.Id;
                var wasOn = inventory.FirstOrDefault(i => i.Ref == item)?.On ?? false;
                HandoverItemResult result;
                if (failure is not null)
                    result = new(item, name, wasOn, HandoverItemStatus.NotAttempted);
                else
                {
                    try
                    {
                        var write = await WriteAsync(item, fromOwnerId, toOwnerId, ct);
                        result = new(item, write.Name, write.WasOn, write.Status);
                    }
                    catch (Exception ex)
                    {
                        failure = ExceptionDispatchInfo.Capture(ex);
                        result = new(item, name, wasOn, HandoverItemStatus.Failed);
                    }
                }
                results.Add(result);
                Reconcile(scope.OperationId, item, result.Status);
            }

            var outcome = new HandoverResult(fromOwnerId, toOwnerId, results);
            HandoverEvent.RecordItems(results);
            // The audit row is bounded (its fields shrink to fit); the operation log keeps every item in full.
            foreach (var r in results)
                _logger?.LogInformation("Handover {Operation}: {Kind} {Id} ({WasOn}) {Status}", scope.OperationId, r.Ref.Kind,
                    r.Ref.Id, r.WasOn ? "was on" : "was off", r.Status);
            MutationScope.Note(EventDetails(outcome), success: failure is null);
            failure?.Throw();

            var handedOver = results.Count(r => r.Status == HandoverItemStatus.HandedOver);
            if (handedOver > 0)
            {
                RecordNotice(scope.OperationId, fromOwnerId, toOwnerId, admin!, handedOver);
                await NotifyAdminsAsync(scope.OperationId, fromOwnerId, toOwnerId, admin!, results);
            }
            return outcome;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The event's summary: both owners with their authority and the item count; every item is its own result
    /// (<see cref="HandoverEvent.RecordItems"/>), so no item is cut off by the audit's per-result length.</summary>
    private string EventDetails(HandoverResult result)
    {
        string Authority(string id)
        {
            try { return AutomationOwners.AuthorityOf(_resolver, id); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return $"{id} (unreadable)"; }
        }
        return $"handover = {{ from: {Authority(result.FromOwnerId)}, to: {Authority(result.ToOwnerId)}, items: {result.Items.Count} }}";
    }

    private void Reconcile(string operationId, AutomationItemRef item, HandoverItemStatus status)
    {
        try
        {
            _ledger.Reconcile(operationId, item, status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The item write stands (it is off, so safe); the stale Pending row reads as "not handed over".
            _logger?.LogError(ex, "The handover ledger could not record {Kind} {Id} as {Status}", item.Kind, item.Id, status);
        }
    }

    private Task<HandoverWrite> WriteAsync(AutomationItemRef item, string from, string to, CancellationToken ct) => item.Kind switch
    {
        AutomationKind.ScheduledTask => (_scheduler ?? throw Missing(item)).HandOverAsync(item.Id, from, to, ct),
        AutomationKind.Webhook => (_webhooks ?? throw Missing(item)).HandOverAsync(item.Id, from, to, ct),
        AutomationKind.GitDeployApp => (_gitDeploy ?? throw Missing(item)).HandOverAsync(item.Id, from, to, ct),
        AutomationKind.AiTrigger => (_triggers ?? throw Missing(item)).HandOverAsync(item.Id, from, to, ct),
        AutomationKind.EndpointCheck => (_checks ?? throw Missing(item)).HandOverAsync(item.Id, from, to, ct),
        AutomationKind.FleetFeature => (_fleet ?? throw Missing(item)).HandOverAsync(item.Id, from, to, ct),
        _ => throw Missing(item),
    };

    private static InvalidOperationException Missing(AutomationItemRef item) =>
        new($"No store keeps {item.Kind} items in this installation.");

    private void RecordNotice(string operationId, string from, string to, string admin, int count)
    {
        try
        {
            _notices.RecordNotice(new HandoverNotice(operationId, operationId, to, from, admin, DateTime.UtcNow, count));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Every item is off already; the handover stands. The Admins' notification and the audit still say it.
            _logger?.LogError(ex, "The handover notice for {Owner} could not be recorded", Whiskers.Utils.LogSafe.Account(to));
            MutationScope.Note("the new owner's notice could not be recorded", success: true);
        }
    }

    /// <summary>D9: one administrative notification per executed handover, counts only — no item names, no arguments.</summary>
    private async Task NotifyAdminsAsync(string operationId, string from, string to, string admin,
        IReadOnlyList<HandoverItemResult> results)
    {
        var handed = results.Where(r => r.Status == HandoverItemStatus.HandedOver).ToList();
        var counts = string.Join(", ", handed.GroupBy(r => r.Ref.Kind).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}"));
        try
        {
            if (_services?.GetService(typeof(INotificationService)) is INotificationService notify)
                await notify.SendAsync(new NotificationEvent
                {
                    EventType = NotificationEventTypes.AutomationHandedOver,
                    ThrottleSubject = NotificationThrottleSubjects.Handover(operationId),
                    ContainerName = "Automation handover",
                    ImageInfo = $"The automation of {Name(from)} ({handed.Count} items: {counts}) was handed over to " +
                                $"{Name(to)} by {Name(admin)}. Every item is off until it is switched on again.",
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "The notification of handover {Operation} failed", operationId);
        }
    }

    private static string Name(string principalId) => PrincipalIds.EmailOf(principalId) ?? principalId;
}

/// <summary>Contract 0020 (D11 c): the hold of every item still recorded on an address that gets a local account again.</summary>
public sealed class AutomationHold : IAutomationHold
{
    private readonly AutomationInventory _inventory;
    private readonly IHandoverLedger _ledger;
    private readonly ISchedulerService? _scheduler;
    private readonly IWebhookService? _webhooks;
    private readonly IGitDeployService? _gitDeploy;
    private readonly IAiTriggerStore? _triggers;
    private readonly IEndpointCheckStore? _checks;
    private readonly IAutomationOwnerStore? _fleet;

    public AutomationHold(IHandoverLedger ledger, ISchedulerService? scheduler = null, IWebhookService? webhooks = null,
        IGitDeployService? gitDeploy = null, IAiTriggerStore? triggers = null, IEndpointCheckStore? checks = null,
        IAutomationOwnerStore? fleet = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _scheduler = scheduler;
        _webhooks = webhooks;
        _gitDeploy = gitDeploy;
        _triggers = triggers;
        _checks = checks;
        _fleet = fleet;
        _inventory = new AutomationInventory(scheduler, webhooks, gitDeploy, triggers, checks, fleet);
    }

    public async Task<IReadOnlyList<HandoverItemResult>> HoldAllAsync(string ownerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var scope = MutationScope.Current
                    ?? throw new InvalidOperationException("A hold runs only inside the account.create decision.");
        var admin = scope.Principal?.Id;
        if (string.IsNullOrWhiteSpace(admin))
            throw new UnauthorizedAccessException("A hold is decided by a signed-in Admin only.");

        var owned = (await _inventory.ReadAsync(ct))
            .Where(i => string.Equals(i.OwnerId, ownerId, StringComparison.Ordinal)).ToList();
        if (owned.Count == 0) return [];
        // Before any item: a ledger that cannot be written refuses the hold, and with it the account.
        _ledger.RecordHandover(scope.OperationId, ownerId, ownerId, admin, owned.Select(i => i.Ref).ToList(),
            HandoverEntryKind.Hold);

        var results = new List<HandoverItemResult>(owned.Count);
        try
        {
            foreach (var item in owned)
            {
                var write = await HoldAsync(item.Ref, ownerId, ct);
                results.Add(new(item.Ref, write.Name, write.WasOn, write.Status));
                try { _ledger.Reconcile(scope.OperationId, item.Ref, write.Status); }
                catch (HandoverLedgerUnavailableException) { /* the item is off either way */ }
            }
        }
        finally
        {
            HandoverEvent.RecordItems(results);
            MutationScope.Note($"hold = {{ owner: {ownerId}, items: {results.Count} of {owned.Count} }}");
        }
        return results;
    }

    private Task<HandoverWrite> HoldAsync(AutomationItemRef item, string owner, CancellationToken ct) => item.Kind switch
    {
        AutomationKind.ScheduledTask => _scheduler!.HoldAsync(item.Id, owner, ct),
        AutomationKind.Webhook => _webhooks!.HoldAsync(item.Id, owner, ct),
        AutomationKind.GitDeployApp => _gitDeploy!.HoldAsync(item.Id, owner, ct),
        AutomationKind.AiTrigger => _triggers!.HoldAsync(item.Id, owner, ct),
        AutomationKind.EndpointCheck => _checks!.HoldAsync(item.Id, owner, ct),
        AutomationKind.FleetFeature => _fleet!.HoldAsync(item.Id, owner, ct),
        _ => throw new InvalidOperationException($"No store keeps {item.Kind} items."),
    };
}

/// <summary>Contract 0020: one event result per handed-over or held item (kind, id, name, was on, status), so the audit
/// names every item — a single result is cut at the audit's per-result length.</summary>
internal static class HandoverEvent
{
    public static void RecordItems(IEnumerable<HandoverItemResult> items)
    {
        if (MutationScope.Current is not { } scope) return;
        foreach (var i in items)
            MutationScope.Record(new MutationDomainResult(scope.CapabilityId, "automation-item", $"{i.Ref.Kind}:{i.Ref.Id}",
                i.Name, $"kind: {i.Ref.Kind}, id: {i.Ref.Id}, name: {i.Name}, wasOn: {(i.WasOn ? "true" : "false")}, status: {i.Status}",
                null, i.Status != HandoverItemStatus.Failed));
    }
}
