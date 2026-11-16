# Contract 0020 — Handing over the automation of an unusable owner

Status: **Draft — awaiting the maintainer's acceptance (2026-11-16).** The direction is the maintainer's decision of
16 November 2026 and is not open here: **no automatic re-linking by e-mail**; an Admin hands the automation of an owner
who can no longer act over to another existing, usable account, explicitly, through the Mutation Gateway, audited and
interactive. The open points are listed under "Decisions for the maintainer".

Scope:
- **Protected areas:** none touched. The auth middleware and its order, the OAuth whitelist, the mTLS/WireGuard layer
  and the docker-socket-proxy verb whitelist stay as they are. Sign-in, federated binding (contract 0003) and
  `OwnerAccounts`' verdict do not change; this contract adds a way to move items **away** from an owner, not a way to
  give an owner back its authority.
- **Surfaces:** the web UI only (`MutationSurface.Ui`, `IUiMutationRunner`). No MCP tool, no agent tool, no automation
  path (D10).

## Purpose

An owner's local account is deleted. The same person later signs in through Google or OIDC with the same address.
Everything they owned stays refused or suspended, and today the only way back is a takeover **by the Admin who clicks**,
item by item and page by page. Often the right new owner is somebody else (the colleague who inherits the service), and
nobody can see in one place what the departed owner still held.

The goal: one Admin action, per departed owner, that lists every automation still recorded on that owner and hands the
chosen items to a named, usable account. Nothing runs on the new owner's authority until someone switches it on.

## Current state (verified at `ccd5137e`)

### The verdict on a deleted owner

- `OwnerAccounts.StateOf` (`Services/Mutations/OwnerAccounts.cs:79-96`): with a local row present the owner is
  `Present` (`:89`). Without one, an address that **ever had** a local account is `Deleted` (`:90`) — also when it has
  been seen signing in federated since, because the local record is checked before the federated one (`:90` before
  `:92-94`). With local accounts only, any missing row is `Deleted`. An unreadable store gives `Unverified` (`:85`).
- `AutomationOwnerResolver.UserState` (`Services/Mutations/AutomationMutationRunner.cs:218-224`) maps that to
  `OwnerState.AccountDeleted` / `Unverified`; `Resolve` (`:239-268`) then gives the owner `read`
  (`:260`), so every mutation of the item is refused (INV-04).
- `AgentAuthority` (`Services/Agent/AgentAuthority.cs:29-34`, `UserNow`) reads users through the same resolver: a
  running agent session of the deleted user stops (INV-08). An AI trigger's run is re-checked per mutation by the
  gateway (R1-13).

**Confirmed as described.** A federated sign-in with the same address never brings the authority back.

**Contradicting detail (finding, D11):** re-creating a **local** account with the same address does bring it back.
`StateOf` checks the local rows first (`OwnerAccounts.cs:89`), and `account.create` calls `RememberLocalAccounts`
(`Services/Auth/LocalAccountService.cs:199`). So an `account.create` for the old address silently revives every item
still recorded on it, including Git deploy apps, which have no on/off switch. That is an explicit, audited Admin act, but
not one that says what it revives.

### What an unusable owner holds, and what happens to it

| Kind | Owner field | On authority loss | Today's takeover (to the clicking Admin) |
|---|---|---|---|
| Scheduled task | `ScheduledTaskEntity.CreatedBy` (+ `CreatedVia`) | switched off, `automation_suspended` (`Services/Scheduler/TaskExecutor.cs:84-91`, `:558-563`) | `scheduler.update` → `SchedulerService.SetOwnerAsync` (`:325-338`), conditional on the old owner; sets `CreatedVia` to the new owner; `Enabled` untouched — "stays off" (`Components/Pages/ScheduledTasksView.razor:600-611`); bulk "Adopt all" (`:622-628`) |
| Webhook | `WebhookEntity.Owner` | switched off, audited `webhook.suspended` (`Services/Webhooks/WebhookService.cs:1346-1352`, `:1502-1545`) | `webhook.save` → `WebhookService.SetOwnerAsync` (`:103-130`), compare-and-set; `Enabled` untouched (`Components/Pages/WebhooksView.razor:331-362`) |
| Git deploy app | `GitDeployApp.Owner` | **no switch**: each webhook-fired deployment is refused (R1-08) | `gitdeploy.save` → `GitDeployService.SetOwnerAsync` (`:199-218`), bumps `Revision` (`Components/Pages/GitDeployView.razor:337-370`) |
| AI trigger | `AiTrigger.OwnerId` + `SuspendedReason` (`Models/AiTrigger.cs:31-39`) | switched off with a reason (`AiTriggerStore.SuspendAsync`) | `aitrigger.save`: switching a suspended or ownerless trigger **on** makes the editor its owner (`Services/Agent/Triggers/AiTriggerStore.cs:107-117`) |
| Endpoint check | `EndpointCheckEntity.OwnerId` + `SuspendedReason` | switched off with a reason (`Services/EndpointChecks/EndpointCheckMonitor.cs:166-169`, `:218-240`) | `endpointcheck.save`: **every** save makes the saver the owner and clears the suspension (`Services/EndpointChecks/EndpointCheckStore.cs:16-20`, `:115-117`) |
| AutoPatch, AutoUpdate, VPN bring-up | `automation-owners.json` (`AutomationOwnerStore`) | blocked with a reason (`AutoPatchService.cs:288-310`, `AutoUpdateService.cs:242-249`, `VpnBootstrapHostedService.cs:147-151`) | `automation.owner_set` in *Settings → Automation owners* (`Components/Shared/AutomationOwnersPanel.razor:104-146`); `SetAsync` clears the block, so the feature **runs again at once** (`AutomationOwnerStore.cs:97-102`) |

Every takeover is to the signed-in Admin only, one item (or one page) at a time, each through the gateway with the
previous and new owner in the event (`MutationScope.Note`, `AutomationOwners.AuthorityOf`,
`AutomationMutationRunner.cs:155-162`). Suspensions are conditional on the owner they read, so a takeover in between
wins (`AutomationOwnerStore.cs:104-118`, `EndpointCheckStore.cs:131-140`).

There is no list of "owners who can no longer act", and the account-deletion dialog
(`Components/Shared/LocalAccountsPanel.razor:179-180`) says nothing about what the account owns.

### Capabilities and targets

- `automation.owner_set` (Admin, `fleet`, RootEquivalent, `CapabilityCatalog.cs:344`), `scheduler.update` (Admin,
  RootEquivalent, `:322`), `webhook.save` (Admin, RootEquivalent, `:239`), `gitdeploy.save` (Admin, RootEquivalent,
  `:232`), `aitrigger.save` (Admin, RootEquivalent, `:302`), `endpointcheck.save` (Admin, Reversible, `:298`).
- A store method carries **one** `[MutatingOperation]` (`MutationAttributes.cs:10`, no `AllowMultiple`), so a new
  capability needs store methods of its own.
- Under PC-D1 a person in the UI meets only principal ceiling, kill switch and protected resources
  (`GuardrailEngine.cs:35-40`). None of the capabilities above is a control-plane capability
  (`ControlPlaneResources.cs:56-69`), so none raises an approval; contract 0019 does not apply to them.

## Goals

1. An Admin sees, in one place, every owner who can no longer act (deleted account, sign-in not verified, no longer
   allowed to sign in, key deleted or disabled, unrecognised id, level too low) with what each still holds.
2. The Admin hands the chosen items of one such owner to one named, usable account in one gateway decision.
3. Nothing runs on the new owner's authority until someone switches the item on (D2).
4. The audit names the old owner, the new owner, the acting Admin and every item with its outcome.
5. The old owner's address never gets those items back by signing in again, by any path.

## Non-goals

- No automatic re-linking by e-mail, for SSO or anything else (maintainer decision).
- No change to `OwnerAccounts`, `AutomationOwnerResolver`, `AgentAuthority`, INV-04's re-check at every run, or the
  suspension paths.
- No move of sessions, API keys, approvals, pending approvals, notification settings or agent memory. Only the owner
  field of automation items.
- No change to today's per-item takeovers; they stay as they are.
- No handover of items without an owner (they are taken over as today), and no handover of items whose owner can still
  run them (D4).

## Interfaces

### Inventory and handover service (new)

```csharp
namespace Whiskers.Services.Mutations;

/// <summary>The kinds of item that run on an owner's authority (INV-04).</summary>
public enum AutomationKind { ScheduledTask, Webhook, GitDeployApp, AiTrigger, EndpointCheck, FleetFeature }

/// <summary>One item: its kind and its id (task id, webhook id, app id, trigger id, check id, or the
/// <see cref="IAutomationOwnerStore"/> feature id).</summary>
public sealed record AutomationItemRef(AutomationKind Kind, string Id);

/// <summary>An item as the dialog shows it. <paramref name="Capabilities"/> are the catalog ids a run needs (a task's
/// per-type capability, a webhook's action, <c>gitdeploy.deploy</c>, a trigger's granted capabilities,
/// <c>endpointcheck.run</c>, <c>autopatch.run</c> / <c>container.update</c> / <c>vpn.bootstrap</c>).
/// <paramref name="CannotRunBecause"/> is <see cref="IAutomationMutationRunner.LostAuthority"/> for its current
/// owner; null means it can still run and is not offered.</summary>
public sealed record OwnedAutomation(AutomationItemRef Ref, string Name, bool On, string? SuspendedReason,
    IReadOnlyList<string> Capabilities, string? CannotRunBecause);

/// <summary>An owner who holds at least one item that cannot run on their authority.</summary>
public sealed record UnusableOwner(string OwnerId, OwnerDescription Description,
    IReadOnlyDictionary<AutomationKind, int> Counts);

public enum HandoverItemStatus
{
    /// <summary>The owner was replaced and the item is off (D2).</summary>
    HandedOver,
    /// <summary>Not changed: its owner is no longer <c>fromOwnerId</c> (someone took it over meanwhile).</summary>
    SkippedOwnerChanged,
    /// <summary>Not changed: the item was deleted meanwhile.</summary>
    SkippedGone,
}

public sealed record HandoverItemResult(AutomationItemRef Ref, string Name, bool WasOn, HandoverItemStatus Status);
public sealed record HandoverResult(string FromOwnerId, string ToOwnerId, IReadOnlyList<HandoverItemResult> Items);

/// <summary>Why <paramref name="ToOwnerId"/> cannot take an item; empty when it can take all of them.</summary>
public sealed record HandoverCheck(IReadOnlyDictionary<AutomationItemRef, string> Refused, string? OwnerRefused);

public interface IAutomationHandover
{
    /// <summary>Every owner with at least one item that cannot run on their authority. Read-only.</summary>
    IReadOnlyList<UnusableOwner> UnusableOwners();

    /// <summary>Every item recorded on <paramref name="ownerId"/>. Read-only.</summary>
    IReadOnlyList<OwnedAutomation> OwnedBy(string ownerId);

    /// <summary>The same rules the handover applies, for the dialog to show before the click (D3, D4). Read-only.
    /// Anything it cannot read counts as refused.</summary>
    HandoverCheck Check(string fromOwnerId, string toOwnerId, IReadOnlyList<AutomationItemRef> items);

    /// <summary>Re-applies <see cref="Check"/> and refuses the whole call when any item or the new owner is refused —
    /// nothing is written then. Otherwise, under one handover lock, for each item: replace the owner only while it is
    /// still <paramref name="fromOwnerId"/> (compare-and-set) and leave the item off (D2). Records the event details
    /// and each store's versioned diff (R1-22).</summary>
    [MutatingOperation("automation.handover")]
    Task<HandoverResult> HandOverAsync([MutationTarget] string fromOwnerId, string toOwnerId,
        IReadOnlyList<AutomationItemRef> items, CancellationToken ct = default);
}
```

`AutomationHandover` (singleton) implements it over the existing stores. Each store gains one conditional method,
called only by `AutomationHandover` inside the `automation.handover` decision (the architecture test enforces it):

```csharp
// Every store: Task<HandoverWrite> HandOverAsync(string itemId, string fromOwnerId, string toOwnerId, CancellationToken ct)
// ISchedulerService — owner replaced only while CreatedBy == from; Enabled := false; CreatedVia unchanged (D5).

// IWebhookService — Owner replaced only while Owner == from; Enabled := false.
// IGitDeployService — Owner replaced only while Owner == from; Revision + 1 (as SetOwnerAsync); HeldReason set (D6).
// IAiTriggerStore — OwnerId replaced only while OwnerId == from; Enabled := false; SuspendedReason := null (D7).
// IEndpointCheckStore — OwnerId replaced only while OwnerId == from; Enabled := false; SuspendedReason := handover text.
// IAutomationOwnerStore — entry replaced only while OwnerId == from: (to, Blocked: true, SuspendedReason: handover text).
```

Each returns one shape:

```csharp
public sealed record HandoverWrite(HandoverItemStatus Status, string Name, bool WasOn);
```

The methods are **not** marked `[MutatingOperation]` themselves; they are internal to the handover's composition
(`MutationCompositions`), like the nested store writes of other compositions, and the coverage test lists them under
`automation.handover`.

### Capability (new)

| Capability | Level / role | MCP tool | RiskClass | Target | Notes |
|---|---|---|---|---|---|
| `automation.handover` | admin / Admin | — (UI only, D10) | RootEquivalent | new kind `automation-owner`, `ResolvedRecord`, argument key `fromOwner` | Deadline `Short`. Under PC-D1: no approval (D8). |

- `CapabilityCatalog`: `yield return Ui("automation.handover", A, "automation-owner", Root, Short);`
- `MutationTargets`: `"automation-owner" => "fromOwner"` in the key map; `"automation-owner"` joins the
  `RecordAsync` branch (contract 0012, D2: every kind has a branch). The record resolves when `fromOwner` is a
  principal id in the `PrincipalIds` scheme or a stored legacy value; it does not need to exist as an account.
- `CapabilityCatalog.RootEquivalent` grows by one (INV-19's count).
- Arguments: `{ fromOwner, toOwner, items: [ { kind, id } … ] }`. All are part of the binding and the audit; none is a
  secret.

### Fleet-feature resume (D7)

`AutomationOwnersPanel` gains, on a feature blocked with a handover reason, a **Resume as {owner}** button for Admins.
It runs the existing `automation.owner_set` with `owner = <the recorded owner>`, through a new conditional
`IAutomationOwnerStore.ResumeAsync(feature, expectedOwnerId)` (marked `[MutatingOperation("automation.owner_set")]`)
that clears the block only while that owner is still recorded. The gateway checks the recorded owner's authority for
the feature's run capability first; refused when the owner cannot run it. The existing **Take over** button stays.

### Git deploy hold (D6, persisted, additive)

```csharp
public sealed class GitDeployApp
{
    /// <summary>Set by a handover: the app's deployments are refused until an Admin resumes it. Null otherwise.</summary>
    public string? HeldReason { get; set; }
}
```

- JSON store field, nullable, absent in old files (reads as null). No database change.
- A webhook-fired deployment of a held app is refused before anything is fetched, recorded through
  `IAutomationMutationRunner.RefuseAsync` as `gitdeploy.deploy` Denied with reason code `AutomationHeld` (new
  `MutationReasonCodes` entry).
- **Resume** on *Git deploy* runs `gitdeploy.save` (existing) and clears it; saving the app clears it as well.

### Persisted data

- No new table, no database migration: tasks, webhooks and endpoint checks already have owner and `Enabled` columns.
- `GitDeployApp.HeldReason` in the Git deploy JSON store (additive, D6).
- `automation-owners.json`: no new field; the handover writes a blocked entry with the new owner and a reason text.
- The handover itself is recorded only as the gateway's event plus each store's `ConfigChange` diff. There is no
  separate handover log.

### Notifications

- New event type `automation_handed_over` (`NotificationEventTypes.AutomationHandedOver`), added to
  `AdministrativeEvents` and `UnsuppressibleEvents` like `automation_suspended` (`NotificationEventTypes.cs:81`,
  `:156-159`).
- Text (DE/EN): "The automation of {from} ({n} items: {counts by kind}) was handed over to {to} by {admin}. Every item
  is off until it is switched on again." No item names, no arguments.
- Sent once per handover after the decision, never for a refused one.

### UI flow

1. *Settings → Automation owners* (`AutomationOwnersPanel`) gains a section **Owners who can no longer act**: one row
   per `UnusableOwner` — name, state (`AutomationOwners_State_*`), counts per kind, button **Hand over…** (Admins).
   - Links into it: the owner-revoked alert on *Scheduled tasks* (`ScheduledTasksView.razor:85-95`), the
     equivalent markers on *Webhooks*, *Git deploy*, *AI triggers* and *Endpoint checks*, and a line in the
     account-deletion result: "This account owned {n} automations; they stop now. Hand them over in Settings →
     Automation owners." (a count read after the deletion, no gateway call).
2. The dialog **Hand over automation of {from}**:
   1. *Items* — grouped by kind; each with name, on/off, and why it cannot run now. All preselected; the Admin may
      deselect (D1). Items that can still run on the owner are not listed (D4).
   2. *New owner* — a list of people who can act now (`OwnerState.Active`: local accounts, and federated addresses seen
      signing in, that the whitelist admits). Keys are not offered (D3). Picking one runs `Check`: items the person
      cannot hold are deselected and greyed with the reason ("needs Admin"); the old owner and an empty list cannot be
      picked.
   3. *Summary* — "{n} items from {from} to {to}. All of them will be **off**; switch each on to resume. Recorded in the
      audit log." The Admin may be the new owner themselves.
   4. Buttons: **Cancel** (default), **Hand over** (not default, not bound to Enter).
3. The page calls `Mutations.RunAsync("automation.handover", new { fromOwner, toOwner, items },
   async (ResolvedRecord rt, CancellationToken ct) => await Handover.HandOverAsync(rt.Id, toOwner, items, ct))`, with
   the usual `RequireAsync` / `RefuseAsync` role check in front (R1-22).
4. The result lists every item: handed over (was on / was off), or skipped with the reason. A snackbar repeats the
   count. The section refreshes.
5. Resuming is the existing switch of each kind (D2, D7): task toggle (`scheduler.update`), webhook switch
   (`webhook.set_enabled`), AI trigger switch (`aitrigger.save`; keeps the new owner, D7), endpoint check save
   (`endpointcheck.save`; makes the saver the owner, existing rule), fleet **Resume as {owner}**, Git deploy
   **Resume**.

### Configuration

No new configuration key.

## Security model

### Who may do what

- **Hand over:** Admin only (`automation.handover`, catalog level `admin`, INV-13 single-sourced). Viewers and
  Operators see neither the section's button nor the dialog; a direct call is refused by the level with a Denied event.
- **Old owner (D4):** any owner id. Only items for which `LostAuthority(current owner, capability)` is non-null for at
  least one of their capabilities may be handed over. An item that still runs on its owner is refused. A store or
  resolver that cannot be read counts as **refused**, not as "can run" (see fail-closed; note that `LostAuthority`
  returns `null` on a resolver exception, `AutomationMutationRunner.cs:424-430`, so `Check` does not rely on it alone).
- **New owner (D3):** a person (`user:<email>`), `OwnerState.Active` now, not the old owner, and holding **now** the
  level of every capability every selected item needs, judged exactly as a run would judge it (`LostAuthority` with
  the new owner on the item's actor, raised levels and key tool lists included). The authority is still re-read on
  every run afterwards (INV-04): a handover never fixes a level.
- **No synthetic principal:** the handover runs as the signed-in Admin; the new owner does nothing and is not
  impersonated. Provenance of scheduled tasks stays as recorded (D5), so an agent-created task keeps the agent's rules.
- **MCP permission levels:** `McpPermission.cs` is not touched; there is no tool (D10).

### Gateway and approval policy

- One gateway decision per handover: `automation.handover` on `automation-owner:<from>`, Admin, human rule set
  (PC-D1). The kill switch refuses it (no exemption, contract 0009 unchanged); a protected-resource hit cannot occur
  (the target is an owner record). **No approval** (D8): like every existing takeover. It is therefore not an INV-59
  action and not part of any wave of contract 0019.
- The items, the old and the new owner are in the binding; a page that changed between render and click is caught by
  the compare-and-set per item.
- **No auto-confirm:** the original click on *Hand over…* opens the dialog; the decision is the separate, non-default
  **Hand over** button on a summary the dialog built from `Check`.

### Audit

- The gateway event of `automation.handover` (intent and outcome, one operation id) with the details:
  `handover = { from: AuthorityOf(from), to: AuthorityOf(to), items: [ { kind, id, name, wasOn, status } ] }` — using
  `AutomationOwners.AuthorityOf` (`<id> (<state>, <level>)`) as the existing takeovers do.
- Each store's versioned, redacted `ConfigChange` diff as today (webhook secrets, Git tokens and AI trigger prompts are
  treated exactly as in their existing diffs).
- Refusals (level, kill switch, `Check` failed) are the gateway's Denied event with the reason; nothing is written.
- Resumes are the existing events of each switch.

### Secrets

- None is read or written. Owner ids (addresses, key ids) and item names are already in the audit trail today.
- No argument, prompt, token, webhook secret or command text is added to a log, a notification or the event details.

### Fail-closed behaviour

| Situation | Behaviour |
|---|---|
| A store, the account record or the resolver cannot be read during `Check` | That item (or the new owner) counts as refused; the whole handover is refused before any write. |
| New owner not `Active`, a key, the old owner, or lacking a level for any selected item | Refused before any write (no partial handover by authority). |
| Item's owner changed or item deleted between render and click | That item is skipped (`SkippedOwnerChanged` / `SkippedGone`); the rest proceed. |
| A store write fails half-way | Items already written stay handed over **and off**; the outcome is `Failed` with the list so far in the event; nothing ran. |
| A suspension races the handover | The suspension is conditional on the old owner and returns "superseded"; the item is off either way. |
| Kill switch on | Refused; resumes are refused too. |
| The old person signs in again (SSO) or a local account is re-created | Handed-over items stay with the new owner. Items not handed over follow today's rules (D11). |

### INV-04, INV-61, AgentAuthority

- INV-04: unchanged in substance — every run still runs on the recorded owner, re-read each time. New: the recorded
  owner may change by an Admin's handover, which always leaves the item off.
- INV-61: account deletion is unchanged. The deletion result only shows the count of owned automations.
- `AgentAuthority`, INV-08: unchanged; a handover moves no session.

## Invariant and §7 impact

### Changed rows (`docs/v1/SECURITY_INVARIANTS.md`)

- **INV-04:** add to the evidence: "Contract 0020: an Admin can hand the items of an owner who can no longer run them
  over to another usable person in one gateway decision (`automation.handover`); every handed-over item is left off
  (Git deploy apps held), the owner is replaced only while it is still the old one, and an address that signs in again
  never gets them back." Tests: `AutomationHandoverTests.Every_handed_over_item_is_off_and_runs_on_the_new_owner_only_after_resume`,
  `AutomationHandoverTests.A_returning_sso_sign_in_never_regains_handed_over_items`.
- **INV-19:** the RootEquivalent count grows by one (`automation.handover`). `RootEquivalentAdminTests` membership and
  `CapabilityTableConsistencyTests` updated.
- **INV-08, INV-61:** wording unchanged.

### New row: INV-66 (the next free ID after 0018's INV-63/64 and 0019's INV-65)

> **INV-66** — The owner of an automation item changes only by an explicit, interactive Admin act through the Mutation
> Gateway — a takeover by that Admin or a handover to another person — never by an address signing in again. A
> handover moves only items that cannot run on their current owner, only to a person who can act now and holds every
> capability the items need, only while each item's owner is still the old one, and leaves every item off (a Git
> deploy app held) until it is switched on. It is audited with old owner, new owner, acting Admin and every item. There
> is no MCP or agent path.
>
> Tests:
> - `AutomationHandoverTests`;
> - `AutomationHandoverWireTests`;
> - `AutomationHandoverDialogTests`;
> - `AutomationHandoverArchitectureTests.The_store_handover_methods_are_called_only_inside_automation_handover`.

0020 uses **one** new INV row (INV-66). Contract 0021 starts at INV-67.

### `docs/v1/PRODUCT_CONTRACT.md`

- **§6:** add under the decisions: "Contract 0020: no re-linking of automation by e-mail; an Admin hands the automation
  of an unusable owner over (U-42)."
- **§7.5, new row U-42:** "Hand over the automation of an owner who can no longer act (*Settings → Automation owners*,
  `automation.handover`, contract 0020) | Admin | `NotVerifiable` + reason (Whiskers' own records) | GW ✅ (one event:
  old and new owner with authority, every item with its outcome; each store's versioned diff) | INV-04, 19, 66".
- **U-26, U-28, U-29, U-31, U-38, U-34:** add "items can also be handed over (U-42); resume by the switch". U-29 also:
  "Resume clears a handover hold (`gitdeploy.save`)". U-34 also: "Resume as {owner} (`automation.owner_set`)".
- **§7.1–§7.4:** unchanged; S-rows for the runners unchanged (they re-read the owner as today), except the Git deploy
  webhook row notes the `AutomationHeld` refusal.
- **§7.6:** U 41 → 42, total 116 → **117** (after 0018 and 0019).
- `docs/v1/PATH_CAPABILITIES.md`: new row U-42 → `automation.handover`. `docs/v1/CAPABILITY_INVENTORY.md`: new row
  `automation.handover`.

## Test plan

### Unit (`src/Whiskers.Tests`)

**`AutomationHandoverTests`** (real stores on an in-memory database and a temp data directory, fake resolver):
- `Unusable_owners_lists_deleted_unverified_disallowed_key_and_demoted_owners_with_counts`
- `An_owner_whose_items_can_still_run_is_not_listed_and_its_items_are_refused`
- `Every_handed_over_item_is_off_and_runs_on_the_new_owner_only_after_resume`
- `An_item_that_was_on_is_switched_off_and_reported_as_was_on`
- `A_new_owner_without_the_level_for_one_item_refuses_the_whole_handover_and_writes_nothing`
- `A_key_the_old_owner_or_an_inactive_person_cannot_be_the_new_owner`
- `An_unreadable_account_record_refuses_the_handover`
- `An_item_taken_over_meanwhile_is_skipped_and_the_rest_proceed`
- `A_deleted_item_is_skipped`
- `A_store_failure_half_way_leaves_written_items_off_and_reports_failed`
- `A_suspension_racing_the_handover_is_superseded`
- `Scheduled_task_provenance_is_unchanged` (D5)
- `A_handed_over_git_app_is_held_and_its_webhook_deployment_is_refused_as_AutomationHeld` (D6)
- `Switching_a_handed_over_ai_trigger_on_keeps_the_new_owner` (D7)
- `Resume_as_owner_clears_a_fleet_block_only_while_that_owner_is_recorded_and_can_run` (D7)
- `A_returning_sso_sign_in_never_regains_handed_over_items`
- `The_event_names_old_and_new_owner_with_authority_and_every_item`
- `The_notification_is_administrative_and_names_counts_only`

**`AutomationHandoverDialogTests`** (bUnit):
- `Items_are_preselected_and_can_be_deselected`
- `Items_the_new_owner_cannot_hold_are_greyed_with_the_reason`
- `Cancel_is_default_and_Enter_hands_over_nothing`
- `Non_admins_see_no_hand_over_button`

**`AutomationHandoverArchitectureTests`:**
- `The_store_handover_methods_are_called_only_inside_automation_handover`
- `No_mcp_tool_or_agent_tool_maps_to_automation_handover`
- the existing `MutationBypassArchitectureTests`, `MutationGatewayCoverageTests` and `CapabilityCatalogTests` stay green
  with the new row.

### Integration (wire tests, `[Collection("WebAppBoot")]`)

**`AutomationHandoverWireTests`** (real gateway, stores, `OwnerAccounts` with a mixed-sign-in configuration):
- `A_deleted_local_owner_signing_in_by_oidc_stays_deleted_and_a_handover_moves_task_webhook_trigger_check_and_autopatch`
- `The_kill_switch_refuses_a_handover_and_writes_nothing`
- `An_operator_is_refused_by_level_with_a_denied_event`
- `After_resume_the_next_scheduled_run_executes_as_the_new_owner_and_is_refused_after_their_demotion`

### Real application `[braucht Nutzer/Hardware]`

1. Delete a local test account that owns a scheduled task, a webhook and AutoPatch. Sign in with the same address
   through the IdP: everything stays suspended.
2. Hand the three over to a second Admin in *Settings → Automation owners*. All are off; the audit log shows one event
   with both owners and three items; the administrative notification arrives.
3. Switch the task and webhook on, and **Resume as {owner}** for AutoPatch. The next runs show the new owner.

## Plan triage

- **[Claude-prüfbar]** the service, the store methods, the capability and target kind, the reason code, the Git deploy
  hold, the panel section and dialog, the fleet resume, the notification type, DE/EN texts, every test above, and the
  docs (INV-04, INV-19, INV-66, §6, §7 U-42 and notes, PATH_CAPABILITIES, CAPABILITY_INVENTORY,
  `Services/Mutations/README.md`, `Components/Shared/README.md`, CHANGELOG).
- **[braucht Nutzer/Hardware]** the three checks in the real application above.

## Decisions for the maintainer

- **D1 — All at once or per item.**
  - Options: (a) every item of the owner, no choice; (b) one item per call; (c) one call for a chosen set, all
    preselected.
  - **Recommendation: (c).** One owner, one decision, one event is what the audit reader wants; deselecting lets the
    Admin delete or leave an item instead of handing it on. Authority is checked for the whole set before any write;
    only a concurrent takeover makes an item skip.
- **D2 — State after the handover.**
  - Options: (a) every item off (Git deploy apps held, fleet features blocked) until switched on; (b) keep each item's
    current state; (c) restore the state before the suspension.
  - **Recommendation: (a).** The new owner's authority is never used without someone deciding to run the item under
    it. Items that were never suspended (not run since the deletion) would otherwise start at once under (b). The
    result and the event say which items were on, so the Admin knows what to switch back on.
- **D3 — Who can be the new owner.**
  - Options: (a) a person who can act now and holds every needed level now; (b) Admins only; (c) persons or MCP keys.
  - **Recommendation: (a).** It reuses the exact run-time judgment (`LostAuthority`), so an Operator may receive their
    Operator-level tasks and nobody receives what they could not run. Keys are excluded: the departed owner's
    successor should be an accountable person (contract 0001's rule for Git apps and webhooks already says so).
- **D4 — Which items may be handed over.**
  - Options: (a) only items that cannot run on their current owner; (b) every item of an owner whose state is not
    `Active`; (c) every item of anyone (offboarding before deletion).
  - **Recommendation: (a).** It covers deleted, unverified, disallowed, key-less and demoted owners alike and never
    takes a working item from a working person. Offboarding is "delete the account, then hand over"; in between the
    items are refused, which is safe.
- **D5 — Provenance of scheduled tasks.**
  - Options: (a) leave `CreatedVia` as recorded; (b) set it to the new owner, as today's takeover does.
  - **Recommendation: (a).** The new owner never reviewed the task; an agent-created task keeps the agent's rules
    (R1-04). The new owner can adopt it themselves later with today's takeover.
- **D6 — Git deploy apps have no switch.**
  - Options: (a) additive `HeldReason`, deployments refused until **Resume**; (b) accept that the next push deploys
    under the new owner; (c) hand over only apps whose webhooks are off.
  - **Recommendation: (a).** It is the only way D2 holds for Git deploy; it is one nullable JSON field and one refusal
    before anything is fetched.
- **D7 — Resume paths that would re-own.**
  - AI triggers: the handover clears `SuspendedReason`, so switching the trigger on keeps the new owner
    (`AiTriggerStore.cs:107-111` then sees no takeover). Fleet features: a new **Resume as {owner}** button, because
    today only **Take over** clears a block and it re-owns to the clicker. Endpoint checks: keep today's rule (saving
    re-owns to the saver).
  - **Recommendation:** as stated. The alternative, a dedicated resume for endpoint checks, is not worth a new path:
    saving already needs Admin, and an Admin saving the check answers for it.
- **D8 — Approval.**
  - Options: (a) none, as for every takeover (PC-D1); (b) an Admin approval (would need a new rule or listing the
    capability in `ControlPlaneResources`, which it is not), so it could be given in the dialog under contract 0019.
  - **Recommendation: (a).** The act adds no authority anywhere — the new owner already holds every needed level and
    nothing runs until switched on. `RequireSecondApprover` stays the knob for four-eyes on approvals, which this
    action does not raise. Not part of any 0019 wave.
- **D9 — Notification.**
  - Options: (a) administrative, never held, counts only; (b) none (the audit is enough).
  - **Recommendation: (a).** A change of whose authority runs automation is exactly what `automation_suspended` already
    reports; the reverse should be as visible.
- **D10 — MCP exposure.**
  - Options: (a) UI only; (b) an Admin MCP tool.
  - **Recommendation: (a).** It is a rare, interactive personnel decision; a key or an agent has no business choosing
    whose authority automation runs on.
- **D11 — Re-creating a local account with the old address (finding).**
  - Today `account.create` with a deleted owner's address revives every item still recorded on it
    (`OwnerAccounts.cs:89`).
  - Options: (a) keep it, and make `account.create` show "This address still owns {n} automations; they will run on
    this account again once switched on (Git deploy apps at once)"; (b) a tombstone: a deleted address stays `Deleted`
    for its old items even after re-creation (needs a creation time on every item kind, or a per-item generation).
  - **Recommendation: (a)**, plus the Admin is pointed to the handover. Creating the account is an explicit, audited
    Admin act about that very address, so it is not "automatic re-linking"; a tombstone would also lock out the
    person's genuinely new items, because owner ids are addresses. If the maintainer reads the decision of 16 November
    as covering this path too, (b) becomes a contract of its own.

## Out of scope

- Re-linking by e-mail on any path (decided against).
- Items without any owner (taken over as today), and items whose owner can still run them (D4).
- Moving sessions, keys, approvals, notification preferences or agent memory.
- A tombstone for re-created local accounts (D11 b).
- MCP or agent access to the handover (D10).
- Changing the existing per-item takeovers.
