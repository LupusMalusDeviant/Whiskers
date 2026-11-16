# Contract 0019 — Approval in the dialog

Status: **Accepted — 2026-11-16 by the maintainer.** D1, D3 and D7 as stated under "Decisions"; D2, D4, D5, D6 and D8
as recommended. Waves 1 and 2 ship in the next 1.0.0; wave 3 is later.

Scope:
- **Protected areas:** none touched. The auth middleware and its order, the OAuth whitelist, the mTLS/WireGuard layer
  and the docker-socket-proxy verb whitelist stay as they are. The contract changes the UI's way to an existing
  approval, not who may approve or what an approval binds.
- **Surfaces:** the web UI only (`MutationSurface.Ui`, `IUiMutationRunner`). MCP, the agent, AI triggers, the
  scheduler, webhooks and every other automation gain **no** in-band approval (see "Paths that do not change").

## Purpose

The user reported on 16 November 2026: a UI action that needs an approval stops with "Wartet auf Freigabe … auf der
Seite Freigaben". The person then has to open the Approvals page, approve there, come back and trigger the action again.

The goal: an Admin who is signed in approves in the same dialog, and the action then continues by itself. The audit
record stays complete. Nobody gains a power they do not have on the Approvals page today. Non-admins still cannot
approve.

## Current state (verified at `9b678c1f`)

### When the UI raises an approval at all

- A person in the UI is judged by the human rule set (PC-D1): `GuardrailMutationPolicy` picks `HumanEngine` for
  `Surface == Ui && Kind == Human` (`Services/Mutations/MutationPolicy.cs:93-99`). That engine has three rules only:
  principal ceiling, kill switch, protected resources (`Services/Guardrails/GuardrailEngine.cs:35-40`).
- Of those, only `ProtectedResourceRule` can answer `Confirm`, and only for a control-plane hit by an **interactive
  Admin** (`Services/Guardrails/BuiltInGuardrailRules.cs:208-216`, `IsInteractiveAdmin` at `:242-246`). Every other hit
  is `Deny` for everyone: a resource protected only by the policy's own pattern, unanalysable free text while the
  policy lists patterns, and any hit by an Operator or Viewer.
- The kill switch is always `Deny` above read (`BuiltInGuardrailRules.cs:83-101`); it never raises an approval.
  Contract 0009's exemptions are allow-or-deny, not approvals.
- Confirmation for writes, tool lists, argument patterns and the exposure rule are agent rules; they do not bind a
  person in the UI (`GuardrailEngine.cs:30-34`).

**So in the UI, an approval is raised only under INV-59**: an Admin acts on Whiskers' own control plane. Examples:
re-onboarding a host that holds mTLS state (`server.onboard`, `ControlPlaneResources.cs:66-69`), a container action on
`whiskers*` / `socket-proxy*` / `ghostunnel*` …, `selfbackup.restore`, the mTLS capabilities
(`ControlPlaneResources.cs:56-62`), a volume restore or DB query that names the control plane.

### How the gateway handles it

1. Policy says `Confirm`. If an open approval (pending, or approved and unused) has exactly this call's binding, the
   call takes over that approval's operation id (`MutationGateway.cs:430-451`, R1-18). This is the
   approve-then-retry continuation; it exists today.
2. `ApprovalGate.CheckAsync` finds a usable approval or raises a new one (`MutationGateway.cs:576`,
   `ApprovalGate.cs:164-198`). A new one is persisted, a notification `agent_approval` is sent
   (`ApprovalGate.cs:265-282`), and the call is refused with `PendingApproval`.
3. For a protected-resource hit the refusal carries `ReasonCode = ProtectedNeedsAdminApproval` and the approval id
   (`MutationGateway.cs:593-605`). The German text is the one the user saw
   (`Resources/SharedResource.de.resx:5120-5122`).
4. On the retry, the approval is used once (`MutationGateway.cs:716`, conditional database update, INV-35).

The approval subject for the UI:
- `ActorType = "web"` (`MutationGateway.cs:972`);
- level **Admin** whenever `protected-resource` matched (`MutationGateway.cs:983-986`);
- payloads withheld from the stored card (`:997`);
- requesters = the person (`:996`);
- no session id: `UiMutationRunner` sets none on the request, so the approval is stored under session `"mcp-direct"`
  (`ApprovalGate.cs:101`, `:240`) and is not `Interactive`. Closing the page does not cancel it; it expires.
- Pending lifetime 15 minutes (`ApprovalStore.cs:102`), grant lifetime after approval 10 minutes (`ApprovalStore.cs:78`).

### Who may decide it today

- `ApprovalEligibility.Check` (`Services/Approvals/ApprovalEligibility.cs:90-107`):
  - the resolver's current level must cover the approval's level (here Admin);
  - a requester may decide **an interactive request** — kinds `agent-web`, `web`, `terminal` (`:80`) — unless the
    policy sets `RequireSecondApprover`.
- **So today the initiating Admin may already approve their own UI request on the Approvals page**, recorded as
  `SelfApproved` (`ApprovalStore.cs:296`, `:432`). With `RequireSecondApprover` on, they may not, and a second Admin
  must. The fail-closed policy sets it on (`Configuration/GuardrailPolicy.cs:149`).
- Approving and rejecting follow the same rules (INV-37).

### Where a person decides today

- **Approvals page:** `ApprovalsView.razor:194-218` → `IApprovalStore.DecideAsync(id, approved, resolver)`.
- **Agent chat:** `AgentView.razor:495-513` and `AiChat.razor:325-345` already decide **in band**, on the recorded
  approval, through the same `DecideAsync`. This contract brings that pattern to UI dialogs.
- **Notification bell:** it has no approve or reject action (`Components/Shared/NotificationBell.razor`, no reference
  to approvals). It shows the `agent_approval` notification only.

### How UI dialogs show it today

- `UiMutationRunner.RunCoreAsync` turns every non-executed outcome into `MutationRefusedException`
  (`UiMutationRunner.cs:157-175`). The exception carries status, operation id and matched rules, but **not the approval
  id** (`:54-62`). The id is only inside the localized text.
- 93 `catch (MutationRefusedException …)` sites in `Components/` (counted at `a335de85`) show the text as a snackbar or as a log line. Example:
  the onboarding window writes `⛔ {message}` into its log (`Servers.razor:991-994`).

### What the audit records

- The execution's audit row names the approval: `approval = { id, approvedBy }` (`MutationAudit.cs:326`).
- The approval row (`ApprovalRecordEntity`) keeps the decision: status, `ResolvedBy`, `ResolvedAt`, `SelfApproved`,
  `ConsumedAt`, `ConsumedBy`.
- Nothing records **where** the decision was made (page or chat).

## Goals

1. When a UI action ends in `PendingApproval` under INV-59, the person sees the approval **in the same dialog**: what
   will run, on which target, why it needs an approval.
2. If they may decide it (same rules as the Approvals page), they approve with **a separate, explicit button**. The
   action then continues as the **same operation**, uses the approval once, and reports its result in the dialog.
3. If they may not (second approver required), the dialog says so, and they can wait in the dialog or close it.
4. Closing the dialog leaves the approval pending on the Approvals page. Nothing ever runs by itself after the dialog
   is gone.
5. The audit tells "approved in the dialog" from "approved on the page" and "approved in the chat".

## Non-goals

- No change to who may approve: levels, requesters, `RequireSecondApprover`, PC-D4.
- No change to what an approval binds (INV-30), to its lifetimes, to single use, or to the operation journal.
- No in-band approval for MCP keys, agents instructed over MCP, AI triggers, the scheduler, webhooks, AutoUpdate,
  AutoPatch or the web terminal.
- No approval for things that are refused today (policy patterns, kill switch, Operator hits). They stay refused.
- No new approval kind and no "approve without an approval record".

## Interfaces

### Approval channel (persisted)

```csharp
namespace Whiskers.Services.Approvals;

/// <summary>Where a human decided an approval. Persisted as "page" | "dialog" | "chat".</summary>
public enum ApprovalChannel { Page, Dialog, Chat }
```

- `Approval.DecidedVia : string?` (new; `null` for expiry or cancellation by the system and for rows from before 0019).
- `ApprovalRecordEntity.DecidedVia : string?` (new nullable column on the `Approvals` table). Additive; paired
  migrations in `Whiskers.Migrations.Sqlite` and `Whiskers.Migrations.Postgres`. No backfill.

### Store (additive)

```csharp
public interface IApprovalStore
{
    // existing — now means ApprovalChannel.Page
    Task<ApprovalDecisionResult> DecideAsync(string approvalId, bool approved, ApprovalResolver resolver);

    /// <summary>As above, recording where the decision was made. Same eligibility, expiry and single-decision rules.</summary>
    Task<ApprovalDecisionResult> DecideAsync(string approvalId, bool approved, ApprovalResolver resolver,
        ApprovalChannel via);
}
```

- `ApprovalsView` passes `Page`; `AgentView` and `AiChat` pass `Chat`; only the new dialog passes `Dialog`.
- The existing warning and information log lines in `DecideAsync` gain `via` (not a secret).

### Gate result and refusal (additive)

```csharp
public sealed record ApprovalGateResult(bool Approved, string Message, string? ApprovalId = null, string? ApprovedBy = null)
{
    public string? DecidedVia { get; init; }   // from the approval row
    public bool SelfApproved { get; init; }     // from the approval row
}

public sealed class MutationRefusedException /* existing */
{
    /// <summary>The approval this refusal waits for (PendingApproval only); null otherwise.</summary>
    public string? ApprovalId { get; init; }
}
```

### The prompt (new, UI only)

```csharp
namespace Whiskers.Services.Approvals;

/// <summary>Shows the approval raised for one UI call in the dialog the person is in. Registered scoped, for a Blazor
/// circuit only; no hosted service, MCP handler or automation can resolve it.</summary>
public interface IInlineApprovalPrompt
{
    Task<InlineApprovalResult> AskAsync(InlineApprovalRequest request, CancellationToken ct);
}

/// <param name="RequesterPayloads">The payload arguments of the runner's own in-memory request (withheld from the stored
/// card), shown to the requester only; never persisted or logged.</param>
public sealed record InlineApprovalRequest(string ApprovalId, string OperationId, string CapabilityId,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? RequesterPayloads = null);

public enum InlineApprovalOutcome
{
    /// <summary>The person approved it in the dialog (recorded, via Dialog).</summary>
    ApprovedHere,
    /// <summary>Someone else approved it while the dialog was open, and the person then clicked "Run now".</summary>
    ApprovedElsewhereRun,
    /// <summary>Rejected in the dialog (recorded) or elsewhere.</summary>
    Rejected,
    /// <summary>Closed, cancelled or the circuit went away: the approval stays pending on the Approvals page.</summary>
    LeftPending,
    /// <summary>It expired while the dialog was open.</summary>
    Expired,
}

public sealed record InlineApprovalResult(InlineApprovalOutcome Outcome, ApprovalDecisionResult? Decision = null);
```

- Implementation: `MudInlineApprovalPrompt` (scoped) over `IDialogService`, with the component
  `Components/Shared/InlineApprovalDialog.razor`.
- The rollout list (code, not configuration):

```csharp
public static class InlineApproval
{
    /// <summary>Capabilities whose INV-59 approval may be given in the dialog (waves 1 and 2 in 1.0.0, D7).</summary>
    public static readonly IReadOnlySet<string> Capabilities;

    public static bool Applies(string capabilityId, IReadOnlyList<string> matchedRules) =>
        matchedRules.Contains(ProtectedResourceRule.RuleId, StringComparer.Ordinal) && Capabilities.Contains(capabilityId);
}
```

### Runner flow (`UiMutationRunner`, constructor gains `IInlineApprovalPrompt? prompt = null`, `IApprovalStore? approvals = null`)

The pages do not change: they keep calling `RunAsync("capability", args, lambda)`, the shape the bypass architecture
test recognises.

1. Run the gateway as today.
2. If the outcome is `PendingApproval` **and** `prompt` is registered **and** `InlineApproval.Applies(…)` **and** the
   approval read from the store
   - is `Pending`,
   - has `CorrelationId == outcome.OperationId`,
   - has `ActorType == "web"`,
   - and its first requester is the current principal id,

   then call `prompt.AskAsync`. Otherwise throw `MutationRefusedException` as today, now with `ApprovalId`.
3. `ApprovedHere` or `ApprovedElsewhereRun`: build the request again from scratch: principal and session re-checked
   (R1-26), the same capability and the same arguments. Call `ExecuteAsync` once more with the same lambda. The gateway
   continues the original operation id through its existing approve-then-retry path (`MutationGateway.cs:430-451`).
   The outcome is handled by the same `switch` as step 1.
4. If step 3 returns `PendingApproval` again with **another** approval id, the runner reads the **first** approval from
   the store again and tells two cases apart:
   - **Not consumed** (`ConsumedAt` is null): the binding changed, the call drifted (D4). Show the new approval's prompt
     once more, marked "changed since you approved". A third `PendingApproval` is thrown as a refusal; there is no loop.
   - **Consumed** (`ConsumedAt` set: another tab, a repeated click or another continuation used the grant first): no
     second prompt. Throw `MutationRefusedException(Status = PendingApproval, ApprovalId = new id)` with the text
     "Your approval was already used at {ConsumedAt} (operation {CorrelationId}); nothing ran here. The new approval
     {id} is pending on the Approvals page." Nothing drifted, so the drift text is never shown in this case.
5. `Rejected`, `LeftPending`, `Expired`: throw `MutationRefusedException(Status = PendingApproval or Denied,
   ApprovalId)` with a localized text ("left on the Approvals page", "rejected by {0}", "expired"). The page's existing
   `catch` shows it as today.

### UI flow in the dialog

1. The person clicks the action (for example "Re-onboard" in the onboarding window) and confirms any dialog the page
   already shows.
2. The gateway raises the approval. The runner opens **"Approval needed"** on top of the current window. It shows the
   persisted approval, not the page's own idea of the call:
   - capability and its label;
   - target server and workload as resolved;
   - the reason (redacted, payloads withheld);
   - level Admin and the matched rule;
   - the arguments with payloads withheld;
   - in a separate block, the requester's own payload input (for example the query of `db.query`), see "Payloads for
     the requester" below;
   - the approval id and the time left until it expires;
   - for a self-decision, the line "You are approving your own request. This is recorded."
3. Buttons: **Approve and run**, **Reject**, **Close (leave pending)**.
   - None is the default button. Enter does not trigger one.
   - "Approve and run" is enabled only after 1.5 s and only when `CanDecide` allows it (D2).
4. With `RequireSecondApprover` on, or a resolver without Admin now, "Approve and run" stays disabled with the reason
   (`Approval_Result_RequesterCannotDecide` / `_InsufficientLevel`). The dialog listens to `IApprovalStore.Changed`.
   - When another Admin approves on the page, it shows "Approved by {0}" and a **Run now** button.
   - When another Admin rejects, it shows "Rejected by {0}".
5. After "Approve and run" the dialog records the decision with `ApprovalChannel.Dialog`. It closes only when the
   decision is `Decided`. Any other result is shown in the dialog and nothing runs.
6. The action continues **within the same runner call, directly after the decision** (runner step 3). Its progress and
   result appear where they appear today (the onboarding log, a snackbar).
   - If the circuit drops after the decision and before the runner's `ExecuteAsync` (step 3), nothing runs: the call
     is cancelled with the circuit. The approval stays **approved and unused**, an open grant for its normal 10 minutes
     (`ApprovalStore.cs:78`), and then lapses (INV-32). No one runs it by itself.
   - If the same person repeats the identical click within those 10 minutes, the gateway finds the open approval with
     the same binding (`IApprovalGate.OpenOperationOf`, `MutationGateway.cs:438`, `ApprovalGate.cs:211-215`), takes
     over its operation id (R1-18, `MutationGateway.cs:430-451`) and uses the grant once (INV-35). A second repeat
     finds no open approval and raises a new one.

**Payloads for the requester (wave 2, `db.query` and other payload arguments).** The stored card withholds payloads
for UI calls (`WithholdPayloads`, `MutationGateway.cs:997`; `ApprovalGate.cs:231-233`), so it cannot show the query.
The dialog shows it anyway, to the requester only, in a separate block "Your input (not stored with the approval)":
the runner passes the payload arguments of its own in-memory request to the prompt. The runner opens the prompt only
for the person who is the approval's first requester (step 2), i.e. the person who typed it. What runs is still bound
by the arguments hash (INV-30), so the text shown is the text approved. It is never written to the approval row, the
audit, a notification or a log beyond what exists today; a second Admin deciding on the Approvals page sees the card
with payloads withheld, as today.

### Configuration

No new configuration key. `RequireSecondApprover` in the guardrail policy stays the one switch for four-eyes (D1, D3).

## Paths that do not change

| Path | Today | After 0019 |
|---|---|---|
| Direct MCP (`/mcp`, key) | `pending_approval` envelope (`McpErrorEnvelope.cs:20`); the requesting key never decides (non-interactive) | Unchanged. No prompt is registered outside a circuit. |
| Agent chat on the web (A-01) | In-band card, self-confirm unless `RequireSecondApprover` | Unchanged; its decisions are now recorded as `chat`. |
| Agent instructed over MCP (`agent-mcp`) | Non-interactive; another person decides on the page | Unchanged. |
| AI trigger, scheduler, AutoUpdate, AutoPatch, webhook, VPN bring-up | `Confirm` → terminal `Denied` `UnattendedConfirm`, no approval raised (`MutationGateway.cs:493-501`) | Unchanged. |
| Web terminal (`terminal`) | Approvals page | Unchanged (out of scope). |
| Approvals page (U-33) | `DecideAsync` | Unchanged, recorded as `page`. |

## Security model

### Who may do what

- **Raising:** unchanged. Only an interactive Admin's control-plane hit raises an approval in the UI. An Operator or a
  Viewer is refused before any approval exists, so a non-admin never sees the prompt.
- **Deciding in the dialog:** exactly `ApprovalEligibility.Check`, through the same `DecideAsync`:
  - the resolver's **current** role, read afresh (`ApprovalResolver.CurrentAsync`);
  - level ≥ Admin for an INV-59 approval;
  - the requester only while `RequireSecondApprover` is off;
  - not after expiry;
  - once only (the conditional update).
- **No new power (D1):** the dialog decides only the approval its own call raised a moment earlier
  (`CorrelationId == OperationId`, first requester = the person). On the page the same person can already decide that
  same approval under the same rules. The dialog cannot decide someone else's approval, an approval raised on another
  surface, or one that is not pending.
- **MCP permission levels and the capability catalog:** unchanged. `McpPermission.cs` is not touched.

### Gateway and approval policy

- Every execution still runs through the Mutation Gateway with the Confirm → approval → single use pipeline. The
  continuation is an ordinary gateway call: the decision is recorded again (INV-22), the binding is computed again, and
  the approval is used only if the binding is identical (INV-30), within the grant (INV-32), once (INV-35), for the
  operation it was raised for (INV-39).
- **No auto-confirm (D2):** the original click never approves. The approval is a separate act on a recorded approval:
  - its own button, never a default;
  - armed after a delay;
  - on a summary read back from the store.
  "Run now" after someone else's approval is a click as well.

### Audit

- Unchanged: the `PendingApproval` refusal event, then the intent and outcome of the same operation id, naming the
  approval.
- New:
  - `approval.via` (`page | dialog | chat`) and `approval.selfApproved` in the execution's audit details;
  - `DecidedVia` on the approval row;
  - the Approvals page history shows "in the dialog" next to the existing self-approval mark.

### Secrets

- The dialog's summary is the persisted card. Payloads are withheld for UI calls (`WithholdPayloads`,
  `MutationGateway.cs:997`) and the reason is redacted (`ApprovalGate.cs:249`).
- The one addition is the requester's own payload input, from the runner's memory, shown to that person only (they
  typed it). It is not added to the approval row, the audit, the notification or any log.
- The runner never logs arguments. A refusal log names the capability and the exception type only, as today
  (`UiMutationRunner.cs:194-199`).
- `DecidedVia` is not secret.

### Fail-closed behaviour

| Situation | Behaviour |
|---|---|
| No prompt registered, capability not in the rollout list, approval not found, not pending, other operation or other requester | Today's refusal, nothing runs. |
| The prompt throws | `LeftPending`, nothing runs, logged without arguments. |
| `DecideAsync` returns anything but `Decided` (`NotRecorded`, `Expired`, `RequesterCannotDecide`, …) | Shown in the dialog, nothing runs. |
| Session revoked or role lowered between prompt and continuation | The rebuilt request is refused (R1-26) or the decision is refused (INV-34). |
| Plan drift (target, connection, exposures, credential, policy version changed) | New binding → the approved grant is not used; a new approval is raised (D4). |
| Circuit gone, dialog closed, page left | Cancellation; nothing runs. A later approval on the page runs nothing by itself (approve-then-retry needs a call). |
| Circuit gone after the decision, before the continuation | Nothing runs. The grant stays approved and unused for its 10 minutes; an identical repeat click by the same person uses it once (`OpenOperationOf`, `MutationGateway.cs:438`); otherwise it lapses. |
| The grant was used by another tab or call before the continuation | "Already used" refusal with the new approval's id; no second prompt, no drift text. |
| Grant lapsed (10 min) before the continuation | Use refused (INV-32). |

## Invariant and §7 impact

### Changed rows (`docs/v1/SECURITY_INVARIANTS.md`)

- **INV-33:** add to the evidence: "Since contract 0019 a UI request may also be decided in the dialog of the action,
  through the same `ApprovalEligibility.Check`. The decision records its channel (`page`, `dialog`, `chat`)."
  Tests: `InlineApprovalRunnerTests.Second_approver_policy_disables_approve_in_the_dialog`,
  `ApprovalChannelTests.A_self_decision_in_the_dialog_is_recorded_as_such`.
- **INV-59:** "approved and audited" becomes "approved — in the dialog of the action or on the Approvals page, as a
  separate explicit act — and audited with the approval's id and channel".
  Test: `InlineApprovalEndToEndTests.Re_onboarding_approved_in_the_dialog_runs_once_under_one_operation_id`.
- **INV-30, INV-32, INV-35, INV-39:** wording unchanged; new evidence tests:
  - `InlineApprovalRunnerTests.Drift_after_approval_never_uses_the_old_grant`;
  - `InlineApprovalRunnerTests.Approve_in_dialog_continues_the_same_operation_once`;
  - `InlineApprovalEndToEndTests.Two_continuations_of_one_approval_execute_once`.

### New row: INV-65 (the next free ID after contract 0018's INV-63 and INV-64)

> **INV-65** — An approval given in the dialog of a UI action is a separate, explicit act on the recorded approval
> raised by that very call (same operation id, the person as first requester), under the Approvals page's rules. The
> call continues only through the gateway, on an identical binding, at most once, and only within the same runner
> call, directly after the decision. If that call ends first (dialog closed, circuit lost), nothing runs by itself; an
> approved grant left unused lapses after its lifetime unless the same person repeats the identical action, which uses
> it once. MCP, agent, trigger, scheduler and webhook calls have no in-band approval.
>
> Tests:
> - `InlineApprovalRunnerTests`;
> - `InlineApprovalEndToEndTests`;
> - `InlineApprovalDialogTests`;
> - `InlineApprovalArchitectureTests.The_prompt_is_resolvable_only_in_a_circuit`.

### `docs/v1/PRODUCT_CONTRACT.md`

- **§6:** PC-D1 and PC-D4 unchanged. Add a line under the decisions: "Contract 0019: an INV-59 approval may be given in
  the action's dialog; same eligibility as the Approvals page."
- **§7.5, common text:** "Approval n/a for humans under PC-D1, except an Admin's action on … Whiskers' control plane,
  which needs an Admin approval (INV-59, R1-16)". Add: "given in the action's dialog or on the Approvals page
  (contract 0019)".
- **U-33:** add "also from the action's dialog, for the approval that action raised; channel recorded".
- **U-01, U-05, U-21, U-24, U-37** and the deploy, compose and app-store rows (U-10, U-11, U-12) for their capabilities
  in waves 1 and 2: add the note "INV-59 approval in the dialog (0019)". U-02 and U-25 (wave 3, later) get it only
  when wave 3 ships (D7).
- **§7.1, §7.2 (A-01), §7.3, §7.4:** unchanged. State this explicitly in the change log of the document.

## Test plan

### Unit (`src/Whiskers.Tests`)

**`InlineApprovalRunnerTests`** (fake gateway, real `ApprovalStore` on an in-memory database, fake prompt):
- `Pending_under_INV59_shows_the_approval_raised_for_this_call`
- `Approve_in_dialog_continues_the_same_operation_once`
- `Closing_the_dialog_leaves_the_approval_pending_and_runs_nothing`
- `Reject_in_dialog_records_rejected_and_runs_nothing`
- `An_approval_of_another_operation_or_requester_is_never_prompted`
- `A_capability_outside_the_rollout_list_keeps_todays_refusal_with_the_approval_id`
- `Without_a_prompt_the_refusal_is_unchanged`
- `Second_approver_policy_disables_approve_in_the_dialog`
- `Approved_elsewhere_runs_only_after_Run_now`
- `Drift_after_approval_never_uses_the_old_grant` (policy saved, host connection changed, credential changed)
- `A_second_drift_is_refused_without_a_third_prompt`
- `A_revoked_session_between_approval_and_continuation_runs_nothing`
- `Cancellation_runs_nothing_and_a_later_page_approval_runs_nothing`
- `Decided_then_circuit_lost_runs_nothing_and_a_repeat_click_uses_the_grant_once`
- `A_consumed_grant_reports_already_used_and_never_the_drift_text`
- `A_prompt_that_throws_runs_nothing`
- `Server_hostkey_replace_keeps_todays_refusal_with_the_approval_id` (contract 0018; not in waves 1–2)

**`ApprovalChannelTests`:**
- `DecidedVia_is_persisted_on_both_providers`
- `Legacy_rows_read_as_unknown_channel`
- `The_page_records_page_and_the_chat_records_chat`
- `A_self_decision_in_the_dialog_is_recorded_as_such`
- `The_execution_audit_names_channel_and_self_approval`

**`InlineApprovalDialogTests`** (bUnit):
- `No_button_is_default_and_Enter_approves_nothing`
- `Approve_is_armed_only_after_the_delay`
- `The_summary_comes_from_the_store_with_payloads_withheld`
- `The_requesters_own_query_is_shown_in_the_dialog_and_never_persisted_or_logged`
- `Another_admins_decision_updates_the_dialog`
- `Expiry_closes_as_expired`

**`InlineApprovalArchitectureTests`:**
- `The_prompt_is_resolvable_only_in_a_circuit`: no singleton, hosted service, MCP tool or automation runner takes
  `IInlineApprovalPrompt`.
- `Only_the_dialog_decides_via_Dialog`.
- The existing `MutationBypassArchitectureTests` stays green unchanged.

**MCP regression:**
`DirectMcpCallGuardTests.Confirm_raises_one_approval_and_the_identical_call_runs_once_after_a_human_approves` stays green.
New: `McpPendingApprovalIsUnchangedTests.A_key_never_gets_an_inline_prompt`.

### Integration (wire tests, `[Collection("WebAppBoot")]`)

**`InlineApprovalEndToEndTests`** (real gateway, gate, store, `ProtectedResourceRule`; fake onboarding service and
Docker):
- `Re_onboarding_approved_in_the_dialog_runs_once_under_one_operation_id`: the audit shows one `PendingApproval` event
  and one `Executed` event with the same operation id and approval id, `via = dialog`.
- `Restarting_the_whiskers_container_from_the_dashboard_is_approved_in_the_dialog`.
- `An_operator_is_refused_and_never_sees_a_prompt`.
- `Two_continuations_of_one_approval_execute_once`.

### Real application `[braucht Nutzer/Hardware]`

1. Re-onboard a host that already holds mTLS state from the onboarding window. Approve in the dialog. The onboarding
   continues in the same log.
2. Restart a `whiskers*` or `socket-proxy` container from the Dashboard. Approve in the dialog.
3. With *Require a second approver* on: the dialog shows "second Admin needed". A second Admin approves on the page,
   and "Run now" runs it.
4. Close the dialog. The approval stays on the Approvals page, expires after 15 minutes, and nothing runs.
5. The audit log and the Approvals history show "in the dialog" and the self-approval mark.

## Plan triage

- **[Claude-prüfbar]**
  - channel enum, column and paired migrations;
  - store overload;
  - gate result and refusal fields;
  - prompt interface, dialog component, runner flow, rollout list with the capabilities of waves 1 and 2 (D7);
  - localized texts (DE/EN);
  - every test above;
  - docs: INV rows, §7, `Services/Approvals/README.md`, `Services/Mutations/README.md`, `Components/Pages/README.md`,
    CHANGELOG.
- **[braucht Nutzer/Hardware]** The five checks in the real application listed above.

## Decisions (2026-11-16, by the maintainer)

- **D1 — Self-approval in the dialog.**
  - **Decided:** the Admin who triggers the action may approve it in the dialog, under exactly the rules of the
    Approvals page. The decision is recorded as `SelfApproved` with `via = dialog`. With `RequireSecondApprover` on, the
    requester cannot approve, and the dialog waits for a second Admin.
  - Reason: the page allows it today (`ApprovalEligibility.cs:80,104`, `ActorType "web"` is interactive). The dialog
    decides only the approval its own call raised and adds no power. The real four-eyes control is
    `RequireSecondApprover` (PC-D4), and it binds the dialog exactly as it binds the page.
- **D2 — The explicit step.**
  - **Decided:**
    - a separate dialog after the action's own click;
    - its own "Approve and run" button: not the default, not bound to Enter, armed after 1.5 s;
    - the summary read back from the persisted approval, with the self-approval line;
    - "Run now" after an approval given elsewhere is a click too.
  - Reason: an approval implicit in the original click (for example an "approve if needed" checkbox) would be an
    auto-confirm.
- **D3 — Exempt classes.**
  - **Decided:** no exempt classes. Every INV-59 capability may be approved in the dialog once its wave ships (D7).
  - Reason: in the UI only INV-59 control-plane hits are approvable at all. The kill switch (contract 0009),
    policy-pattern protected resources and every Operator hit are refused, not approvable. Destructive cloud
    operations need no approval for a person (PC-D1). For the control-plane hits, the page adds no check the dialog
    lacks: it is the same person under the same rules. An install that wants more uses `RequireSecondApprover`.
- **D4 — Continuation and drift.**
  - **Decided:**
    - the original **operation** continues: same operation id, same audit chain, through the gateway's existing
      approve-then-retry path (`MutationGateway.cs:430-451`);
    - the request is rebuilt (fresh session check), the decision recorded again, the binding computed again;
    - if anything bound changed, the old grant is not used: a new approval is raised and shown once as "changed since
      you approved"; a second drift is refused;
    - the old grant lapses unused after 10 minutes.
  - Reason: a fresh operation id would split the audit chain, and INV-39 already defines the continuation.
- **D5 — Timeout and closing.**
  - **Decided:**
    - the dialog has no timeout of its own; it shows the approval's remaining time (15 minutes from raising) and closes
      as "expired" at the end;
    - Close, Escape, leaving the page or a dropped circuit leave the approval **pending** on the Approvals page (it is
      not rejected);
    - nothing ever runs after the dialog is gone, even if someone approves it later on the page: the person repeats
      the action, as today.
  - Reason: if closing rejected the approval, a closed tab would silently decide.
- **D6 — Audit channel.**
  - **Decided:** distinguish the channel. `DecidedVia` on the approval row (`page`, `dialog`, `chat`; `null` for system
    and legacy), plus `approval.via` and `approval.selfApproved` in the execution's audit details.
  - Reason: logs are not the audit record.
- **D7 — Scope and rollout.**
  - **Decided:** waves 1 and 2 ship in the next 1.0.0. Wave 3 is allowed by D3, but it is out of scope for 1.0.0 and
    comes later.
    1. **Wave 1 (1.0.0):** `server.onboard` (the onboarding window, the reported case), `container.start` / `stop` /
       `restart` / `update` / `rollback` on Dashboard and Container detail.
    2. **Wave 2 (1.0.0):**
       - `mtls.client_migrate`, `certificate.renew_mtls`, `certificate.issue_mtls`, `mtls.client_renew` on *Servers*;
       - `volume.backup` / `volume.restore` and `db.query` when they name the control plane;
       - `compose.deploy` and the deploy / compose / app-store paths when the plan touches a protected container.
    3. **Wave 3 (later, not in 1.0.0):** the irreversible actions `container.remove`, `selfbackup.restore`,
       `mtls.shared_client_retire`, `volume.backup_delete`. Until then they keep today's refusal with the approval id
       and are approved on the Approvals page.
  - One PR per wave. Each wave adds its capabilities to `InlineApproval.Capabilities` and its §7 notes.
  - Not in any wave yet: `server.hostkey_replace` (new in contract 0018, an INV-59 capability through
    `ControlPlaneResources.Capabilities`). Until a later wave lists it, it keeps today's refusal with the approval id
    and is approved on the Approvals page, as wave 3. Contract 0018 states the same (its UI step 5 and U-41).
  - Reason: the reported case and the frequent control-plane actions come first; the irreversible ones follow once
    the dialog has been used in practice.
- **D8 — Notification on a dialog approval.**
  - **Decided:** keep it. The gate still sends `agent_approval` when it raises the approval, even if the person
    approves it a moment later in the dialog.
  - Reason: a change to the control plane is worth a signal to the other Admins. The card then shows it as already
    decided, in the dialog, self-approved. Holding the notification back for `web` requests would also change the
    gate's timing for every surface.

## Out of scope

- Approvals for MCP keys, agents over MCP, AI triggers, the scheduler, webhooks, AutoUpdate, AutoPatch and the web
  terminal.
- Wave 3 of D7 in 1.0.0 (`container.remove`, `selfbackup.restore`, `mtls.shared_client_retire`,
  `volume.backup_delete`): allowed by D3, shipped later.
- Approve or reject in the notification bell.
- Any change to `ProtectedResourceRule`, `ControlPlaneResources`, the binding, the lifetimes or the pending quota
  (C-19).
- Giving UI approvals a session (`Interactive`, INV-36). Today they are stored under `"mcp-direct"` and outlive the
  page. That stays, and it is why "nothing continues after the dialog" is enforced by the runner, not by the store.
- Revoking an approved but unused grant before its 10 minutes end.
