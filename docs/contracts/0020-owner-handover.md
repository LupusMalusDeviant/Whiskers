# Contract 0020 — Handing over the automation of an unusable owner

Status: **Draft — awaiting the maintainer's acceptance (2026-11-16).** The direction is the maintainer's decision of
16 November 2026 and is not open here: **no automatic re-linking by e-mail**. Instead, an Admin hands the automation of
an owner who can no longer act over to another existing, usable account. The handover is explicit, interactive,
audited and goes through the Mutation Gateway. The open points are listed under "Decisions for the maintainer".
Revised after review (2026-11-16).

Scope:
- **Protected areas:** none touched. The auth middleware and its order, the OAuth whitelist, the mTLS/WireGuard layer
  and the docker-socket-proxy verb whitelist stay as they are. Sign-in, federated binding (contract 0003) and
  `OwnerAccounts`' verdict do not change. This contract adds a way to move items **away** from an owner, not a way to
  give an owner back its authority. D11 (c) would add a step to `account.create`, but not to the auth layer.
- **Surfaces:** the web UI only (`MutationSurface.Ui`, `IUiMutationRunner`). There is no MCP tool, no agent tool and no
  automation path (D10).

## Purpose

An owner's local account is deleted. Later the same person signs in through Google or OIDC with the same address.
Everything they owned stays refused or suspended. Today the only way back is a takeover **by the Admin who clicks**,
item by item and page by page. Often the right new owner is somebody else, for example the colleague who inherits the
service. And nobody can see in one place what the departed owner still held.

The goal: one Admin action per departed owner. It lists every automation still recorded on that owner and hands the
chosen items to a named, usable account. Nothing runs on the new owner's authority until someone switches it on.

## Current state (verified at `ccd5137e`)

### The verdict on a deleted owner

- `OwnerAccounts.StateOf` (`Services/Mutations/OwnerAccounts.cs:79-96`):
  - With a local row present, the owner is `Present` (`:89`).
  - Without one, an address that **ever had** a local account is `Deleted` (`:90`). This holds even when the address
    has been seen signing in federated since, because the local record is checked first (`:90` before `:92-94`).
  - With local accounts only, any missing row is `Deleted`.
  - An unreadable store gives `Unverified` (`:85`).
- `AutomationOwnerResolver.UserState` (`Services/Mutations/AutomationMutationRunner.cs:218-224`) maps that to
  `OwnerState.AccountDeleted` / `Unverified`. `Resolve` (`:239-268`) then gives the owner `read` (`:260`), so every
  mutation of the item is refused (INV-04).
- `LostAuthority` (`:410-458`):
  - It returns `null` for an `Unverified` owner (`:433`): "refused but not suspended".
  - It also returns `null` when the resolver throws (`:424-430`) and for an unknown capability id (`:414`).
  - The interface default is `=> null` (`:299`).
  - So `LostAuthority == null` does **not** mean "can run".
- `AgentAuthority` (`Services/Agent/AgentAuthority.cs:29-34`, `UserNow`) reads users through the same resolver, so a
  running agent session of the deleted user stops (INV-08). The gateway re-checks an AI trigger's run for each
  mutation (R1-13).

**Confirmed as described.** A federated sign-in with the same address never brings the authority back.

**Contradicting detail (finding, D11):** re-creating a **local** account with the same address does bring the
authority back. `StateOf` checks the local rows first (`OwnerAccounts.cs:89`), and `account.create` calls
`RememberLocalAccounts` (`Services/Auth/LocalAccountService.cs:199`). The `account.create` is an explicit, audited
Admin act, but it does not say what it revives:
- every item still recorded on the address that was **never suspended** (no run since the deletion) runs again at its
  next schedule or event: tasks, webhooks, AI triggers, endpoint checks;
- Git deploy apps have no switch and deploy at the next push;
- items that were suspended stay off until someone switches them on.

### What an unusable owner holds, and what happens to it

| Kind | Owner field | On authority loss | Today's takeover (to the clicking Admin) |
|---|---|---|---|
| Scheduled task | `ScheduledTaskEntity.CreatedBy` (+ `CreatedVia`) | switched off, `automation_suspended` (`Services/Scheduler/TaskExecutor.cs:84-91`, `:558-563`) | `scheduler.update` → `SchedulerService.SetOwnerAsync` (`:325-338`), conditional on the old owner; sets `CreatedVia` to the new owner; `Enabled` untouched, so the task stays off (`Components/Pages/ScheduledTasksView.razor:600-611`); bulk "Adopt all" (`:622-628`) |
| Webhook | `WebhookEntity.Owner` | switched off, audited `webhook.suspended` (`Services/Webhooks/WebhookService.cs:1346-1352`, `:1502-1545`) | `webhook.save` → `WebhookService.SetOwnerAsync` (`:103-130`), compare-and-set; `Enabled` untouched (`Components/Pages/WebhooksView.razor:331-362`) |
| Git deploy app | `GitDeployApp.Owner` | **no switch**: each webhook-fired deployment is refused (R1-08) | `gitdeploy.save` → `GitDeployService.SetOwnerAsync` (`:199-218`), bumps `Revision` (`Components/Pages/GitDeployView.razor:337-370`). Saving the app also makes the saver its owner (`GitDeployService.cs:166-167`). |
| AI trigger | `AiTrigger.OwnerId` + `SuspendedReason` (`Models/AiTrigger.cs:31-39`) | switched off with a reason (`AiTriggerStore.SuspendAsync`) | `aitrigger.save`: switching a suspended or ownerless trigger **on** makes the editor its owner (`Services/Agent/Triggers/AiTriggerStore.cs:107-117`) |
| Endpoint check | `EndpointCheckEntity.OwnerId` + `SuspendedReason` | switched off with a reason (`Services/EndpointChecks/EndpointCheckMonitor.cs:166-169`, `:218-240`) | `endpointcheck.save`: **every** save makes the saver the owner and clears the suspension (`Services/EndpointChecks/EndpointCheckStore.cs:16-20`, `:115-117`) |
| AutoPatch, AutoUpdate, VPN bring-up | `automation-owners.json` (`AutomationOwnerStore`) | blocked with a reason (`AutoPatchService.cs:288-310`, `AutoUpdateService.cs:242-249`, `VpnBootstrapHostedService.cs:147-151`) | `automation.owner_set` in *Settings → Automation owners* (`Components/Shared/AutomationOwnersPanel.razor:104-146`); `SetAsync` clears the block, so the feature **runs again at once** (`AutomationOwnerStore.cs:97-102`) |

How the takeovers work today:
- Every takeover is to the signed-in Admin only, one item (or one page) at a time.
- Each goes through the gateway, with the previous and the new owner in the event (`MutationScope.Note`,
  `AutomationOwners.AuthorityOf`, `AutomationMutationRunner.cs:155-162`).
- Suspensions are conditional on the owner they read, so a takeover in between wins (`AutomationOwnerStore.cs:104-118`,
  `EndpointCheckStore.cs:131-140`).

There is no list of "owners who can no longer act". The account-deletion dialog
(`Components/Shared/LocalAccountsPanel.razor:179-180`) says nothing about what the account owns.

### Which rule set an item's runs are decided under

`GuardrailMutationPolicy` (`Services/Mutations/MutationPolicy.cs:93-98`) picks the human rule set
(`GuardrailEngine.CreateForHumans`) for an unattended run when two things hold: the owner (`OnBehalfOf`) is a
**person**, and the item was not created by an agent (`CreatedByAgent`, `:117-120`: the run's `DelegatedBy` is empty or
starts with the agent prefix). Otherwise the run is decided under the agent rule set (`CreateDefault`), which includes
`ExposureChangeRule`, write confirmation and tool lists.

- **Key-owned items exist.** A scheduled task created over MCP with a key is owned by the key, and its provenance is
  the key (`AutomationOwners.OfCaller`, `AutomationMutationRunner.cs:188-197`; `ViaOf`, `:182-183`).
- **Webhooks and Git apps** take their provenance from the owner itself (`AutomationActor.ForWebhook`,
  `ForGitDeploy`: `CreatedVia = ownerId`, `:66-72`, `:83-88`). Their MCP tools record a person as owner since
  contract 0001 (`Mcp/Tools/GitDeployTools.cs:88`, `:135`, `:171`, `AutomationOwners.PersonFor`). No current code path
  writes a key owner on a webhook or a Git app. Rows written before contract 0001 may still hold one; the code cannot
  rule that out.
- **What a handover would change.** Handing a key-owned item to a person would move its runs from the agent rule set to
  the human rule set:
  - For a webhook or Git app this happens because the provenance follows the owner.
  - For a key-created task it happens even with `CreatedVia` unchanged, because `CreatedByAgent` does not treat a `key:`
    provenance as an agent's.
  - The human rule set has no exposure confirmation: `ExposureChangeRule` is agent-only (`GuardrailEngine.cs:25`).
  - See D12.

### Capabilities and targets

- Existing capabilities:
  - `automation.owner_set`: Admin, `fleet`, RootEquivalent (`CapabilityCatalog.cs:344`);
  - `scheduler.update`: Admin, RootEquivalent (`:322`);
  - `webhook.save`: Admin, RootEquivalent (`:239`);
  - `gitdeploy.save`: Admin, RootEquivalent (`:232`);
  - `aitrigger.save`: Admin, RootEquivalent (`:302`);
  - `endpointcheck.save`: Admin, Reversible (`:298`).
- `[MutatingOperation]` drives the runtime scope guard and `MutationBypassArchitectureTests`
  (`Services/Mutations/MutationAttributes.cs:3-10`). A method carries one such attribute, because `AllowMultiple` is
  not set. The handover therefore marks **new** store methods with the new capability (see "Interfaces").
- Under PC-D1 a person in the UI meets only three rules: principal ceiling, kill switch and protected resources
  (`GuardrailEngine.cs:35-40`). None of the capabilities above is a control-plane capability
  (`ControlPlaneResources.cs:56-69`), so none raises an approval. Contract 0019 does not apply to them.

## Goals

1. An Admin sees, in one place, every owner whose items **cannot be run on that owner** by the handover predicate below.
   This covers a deleted account, a sign-in not yet verified, an address no longer allowed to sign in, a deleted or
   disabled key, an unrecognised id and a level that is too low. The list shows what each such owner still holds.
2. The Admin hands the chosen items of one such owner to one named, usable person in one gateway decision.
3. Nothing runs on the new owner's authority until someone switches the item on (D2). A resume never re-owns (D7).
4. The audit names the old owner, the new owner, the acting Admin, every item with its outcome, and later the person
   who resumed each item, on that item's first run.
5. The old owner's address never gets those items back by signing in again, by any path.

## Non-goals

- No automatic re-linking by e-mail, for SSO or anything else (maintainer decision).
- No change to `OwnerAccounts`, `AutomationOwnerResolver`, `LostAuthority`, `AgentAuthority`, INV-04's re-check at
  every run, or the suspension paths.
- No move of sessions, API keys, approvals, pending approvals, notification settings or agent memory. Only the owner
  field of automation items changes.
- No change to today's per-item takeovers.
- No handover of items without an owner (they are taken over as today), of items the predicate says can still run
  (D4), or of key-owned or key-created items (D12).

## Interfaces

### The handover predicate

```csharp
namespace Whiskers.Services.Mutations;

/// <summary>Whether an item may be handed over: its current owner cannot run it.</summary>
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
```

The predicate takes a different route from `LostAuthority` on purpose:
- `Unverified` counts as `CannotRunOnOwner` from the state, although `LostAuthority` returns `null` for it (`:433`).
- Anything `LostAuthority` would answer with `null` for a reason other than "holds it" is `CannotJudge`: an unknown
  capability (`:414`), a resolver exception (`:424-430`) or the interface default (`:299`).
- The predicate calls `Describe` and `Resolve` itself, in a `try` block, and checks the capability against the catalog
  first.

### Inventory and handover service (new)

```csharp
/// <summary>The kinds of item that run on an owner's authority (INV-04).</summary>
public enum AutomationKind { ScheduledTask, Webhook, GitDeployApp, AiTrigger, EndpointCheck, FleetFeature }

/// <summary>One item: its kind and its id (task id, webhook id, app id, trigger id, check id, or the
/// <see cref="IAutomationOwnerStore"/> feature id).</summary>
public sealed record AutomationItemRef(AutomationKind Kind, string Id);

/// <summary>An item as the dialog shows it. <paramref name="Capabilities"/> are the catalog ids a run needs: a task's
/// per-type capability, a webhook's action, <c>gitdeploy.deploy</c>, a trigger's granted capabilities,
/// <c>endpointcheck.run</c>, or <c>autopatch.run</c> / <c>container.update</c> / <c>vpn.bootstrap</c>.
/// <paramref name="Why"/> is the reason text for <see cref="HandoverEligibility.CannotRunOnOwner"/> (the owner's state,
/// or the <c>LostAuthority</c> text) or for <see cref="HandoverEligibility.CannotJudge"/>.</summary>
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
}

/// <summary>Thrown inside the decision when <see cref="IAutomationHandover.Check"/> fails on re-check. Nothing was
/// written.</summary>
public sealed class HandoverRefusedException(HandoverCheck check) : InvalidOperationException("Handover refused.")
{
    public HandoverCheck Check { get; } = check;
}

public interface IAutomationHandover
{
    /// <summary>Every owner with at least one item the predicate does not clear. Read-only.</summary>
    IReadOnlyList<UnusableOwner> UnusableOwners();

    /// <summary>Every item recorded on <paramref name="ownerId"/>, with its eligibility. Read-only.</summary>
    IReadOnlyList<OwnedAutomation> OwnedBy(string ownerId);

    /// <summary>The rules the handover applies (D3, D4, D12), for the dialog before the click and for the page's
    /// refusal. Read-only. Anything it cannot read or judge counts as refused.</summary>
    HandoverCheck Check(string fromOwnerId, string toOwnerId, IReadOnlyList<AutomationItemRef> items);

    /// <summary>Under one handover lock: runs <see cref="Check"/> again and throws
    /// <see cref="HandoverRefusedException"/> when it fails (nothing written). Otherwise, one item after the other,
    /// calls the item's store method below. A store exception stops the loop: that item is <c>Failed</c> and the rest
    /// are <c>NotAttempted</c>. The partial result is noted on the event (<see cref="MutationScope.Note"/>) and the
    /// exception is rethrown.</summary>
    [MutatingOperation("automation.handover")]
    Task<HandoverResult> HandOverAsync([MutationTarget] string fromOwnerId, string toOwnerId,
        IReadOnlyList<AutomationItemRef> items, CancellationToken ct = default);
}
```

`AutomationHandover` (singleton) implements the interface over the existing stores.

### Store methods (new, each marked `[MutatingOperation("automation.handover")]`)

These are new methods, so the one-attribute limit does not stand in the way. The runtime scope guard and
`MutationBypassArchitectureTests` then accept a call only inside an `automation.handover` decision. They are called
only from `AutomationHandover.HandOverAsync`.

```csharp
public sealed record HandoverWrite(HandoverItemStatus Status, string Name, bool WasOn);   // HandedOver | Skipped*

// ISchedulerService
[MutatingOperation("automation.handover")]
Task<HandoverWrite> HandOverAsync([MutationTarget] string taskId, string fromOwnerId, string toOwnerId, CancellationToken ct = default);
//   CreatedBy := to only while CreatedBy == from (one conditional UPDATE); Enabled := false; CreatedVia unchanged (D5).

// IWebhookService
[MutatingOperation("automation.handover")]
Task<HandoverWrite> HandOverAsync(string webhookId, string fromOwnerId, string toOwnerId, CancellationToken ct = default);
//   Owner := to only while Owner == from; Enabled := false; versioned diff as SetOwnerAsync.

// IGitDeployService
[MutatingOperation("automation.handover")]
Task<HandoverWrite> HandOverAsync(string appId, string fromOwnerId, string toOwnerId, CancellationToken ct = default);
//   Owner := to only while Owner == from; Revision + 1; HeldReason := handover text (D6).

// IAiTriggerStore
[MutatingOperation("automation.handover")]
Task<HandoverWrite> HandOverAsync(string triggerId, string fromOwnerId, string toOwnerId, CancellationToken ct = default);
//   OwnerId := to only while OwnerId == from; Enabled := false; SuspendedReason := null (D7).

// IEndpointCheckStore
[MutatingOperation("automation.handover")]
Task<HandoverWrite> HandOverAsync(string checkId, string fromOwnerId, string toOwnerId, CancellationToken ct = default);
//   OwnerId := to only while OwnerId == from; Enabled := false; SuspendedReason := handover text.

// IAutomationOwnerStore
[MutatingOperation("automation.handover")]
Task<HandoverWrite> HandOverAsync(string feature, string fromOwnerId, string toOwnerId, CancellationToken ct = default);
//   entry := (to, Blocked: true, SuspendedReason: handover text) only while OwnerId == from.
```

`MutationGatewayCoverageTests` and `MutationBypassArchitectureTests` list the six methods under `automation.handover`.

### Resume paths (D7: a resume never re-owns)

| Kind | Resume | Capability | New method? |
|---|---|---|---|
| Scheduled task | existing switch (`ScheduledTasksView.razor:577-582`) | `scheduler.update` | no: the toggle does not touch the owner |
| Webhook | existing switch (`WebhooksView.razor:477`) | `webhook.set_enabled` | no: `SetEnabledAsync` does not touch the owner (`WebhookService.cs:208-222`) |
| AI trigger | existing switch on save | `aitrigger.save` | no: with `SuspendedReason` cleared, switching on is no takeover (`AiTriggerStore.cs:109-111`) |
| Endpoint check | new **Resume** button | `endpointcheck.save` | `IEndpointCheckStore.ResumeAsync(string id, string expectedOwnerId, CancellationToken ct)`, `[MutatingOperation("endpointcheck.save")]`: `Enabled := true`, `SuspendedReason := null`, only while `OwnerId == expectedOwnerId`; the owner is kept |
| Git deploy app | new **Resume** button | `gitdeploy.save` | `IGitDeployService.ResumeAsync(string appId, string expectedOwnerId, CancellationToken ct = default)`, `[MutatingOperation("gitdeploy.save")]`: clears `HeldReason` only while `Owner == expectedOwnerId`; the owner is kept. Saving the app (`SaveAppAsync`) also clears it, but re-owns to the saver as today. |
| Fleet feature | new **Resume as {owner}** button | `automation.owner_set` | `IAutomationOwnerStore.ResumeAsync(string feature, string expectedOwnerId, CancellationToken ct)`, `[MutatingOperation("automation.owner_set")]`: clears the block only while that owner is recorded |

- Before a resume of the last three kinds, the page checks the recorded owner with the run's own judgment. If the
  predicate does not clear the owner, the resume is refused (`RefuseAsync`) and the Admin can only take the item over
  as today.
- Every resume path, inside its decision's delegate, calls `IHandoverLedger.RecordResume` (below).

### Handover ledger (persisted, additive)

```csharp
/// <summary>Items handed over and not yet run since. Whiskers' own bookkeeping, like recording a run: not a
/// mutation, no gateway decision of its own; written only inside the handover and resume decisions.</summary>
public interface IHandoverLedger
{
    /// <summary>Written <b>before</b> any item is changed, with every item as <c>Pending</c>. Throws when the file
    /// cannot be written: the handover is then refused and nothing is changed.</summary>
    void RecordHandover(string operationId, string fromOwnerId, string toOwnerId, string actingAdminId,
        IReadOnlyList<AutomationItemRef> items);
    /// <summary>After each item write: its status in that entry (<c>HandedOver</c>, <c>Skipped*</c>, <c>Failed</c>,
    /// <c>NotAttempted</c>). Only <c>HandedOver</c> items stay in the ledger.</summary>
    void Reconcile(string operationId, AutomationItemRef item, HandoverItemStatus status);
    /// <summary>Called inside a resume decision for an item in the ledger; remembers who resumed it and in which
    /// operation.</summary>
    void RecordResume(AutomationItemRef item, string resumedBy, string operationId);
    /// <summary>For a runner's run of a ledger item: the entry, without removing it. Null otherwise.</summary>
    HandoverEntry? PeekFirstRun(AutomationItemRef item);
    /// <summary>Removes the entry, called only after a run of the item that the gateway <b>executed</b>. A run that was
    /// refused (kill switch, level, policy, held) leaves the entry for the next run.</summary>
    void ConsumeAfterExecuted(AutomationItemRef item, string runOperationId);
    /// <summary>Removes the entry of an item that was deleted.</summary>
    void Forget(AutomationItemRef item);
    /// <summary>Removes entries whose item was handed over and not resumed for <paramref name="maxAge"/> (D14).
    /// Recorded once per pruning as a log line naming the items; the handover event stays in the audit.</summary>
    int Prune(TimeSpan maxAge, DateTime nowUtc);
}

public sealed record HandoverEntry(string HandoverOperationId, string FromOwnerId, string ToOwnerId,
    string HandedOverBy, DateTime HandedOverAtUtc, string? ResumedBy, string? ResumeOperationId);
```

- Stored as `automation-handovers.json` in the data directory, written atomically and flushed (`DurableFile`, R1-35).
  It is a new file, not a schema change.
- **Order inside `HandOverAsync`:**
  1. `Check` again under the lock;
  2. `RecordHandover`, with every item `Pending`. A failure here refuses the whole handover (`HandoverRefusedException`,
     outcome Failed, nothing changed);
  3. each item write, followed by `Reconcile` with its status. If an item write fails after the ledger write, that item
     is reconciled as `Failed` and the rest as `NotAttempted`.
  4. If `Reconcile` itself cannot write, the item write stands (it is off, so safe). The stale `Pending` row is read as
     "not handed over" and dropped at the next ledger write.
- **First run:**
  - Each runner (task executor, webhook delivery, Git deploy, AI trigger, endpoint-check monitor, AutoPatch,
    AutoUpdate, VPN bring-up) calls `PeekFirstRun` before its gateway call. On a hit it adds the note
    `handover = { operationId, from, to, handedOverBy, resumedBy, resumeOperationId }` to the run's event.
  - The runner calls `ConsumeAfterExecuted` only when the outcome is Executed. A Denied run (kill switch, level, policy,
    `AutomationHeld`) keeps the entry, so the first run that really executes names the handover.
- **Pruning:**
  - The delete paths of the six kinds call `Forget` inside their own decisions.
  - A daily pass in the existing retention service calls `Prune` with the age from D14. The recommendation is
    **30 days**, counted from the handover, for entries that were not resumed.
  - A resumed entry is removed by its first executed run, or by `Forget`.
- An unreadable ledger never blocks a run, because it only adds context. The run's event then says
  `handover = unknown (ledger unreadable)`, and the error is logged.

### Capability (new)

| Capability | Level / role | MCP tool | RiskClass | Target | Notes |
|---|---|---|---|---|---|
| `automation.handover` | admin / Admin | none (UI only, D10) | RootEquivalent | new kind `automation-owner`, `ResolvedRecord`, argument key `fromOwner` | Deadline `Short`. Under PC-D1: no approval (D8). |
| `automation.handover_acknowledge` | read / every signed-in user | none (UI only) | Reversible | new kind `handover-notice`, `ResolvedRecord`, argument key `notice` | Deadline `Short`. Own notices only. The service refuses a notice whose `ToOwnerId` is not the caller, past the page and the gateway, as `account.password_change` does. Passes the kill switch like every read-level capability. |
| `automation.hold` (D11 c only) | admin / Admin | none (no page of its own) | Disruptive | `automation-owner`, argument key `fromOwner` | Only nested: `MutationCompositions` gains `("account.create", ["config.roles", "automation.hold"])`; a direct call outside `account.create` is refused by the scope guard. |

- `CapabilityCatalog`:
  - `yield return Ui("automation.handover", A, "automation-owner", Root, Short);`
  - `yield return Ui("automation.handover_acknowledge", McpPermissionLevels.Read, "handover-notice", Rev, Short);`
  - `yield return Ui("automation.hold", A, "automation-owner", Dis, Short);` (D11 c only)
- `IHandoverNotices.AcknowledgeAsync([MutationTarget] string noticeId, CancellationToken ct)` is marked
  `[MutatingOperation("automation.handover_acknowledge")]`.
- `MutationTargets` also maps `"handover-notice" => "notice"`, and `"handover-notice"` joins the `RecordAsync` branch.
- The capabilities are counted in `MutationGatewayCoverageTests`, `CapabilityCatalogTests`,
  `CapabilityTableConsistencyTests`, `docs/v1/CAPABILITY_INVENTORY.md` and `docs/v1/PATH_CAPABILITIES.md` (U-42).
  `automation.hold` is listed under U-42 as nested in `account.create` (U-35).
- `MutationTargets`: `"automation-owner" => "fromOwner"` joins the key map, and `"automation-owner"` joins the
  `RecordAsync` branch (contract 0012, D2: every kind has a branch). The record resolves when `fromOwner` is a
  principal id in the `PrincipalIds` scheme or a stored legacy value. It does not need to exist as an account.
- `CapabilityCatalog.RootEquivalent` grows by one (INV-19's count).
- Arguments: `{ fromOwner, toOwner, items: [ { kind, id } … ] }`. All of them are part of the binding and the audit;
  none is a secret.

### Git deploy hold (D6, persisted, additive)

```csharp
public sealed class GitDeployApp
{
    /// <summary>Set by a handover: the app's deployments are refused until an Admin resumes it. Null otherwise.</summary>
    public string? HeldReason { get; set; }
}
```

- This is a nullable field in the Git deploy JSON store. It is absent in old files and reads as `null`. No database
  change.
- A webhook-fired deployment of a held app is refused before anything is fetched. It is recorded through
  `IAutomationMutationRunner.RefuseAsync` as a Denied `gitdeploy.deploy`, with the new `MutationReasonCodes` entry
  `AutomationHeld`.

### Persisted data

- No new table and no database migration. Tasks, webhooks and endpoint checks already have owner and `Enabled`
  columns.
- Additive, outside the database:
  - `GitDeployApp.HeldReason` in the Git deploy JSON store (D6);
  - the new `automation-handovers.json` (the ledger);
  - for D13 a), the new `automation-handover-notices.json`.
- `automation-owners.json` gets no new field: the handover writes a blocked entry with the new owner and a reason text.

### Refusals and failures (which event says what)

| Where | What | Audit |
|---|---|---|
| Page, before the call | Role below Admin (`RequireAsync`) | `RefuseAsync("automation.handover", args, reason)`: the gateway's Denied event (R1-22). Nothing runs. |
| Page, before the call | `Check(from, to, items)` is not `Ok` | `RefuseAsync("automation.handover", args, <the check's reasons>)`: a Denied event. Nothing runs. |
| Gateway | Level, kill switch, target not resolvable | The gateway's Denied event. The delegate never runs. |
| Inside the delegate | `HandOverAsync` runs `Check` again under the lock and it fails (a state changed between the page's check and the call) | `HandoverRefusedException`. The gateway records the operation's outcome as **Failed**, with the exception type and the reasons noted. Nothing was written. |
| Inside the delegate | A store write throws on item *k* | Items before *k* are `HandedOver`, *k* is `Failed`, the rest are `NotAttempted`. The list is noted before the rethrow, and the outcome is **Failed**. Items written stay off. |
| Inside the delegate | Owner changed or item gone | That item is skipped, the rest proceed, and the outcome is Executed. |

### Notifications

- **To the administrators:** a new event type `automation_handed_over` (`NotificationEventTypes.AutomationHandedOver`).
  - It joins `AdministrativeEvents` and `UnsuppressibleEvents`, like `automation_suspended`
    (`NotificationEventTypes.cs:81`, `:156-159`).
  - Text (DE/EN): "The automation of {from} ({n} items: {counts by kind}) was handed over to {to} by {admin}. Every item
    is off until it is switched on again." No item names, no arguments.
  - It is sent once per executed handover, never for a refused one.
- **To the new owner personally (D13).** Whiskers has no per-user delivery today. E-mail goes to the configured
  recipients only (`EmailNotificationService.cs:57`), and the in-app log is shared (U-39). The draft adds a personal
  in-app notice:
  - `IHandoverNotices` keeps one entry per handover for `toOwnerId` in `automation-handover-notices.json`;
  - the new owner sees a banner on every page after sign-in: "{admin} handed you {n} automations from {from} on {date}.
    They are off until switched on. [Show] [Acknowledge]";
  - the entry stays until they acknowledge it. Acknowledging is the new owner's own read-level bookkeeping, recorded
    as `automation.handover_acknowledge` (read level, own notices only), like `account.password_change`.
  - No message goes to an individual address.

### UI flow

1. *Settings → Automation owners* (`AutomationOwnersPanel`) gets a new section, **Owners who can no longer act**:
   - one row per `UnusableOwner`: name, state (`AutomationOwners_State_*`), counts per kind, and the button
     **Hand over…** (Admins).
   - Links into the section: the owner-revoked alert on *Scheduled tasks* (`ScheduledTasksView.razor:85-95`), the
     equivalent markers on *Webhooks*, *Git deploy*, *AI triggers* and *Endpoint checks*, and a line in the
     account-deletion result: "This account owned {n} automations; they stop now. Hand them over in Settings →
     Automation owners." That line is a count read after the deletion, with no gateway call.
2. The dialog **Hand over automation of {from}** has three steps:
   1. *Items*:
      - grouped by kind, each with name, on/off and why it cannot run now;
      - eligible items are preselected, and the Admin may deselect them (D1);
      - `CannotJudge` items and items with key provenance (D12) are listed greyed with the reason, and cannot be
        selected;
      - items that still run on their owner are not listed (D4).
   2. *New owner*:
      - a list of persons who can act now (`OwnerState.Active`): local accounts, and federated addresses seen signing
        in, that the whitelist admits;
      - keys are not offered (D3);
      - picking one runs `Check`: items the person cannot hold are deselected and greyed with the reason ("needs
        Admin");
      - the old owner and an empty selection cannot be picked.
   3. *Summary*: "{n} items from {from} to {to}. All of them will be **off**; switch each on to resume. Recorded in the
      audit log; {to} is notified." The Admin may be the new owner themselves.
   4. Buttons: **Cancel** (default) and **Hand over** (not the default, not bound to Enter).
3. The page:
   1. copies `toOwner` and the selected `items` into immutable locals;
   2. calls `Check`, and on a failed check calls `RefuseAsync` (see "Refusals and failures");
   3. otherwise calls `Mutations.RunAsync("automation.handover", new { fromOwner, toOwner, items }, async (ResolvedRecord rt,
      CancellationToken ct) => await Handover.HandOverAsync(rt.Id, toOwner, items, ct))`.
   - The delegate takes `toOwner` and `items` from those locals, not from the bound arguments. This is harmless here:
     the arguments and the delegate read the same immutable locals in the same call, and without an approval there is
     no time gap in which they could differ. `HandOverAsync` re-checks everything under its lock.
4. The result lists every item: handed over (was on / was off), skipped, failed or not attempted, each with the reason.
   A snackbar repeats the count, and the section refreshes.
5. Resuming follows the table under "Resume paths".

### Configuration

No new configuration key.

## Security model

### Who may do what

- **Hand over:** Admin only (`automation.handover`, catalog level `admin`, single-sourced per INV-13). Viewers and
  Operators see neither the button nor the dialog. A direct call is refused by the level with a Denied event.
- **Which items (D4, D12):** an item may be handed over only when the predicate says `CannotRunOnOwner` **and** neither
  its owner nor its provenance is an MCP key. `CannotJudge` refuses any handover that names the item.
- **New owner (D3):** must meet all of these:
  - a person (`user:<email>`), `OwnerState.Active` now, and not the old owner;
  - holds **now** the level of every capability every selected item needs, judged as a run would judge it (raised
    levels in Settings → MCP included).
  - The new-owner check is **fail-closed** and does not rely on `LostAuthority` alone. Each of these counts as
    **refused**:
    - an unknown capability id;
    - an exception from the resolver (`Describe` / `Resolve`);
    - a runner whose `LostAuthority` is the interface default (`=> null`, `:299`);
    - an MCP level override (`ICapabilityOverrides.Resolve`) that cannot be read. `LostAuthority` swallows that case
      and keeps the catalog level (`AutomationMutationRunner.cs:440-448`), which could be lower than a raised level.
      The check therefore resolves the override itself and refuses when it throws.
  - The authority is still re-read on every run afterwards (INV-04). There the gateway refuses fail-closed on an
    unreadable override, so a level a handover could not see is still enforced at the run. A handover never fixes a
    level.
- **No synthetic principal:** the handover runs as the signed-in Admin. The new owner does nothing and is not
  impersonated.
- **Rule set (D5, D12):** no handed-over item moves from the agent rule set to the human rule set.
  - An agent-created task keeps `CreatedVia`, so it stays under the agent rules.
  - Key-owned and key-created items are not handed over.
- **MCP permission levels:** `McpPermission.cs` is not touched, and there is no tool (D10).

### Gateway and approval policy

- One gateway decision per handover: `automation.handover` on `automation-owner:<from>`, Admin, human rule set
  (PC-D1).
- The kill switch refuses it: there is no exemption, and contract 0009 is unchanged.
- A protected-resource hit cannot occur, because the target is an owner record.
- **No approval** (D8), as for every existing takeover. The capability is therefore not an INV-59 action and is not in
  any wave of contract 0019.
- The binding holds the items, the old owner and the new owner. A page that changed between render and click is caught
  by `Check` under the lock and by the compare-and-set per item.
- **No auto-confirm:** the click on *Hand over…* only opens the dialog. The decision is the separate, non-default
  **Hand over** button on a summary the dialog built from `Check`.

### Audit

- The gateway event of `automation.handover`: intent and outcome under one operation id. Its details are
  `handover = { from: AuthorityOf(from), to: AuthorityOf(to), items: [ { kind, id, name, wasOn, status } ] }`, using
  `AutomationOwners.AuthorityOf` (`<id> (<state>, <level>)`) as the existing takeovers do.
- Each store's versioned, redacted `ConfigChange` diff, as today. Webhook secrets, Git tokens and AI trigger prompts
  are treated exactly as in their existing diffs.
- Refusals and failures as in the table under "Refusals and failures".
- Each resume is the existing event of its switch or button. **The first run after the handover** names the handover
  and the resumer (the ledger note).
- The new owner's acknowledgement is its own event (D13).

### Secrets

- No secret is read or written. Owner ids (addresses, key ids) and item names are in the audit trail today already.
- No argument, prompt, token, webhook secret or command text goes into a log, a notification, the ledger, the notices
  file or the event details.

### Fail-closed behaviour

| Situation | Behaviour |
|---|---|
| The predicate cannot judge an item (exception, unknown capability, default `LostAuthority`) | `CannotJudge`: not offered; a handover naming it is refused before any write. |
| A store, the account record or the resolver cannot be read during `Check` | Refused before any write. |
| New owner not `Active`, a key, the old owner, or lacking a level for any selected item | Refused before any write (no partial handover by authority). |
| Key owner or key provenance on a selected item | Refused before any write (D12). |
| Item's owner changed or item deleted between render and click | That item is skipped; the rest proceed. |
| A store write fails half-way | `Failed` / `NotAttempted` as in the table; the items written stay handed over **and off**; nothing ran. |
| A suspension races the handover | The suspension is conditional on the old owner and returns "superseded"; the item is off either way. |
| Kill switch on | Refused. Resumes are refused too. |
| Ledger or notices file unreadable | The handover is refused (the ledger write is part of it). A run is never blocked by the ledger; its event says the context is unknown. |
| The old person signs in again by SSO | Handed-over items stay with the new owner. Items not handed over stay as they are today. |
| A local account is re-created for the old address | See D11. |

### INV-04, INV-61, AgentAuthority

- INV-04: unchanged in substance. Every run still runs on the recorded owner, re-read each time. New: an Admin's
  handover may change the recorded owner. A handover always leaves the item off, and a resume never re-owns it.
- INV-61: account deletion is unchanged; its result only shows the count of owned automations. D11 (c) would add a
  step to `account.create`.
- `AgentAuthority`, INV-08: unchanged. A handover moves no session.

## Invariant and §7 impact

### Changed rows (`docs/v1/SECURITY_INVARIANTS.md`)

- **INV-04:** add to the evidence:
  > "Contract 0020: an Admin can hand the items of an owner who cannot run them over to another usable person in one
  > gateway decision (`automation.handover`). Every handed-over item is left off (a Git deploy app held), the owner is
  > replaced only while it is still the old one, a resume never re-owns, and the first run names the handover and the
  > resumer. An address that signs in again never gets the items back."

  Tests:
  - `AutomationHandoverTests.Every_handed_over_item_is_off_and_runs_on_the_new_owner_only_after_resume`;
  - `AutomationHandoverTests.A_returning_sso_sign_in_never_regains_handed_over_items`;
  - `AutomationHandoverTests.The_first_run_after_resume_names_the_handover_and_the_resumer`.
- **INV-19:** the RootEquivalent count grows by one (`automation.handover`). The `RootEquivalentAdminTests`
  membership and `CapabilityTableConsistencyTests` are updated.
- **INV-08, INV-61:** wording unchanged. If D11 (c) is chosen, INV-61 gains: "creating an account for an address that
  still owns automation holds those items in the same decision".

### New row: INV-66

INV-66 is the next free ID after contract 0018's INV-63 and INV-64 and contract 0019's INV-65.

> **INV-66** — An automation item changes owner only by an explicit, interactive Admin act through the Mutation
> Gateway: a takeover by that Admin, or a handover to another person. It never changes owner because an address signs
> in again. A handover:
> - moves only items whose current owner cannot run them (owner not Active, or authority lost), and never an item it
>   cannot judge;
> - never moves an item owned or created by an MCP key;
> - moves items only to a person who can act now and holds every capability the items need;
> - changes each item only while its owner is still the old one;
> - leaves every item off (a Git deploy app held) until someone switches it on, and a resume never re-owns the item.
>
> The handover is audited with the old owner, the new owner, the acting Admin and every item. The item's first run
> afterwards names the handover and the resumer. There is no MCP or agent path.
>
> Tests:
> - `AutomationHandoverTests`;
> - `AutomationHandoverWireTests`;
> - `AutomationHandoverDialogTests`;
> - `AutomationHandoverArchitectureTests.The_store_handover_methods_are_marked_and_called_only_inside_automation_handover`.

Contract 0020 uses **one** new INV row (INV-66). Contract 0021 starts at INV-67.

### Release gate

- **Rule R5:** INV-66 and the INV-04 / INV-19 additions land in the **implementing PR**, together with the tests they
  name, and those tests must exist and pass in that PR. A row whose tests are not there yet is not ✅. It is then listed
  under *Open invariants* in `RELEASE_GATE.md` with its package. From 1.0.0 on, such an open row also needs a
  reference: a GHSA id or an issue URL of this repository, unless an exception has been accepted
  (`src/Whiskers.Tests/ReleaseGate/ReleaseGate.cs:212-213`).
- **`docs/v1/RELEASE_GATE.md:36`** reads "Highest invariant ID: INV-62" today.
  - It becomes **INV-66** once 0018 (INV-63/64), 0019 (INV-65) and 0020 (INV-66) have landed.
  - **Rule R3** (`ReleaseGate.cs:299-305`) refuses any ID at or below the pin that is neither a row nor retired, and
    any row above the pin. **Contract 0020 is implemented after 0018 and 0019 have landed INV-63 to INV-65.** If it
    lands first, its row takes the next free ID at that time (then INV-63), and this contract, 0018, 0019 and 0021 are
    renumbered in the same PR. It never leaves a gap.
  - Each implementing PR raises the pin to its own highest row in the same PR.
- **`docs/v1/SECURITY_INVARIANTS.md` §11** (`:144-152`, today ✅ 61, ⚠️ 1, total 62):
  - 0020 adds one row: total +1, and ✅ +1 when INV-66 lands ✅.
  - After 0018, 0019 and 0020 the total is **66**. The ✅ count is 61 plus every new row that lands ✅.

### `docs/v1/PRODUCT_CONTRACT.md`

- **§6:** add under the decisions: "Contract 0020: no re-linking of automation by e-mail; an Admin hands the automation
  of an unusable owner over (U-42)."
- **§7.5, new row U-42:**
  > "Hand over the automation of an owner who can no longer act (*Settings → Automation owners*,
  > `automation.handover`, contract 0020); the new owner acknowledges the notice (`automation.handover_acknowledge`, own
  > notices) | Admin; acknowledge: read level, the notice's own addressee only | `NotVerifiable` + reason (Whiskers' own
  > records) | GW ✅ (one event: old and new owner with authority, every item with its outcome; each store's versioned
  > diff; the acknowledgement is its own event) | INV-04, 19, 66"
  - With D11 (c), U-35 (`account.create`) also gets: "holds every automation still recorded on the address before the
    account row exists (nested `automation.hold`, contract 0020)".
  - §7.6 counts U-42 as **one** row, because both capabilities are on the same page. `AuditPathMatrixTests` and
    `PATH_CAPABILITIES.md` map U-42 to `automation.handover` and `automation.handover_acknowledge`, and U-35 to
    `automation.hold` (with c).
- **Notes on existing rows:**
  - U-26, U-28, U-29, U-31, U-38 and U-34: "items can also be handed over (U-42); a resume never re-owns".
  - U-29 also: "Resume (`gitdeploy.save`) clears a handover hold".
  - U-38 also: "Resume (`endpointcheck.save`) keeps the owner".
  - U-34 also: "Resume as {owner} (`automation.owner_set`)".
- **§7.1–§7.4:** unchanged. The S-rows of the runners note the first-run ledger context, and the Git deploy webhook row
  notes the `AutomationHeld` refusal.
- **§7.6:** U 41 → 42, total 116 → **117** (counted after 0018 and 0019).
- `docs/v1/PATH_CAPABILITIES.md`: new row U-42 → `automation.handover`, `automation.handover_acknowledge`.
- `docs/v1/CAPABILITY_INVENTORY.md`: new rows `automation.handover` and `automation.handover_acknowledge`.

## Test plan

### Unit (`src/Whiskers.Tests`)

**`AutomationHandoverTests`** (real stores on an in-memory database and a temp data directory, fake resolver):
- `Unusable_owners_lists_deleted_unverified_disallowed_key_and_demoted_owners_with_counts`
- `An_unverified_owner_is_eligible_although_LostAuthority_is_null`
- `An_unknown_capability_or_a_resolver_exception_makes_the_item_CannotJudge_and_refuses_the_handover`
- `A_runner_with_the_default_LostAuthority_is_CannotJudge`
- `An_owner_whose_items_can_still_run_is_not_listed_and_its_items_are_refused`
- `Every_handed_over_item_is_off_and_runs_on_the_new_owner_only_after_resume`
- `An_item_that_was_on_is_switched_off_and_reported_as_was_on`
- `A_new_owner_without_the_level_for_one_item_refuses_the_whole_handover_and_writes_nothing`
- `A_key_the_old_owner_or_an_inactive_person_cannot_be_the_new_owner`
- `The_new_owner_check_refuses_on_resolver_exception_and_unknown_capability`
- `A_key_owned_task_or_webhook_and_a_key_created_task_are_refused` (D12)
- `An_unreadable_account_record_refuses_the_handover`
- `An_item_taken_over_meanwhile_is_skipped_and_the_rest_proceed`
- `A_deleted_item_is_skipped`
- `A_store_failure_half_way_reports_failed_and_not_attempted_and_leaves_written_items_off`
- `A_recheck_failure_inside_the_delegate_writes_nothing_and_records_failed`
- `A_suspension_racing_the_handover_is_superseded`
- `Scheduled_task_provenance_is_unchanged_and_an_agent_created_task_keeps_the_agent_rule_set` (D5)
- `A_handed_over_git_app_is_held_and_its_webhook_deployment_is_refused_as_AutomationHeld` (D6)
- `Resume_never_re_owns_for_each_kind` (D7: task, webhook, trigger, endpoint check, Git app, fleet feature; resumed by
  an Admin other than the new owner)
- `Resume_is_refused_when_the_recorded_owner_cannot_run_the_item`
- `The_first_run_after_resume_names_the_handover_and_the_resumer`
- `A_returning_sso_sign_in_never_regains_handed_over_items`
- `The_event_names_old_and_new_owner_with_authority_and_every_item`
- `The_admin_notification_names_counts_only_and_the_new_owner_gets_a_personal_notice_until_acknowledged` (D13)
- `The_ledger_is_written_before_any_item_and_a_ledger_failure_changes_nothing`
- `An_item_failure_after_the_ledger_write_is_reconciled_as_failed`
- `A_denied_first_run_keeps_the_ledger_entry_and_the_first_executed_run_consumes_it`
- `Deleting_a_handed_over_item_forgets_its_entry_and_unresumed_entries_age_out_after_30_days` (D14)
- `Acknowledge_is_refused_for_another_persons_notice_and_passes_the_kill_switch`
- `The_acknowledge_capability_is_read_level_reversible_on_handover_notice` (catalog and inventory consistency)
- D11 (c) only: the five tests listed under D11.

**`AutomationHandoverDialogTests`** (bUnit):
- `Eligible_items_are_preselected_and_can_be_deselected`
- `CannotJudge_and_key_provenance_items_are_greyed_and_not_selectable`
- `Items_the_new_owner_cannot_hold_are_greyed_with_the_reason`
- `Cancel_is_default_and_Enter_hands_over_nothing`
- `A_failed_check_is_refused_before_RunAsync_with_a_denied_event`
- `Non_admins_see_no_hand_over_button`

**`AutomationHandoverArchitectureTests`:**
- `The_store_handover_methods_are_marked_and_called_only_inside_automation_handover`
- `The_resume_methods_are_marked_with_their_kinds_capability`
- `No_mcp_tool_or_agent_tool_maps_to_automation_handover`
- The existing `MutationBypassArchitectureTests`, `MutationGatewayCoverageTests` and `CapabilityCatalogTests` stay
  green with the new rows.

### Integration (wire tests, `[Collection("WebAppBoot")]`)

**`AutomationHandoverWireTests`** (real gateway, stores, `OwnerAccounts` with a mixed-sign-in configuration):
- `A_deleted_local_owner_signing_in_by_oidc_stays_deleted_and_a_handover_moves_task_webhook_trigger_check_and_autopatch`
- `The_kill_switch_refuses_a_handover_and_writes_nothing`
- `An_operator_is_refused_by_level_with_a_denied_event`
- `After_resume_the_next_scheduled_run_executes_as_the_new_owner_names_the_resumer_and_is_refused_after_their_demotion`

### Real application `[braucht Nutzer/Hardware]`

1. Delete a local test account that owns a scheduled task, a webhook and AutoPatch. Sign in with the same address
   through the IdP: everything stays suspended.
2. Hand the three items over to a second Admin in *Settings → Automation owners*. Check that:
   - all three are off;
   - the audit log shows one event with both owners and three items;
   - the administrative notification arrives;
   - the second Admin sees the personal notice after signing in.
3. A third Admin switches the task on and uses **Resume as {owner}** for AutoPatch. The owners stay unchanged. The next
   runs show the new owner, and their events name the handover and the third Admin as resumer.

## Plan triage

- **[Claude-prüfbar]**
  - the predicate, the service, the six marked store methods, the three resume methods, the ledger, the notices;
  - the capability and target kind, the reason code, the Git deploy hold;
  - the panel section and dialog, the banner, DE/EN texts;
  - every test above;
  - the docs: INV-04, INV-19, INV-66, §11, the `RELEASE_GATE.md` pin, §6, §7 U-42 and the notes, PATH_CAPABILITIES,
    CAPABILITY_INVENTORY, `Services/Mutations/README.md`, `Components/Shared/README.md`, CHANGELOG.
- **[braucht Nutzer/Hardware]** the three checks in the real application above.

## Decisions for the maintainer

- **D1 — All at once or per item.**
  - Options:
    - (a) every item of the owner, no choice;
    - (b) one item per call;
    - (c) one call for a chosen set, all eligible items preselected.
  - **Recommendation: (c).**
  - Reason: one owner, one decision and one event is what the reader of the audit wants. Deselecting lets the Admin
    delete or leave an item instead of handing it on.
- **D2 — State after the handover.**
  - Options:
    - (a) every item off (Git deploy apps held, fleet features blocked) until switched on;
    - (b) keep each item's current state;
    - (c) restore the state before the suspension.
  - **Recommendation: (a).**
  - Reason: the new owner's authority is never used unless someone decides to run the item under it. Under (b), items
    that were never suspended would start at once. The result and the event say which items were on.
- **D3 — Who can be the new owner.**
  - Options:
    - (a) a person who can act now and holds every needed level now;
    - (b) Admins only;
    - (c) persons or MCP keys.
  - **Recommendation: (a)**, with the fail-closed check described under "Who may do what".
  - Reason: an Operator may receive Operator-level tasks, and nobody receives what they could not run. A key is not an
    accountable successor.
- **D4 — Which items may be handed over.**
  - Options:
    - (a) only items for which the predicate says `CannotRunOnOwner` (owner not Active, or authority lost; `Unverified`
      included);
    - (b) every item of an owner whose state is not `Active`;
    - (c) every item of anyone (offboarding before deletion).
  - **Recommendation: (a).**
  - Reason: it covers deleted, unverified, disallowed, key-less and demoted owners, and it never takes a working item
    from a working person. Offboarding means: delete the account, then hand over. In between, the items are refused,
    which is safe.
- **D5 — Provenance of scheduled tasks.**
  - Options:
    - (a) leave `CreatedVia` as recorded;
    - (b) set it to the new owner, as today's takeover does.
  - **Recommendation: (a).**
  - Reason: the new owner never reviewed the task, and an agent-created task keeps the agent's rules (R1-04). Key
    provenance is handled by D12.
- **D6 — Git deploy apps have no switch.**
  - Options:
    - (a) the additive `HeldReason`: deployments are refused until **Resume**;
    - (b) accept that the next push deploys under the new owner;
    - (c) hand over only apps whose webhooks are off.
  - **Recommendation: (a).**
  - Reason: it is the only way D2 holds for Git deploy.
- **D7 — A resume never re-owns.**
  - Today three paths re-own on resume: saving an endpoint check (`EndpointCheckStore.cs:117`), saving a Git app
    (`GitDeployService.cs:167`), and a fleet **Take over** (`AutomationOwnersPanel.razor:126`).
  - Options:
    - (a) dedicated resume methods for those three that keep the recorded owner, the AI trigger kept by clearing its
      `SuspendedReason`, any Admin may resume, and the resumer is recorded on the first run;
    - (b) only the new owner may resume; another Admin who resumes takes the item over, as today.
  - **Recommendation: (a).**
  - Reason: under (b) a colleague switching an item back on would silently become its owner, which defeats the
    handover. (a) keeps "who answers for it" (the owner) apart from "who switched it on" (the resumer), and records
    both.
- **D8 — Approval.**
  - Options:
    - (a) none, as for every takeover (PC-D1);
    - (b) an Admin approval. That needs a new rule, or listing the capability in `ControlPlaneResources`, which it is
      not. The approval could then be given in the dialog under contract 0019.
  - **Recommendation: (a).**
  - Reason: the act adds no authority anywhere. The new owner already holds every needed level, and nothing runs until
    switched on. The capability is not part of any 0019 wave.
- **D9 — Administrative notification.**
  - Options:
    - (a) administrative, never held, counts only;
    - (b) none (the audit is enough).
  - **Recommendation: (a).**
- **D10 — MCP exposure.**
  - Options:
    - (a) UI only;
    - (b) an Admin MCP tool.
  - **Recommendation: (a).**
  - Reason: this is a rare, interactive personnel decision.
- **D11 — Re-creating a local account with the old address (finding).**
  - Today `account.create` for a deleted owner's address revives every item still recorded on it:
    - items that were never suspended run again at their next schedule or event;
    - Git deploy apps deploy at the next push;
    - suspended items stay off.
  - Options:
    - (a) keep the behaviour, and show in the `account.create` dialog "This address still owns {n} automations ({m}
      not suspended); they will run on this account again", with a link to the handover;
    - (b) a tombstone: a deleted address stays `Deleted` for its old items even after re-creation. This needs a
      creation time on every item kind or a per-item generation. Because owner ids are addresses, the person's new
      items need the generation to tell them apart;
    - (c) within the same `account.create` decision, every item still recorded on the address is switched off or held
      (as by D2), recorded in the ledger, and listed in the event. The re-created person or an Admin resumes them by
      the D7 paths.
  - **Recommendation: (c).**
  - Reason: it closes the silent revival without a tombstone and without locking out the person's new items.
  - (a) is the smallest change; (b) is the strictest. The maintainer chooses.
  - **How (c) is built.** The handover methods are not reused: `Check` refuses `from == to`, and `HandOverAsync` stays
    callable only from `AutomationHandover`.
    - Each of the six stores gains `Task<HandoverWrite> HoldAsync(string itemId, string ownerId, CancellationToken ct)`,
      marked `[MutatingOperation("automation.hold")]`. It switches the item off (Git app: `HeldReason`; fleet feature:
      blocked) only while its owner is still `ownerId`, and leaves the owner as it is.
    - `IAutomationHold.HoldAllAsync(string ownerId, CancellationToken ct)` calls them for every item recorded on
      `user:<email>`. Each held item is recorded in the ledger (as a handover with `from == to`, acting Admin = the
      creator), and the list goes into the `account.create` event.
    - The capability `automation.hold` is nested only: `MutationCompositions` gets
      `("account.create", ["config.roles", "automation.hold"])` (today `["config.roles"]`, `MutationCompositions.cs:61`).
  - **Order.** `OwnerAccounts.StateOf` reads the Identity table directly (`OwnerAccounts.cs:89`). So the address
    becomes `Present` as soon as `_users.CreateAsync` has saved the row (`LocalAccountService.cs:157`), even before
    `RememberLocalAccounts` (`:199`). `HoldAllAsync` therefore runs inside `CreateAsync` under `AdminGate`, **before
    `:157`**, and so also before `:199`. If the hold fails, the account is not created: the decision fails before any
    account row exists.
  - **Rollback.** If `account.create` fails after the hold (role write or whitelist entry, `LocalAccountService.cs:176-214`), the account is
    deleted again but the items **stay held**. This is safe: they are off, owned by an address that is `Deleted` again
    (as before), and can be resumed or handed over through the normal paths. Nothing runs. The `account.create`
    outcome event lists the held items, so the state is on record.
  - Tests (only with c):
    - `Creating_an_account_for_an_address_with_owned_items_holds_them_before_the_identity_row_exists`
    - `A_hold_failure_creates_no_account`
    - `A_rolled_back_account_create_leaves_the_items_held_and_nothing_runs`
    - `HoldAsync_is_called_only_inside_account_create` (architecture)
    - `A_held_item_resumed_after_re_creation_keeps_the_re_created_owner_and_names_the_resumer`
- **D12 — Items owned or created by an MCP key (from review).**
  - Facts:
    - key-owned and key-created scheduled tasks exist;
    - webhook and Git app rows from before contract 0001 may hold a key owner;
    - handing either to a person would move its runs from the agent rule set (`CreateDefault`, with exposure
      confirmation) to the human one (`CreateForHumans`), see "Which rule set".
  - Options:
    - (a) exclude them: they are shown greyed and handled with today's per-item takeover;
    - (b) carry the provenance: extend `CreatedByAgent` (`MutationPolicy.cs:117-120`) so that a `key:` provenance also
      keeps the agent rule set, and add an additive `CreatedVia` to webhooks and Git apps, written by the handover.
  - **Recommendation: (a)** for 1.0.
  - Reason: it is fail-closed and small. (b) changes the policy's engine selection, which is a decision of its own.
  - Today's takeover of such an item already moves it to the human rule set under the clicking Admin. That is unchanged
    here and worth a separate look.
- **D13 — Notifying the new owner personally (from review).**
  - Options:
    - (a) a personal in-app notice (banner until acknowledged, `automation-handover-notices.json`);
    - (b) an e-mail to the new owner's address, a new per-person outbound path;
    - (c) only the administrative notification.
  - **Recommendation: (a).**
  - Reason: the new owner learns of it on their next visit, and acknowledging is on record. There is no new outbound
    path, and no address beyond the configured recipients receives mail.
- **D14 — Ledger age (from review).**
  - The ledger only adds context to the first run. An entry for an item that is never resumed would stay forever.
  - Options:
    - (a) age out entries that were not resumed after 7 days;
    - (b) after 30 days;
    - (c) after 90 days;
    - (d) never.
  - **Recommendation: (b), 30 days.**
  - Reason: it covers a holiday or a month-end before someone resumes the item. The handover itself stays in the audit
    trail whatever the age, so pruning loses only the note on a run that comes later. Resumed entries are not aged:
    they are removed by their first executed run or when the item is deleted.

## Out of scope

- Re-linking by e-mail on any path (decided against).
- Items without any owner (taken over as today), and items whose owner can still run them (D4).
- Key-owned or key-created items (D12 a).
- Moving sessions, keys, approvals, notification preferences or agent memory.
- A tombstone for re-created local accounts (D11 b), unless chosen.
- MCP or agent access to the handover (D10).
- Changing the existing per-item takeovers. The rule-set observation for key-owned items in D12 is noted, not changed.
