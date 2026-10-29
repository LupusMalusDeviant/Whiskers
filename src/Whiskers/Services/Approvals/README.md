# Services/Approvals

**Human-in-the-Loop approvals**, core since ADR-0005 step 2.1 (PC-D8). When the guardrail policy answers `Confirm`,
a human must approve the call before it runs, whether it came from the agent, from a direct MCP client, or (from
WP2 step 2.2 on) from the UI. Unattended jobs (scheduler, AutoUpdate, AutoPatch) never come here: the gateway refuses
their `Confirm` outright, since nobody could approve it and a later run of the same job is not a retry (R1-01).

## How it fits together

1. **Agent session.** [`AgentSession`](../Agent/AgentSession.cs) pauses on `Confirm`; the page registers an approval
   through [`ApprovalCoordinator`](../Agent/Approvals/ApprovalCoordinator.cs) with the call's correlation id and the
   hash of its arguments, and the binding the gateway computed when it decided the call (`DecideAsync` →
   `GuardrailDecision.Binding`). Once a human approves, the session continues, and the Mutation Gateway asks
   `ApprovalGate` whether an approval exists for **this operation** (correlation id + session + tool) with **these
   arguments** and **this binding**. The session pauses on the same level the gateway uses: the tool's level (stricter if stored in Settings → MCP)
   raised to the operation's **effective** level (`IEffectiveRequirementResolver`, R1-05 — e.g. Admin for a
   scheduled custom command). An approval below the current effective level is never used (`ApprovalGate`), and
   the approver must hold that level.
2. **Direct MCP call** (cannot wait): `ApprovalGate` raises an approval and refuses the call; once a human approved
   it, the identical call (same binding) runs once within ten minutes of the approval (approve-then-retry).
   Concurrent identical first calls share one approval (`IApprovalStore.FindOpen` under one lock). An approval that
   was rejected, expired or used answers no retry, so the next call asks again. The approval keeps the operation id
   of the call that raised it (`CorrelationId`); `OpenOperationOf(binding)` hands it to the gateway, so the retry
   continues that operation under the same id (R1-18, INV-39). A retry under another operation id never gets that
   approval (`FindOpen(binding, operationId)`): it asks for its own. An approval raised before R1-18 (no operation id)
   is therefore not reused by a gateway call after the upgrade; the call asks again.
3. **Checking is not using.** `CheckAsync` only finds (or raises) the approval. The caller uses it with
   `TryConsumeAsync` right before the call runs: the gateway after its audit intent is written, `DirectMcpCallGuard`
   immediately. A refusal in between leaves the approval valid. The use is one conditional update in the database
   (approved, unused, grant not lapsed), so of any number of concurrent consumers — in one process or in the store
   started after a restart — exactly one wins (INV-35).
4. **Which decision.** An approval names the recorded policy decision that asked for it (`Approval.DecisionId`,
   INV-22) and that decision's preset (`GuardrailPreset` — a trigger's pinned one, not whichever is active by then).
   Before an agent session pauses it has the gateway decide and record the call (`IMutationGateway.DecideAsync`),
   so its approval names that decision too, also when the human rejects it.
5. **What an approval is bound to (R1-14, INV-30).** `ApprovalBinding` hashes the principal (kind, id, the credential
   it authenticated with, whom it acts for), the capability and its effective level, the normalized arguments
   (`PolicyCanonicalJson.InputHash`), the resolved target (kind, id, server, related resources) and the policy version
   the decision was made under. Every call records a decision of its own, so the binding names the decision's
   content — its policy version — rather than its id. A call with any other binding never inherits the approval;
   an approval or a subject without a binding is never used.
6. The notification bell and the **Approvals** page read `GetPending()` and decide with `DecideAsync(id, approved, ApprovalResolver)` — see *Who decides* below.

There is no way to grant a call without an approval decided in `IApprovalStore`.

## Persistence (R1-14, INV-31)

Every approval is in the `Approvals` table (`IApprovalPersistence`, `DbApprovalPersistence`) from the moment it is
raised: an approval that cannot be written is not raised (the gateway refuses the call). Decisions are conditional
updates (only a pending approval is decided, and a human decision counts only once it is on record), uses too.
After the schema is migrated at startup, `LoadAsync` loads the open approvals: pending approve-then-retry approvals
and unused grants carry on; a pending agent approval (`Interactive`) is cancelled, since its session is gone. Rows
are never deleted — the page reads the history from the table (`GetHistoryAsync`), and the store only forgets
closed approvals from memory, never an open one. Overdue approvals (default 15 min) are swept to `Expired`; a grant
lapses ten minutes after the approval (`GrantExpiresAt`). A host without a database (unit tests) uses
`InMemoryApprovalPersistence` with the same conditional semantics.

## Who decides (R1-15)

A human decides through `IApprovalStore.DecideAsync` with an `ApprovalResolver` — their principal id
(`user:<email>`) and the level their role grants **now** (`ApprovalResolver.CurrentAsync`); the chat, the Approvals
page and the bell all go through it. `ApprovalEligibility` holds approving and rejecting to the same rules (INV-37):

- **Level (INV-34).** The resolver holds at least the approval's level — the effective level of the action. An
  approval without a readable level asks for Admin. A Viewer decides nothing.
- **Separation of duties (INV-33, PC-D4).** An approval records who asked (`Approval.Requesters`: the caller, then
  everyone it acts for — an agent's user, an automation's owner); an approval that names nobody (raised before
  R1-15) is decided by nobody, and `LoadAsync` cancels it so the caller asks again. None of them decides a non-interactive request
  (direct MCP, AI trigger, scheduled task, webhook; any unknown kind). An interactive request (agent chat `agent-web` —
  also an approval the gateway raises for a chat call, from the trusted `MutationRequest.AgentOrigin`,
  a UI click `web`, the web terminal) may be decided by its requester unless the active guardrail policy sets
  `RequireSecondApprover`; such a decision is recorded as `SelfApproved`. An unreadable policy requires a second
  approver.
- **Expiry (INV-32).** An approval past `ExpiresAt` is expired, not decided — checked by the store and again by the
  conditional database update; a decision is committed in one transaction only if the approval has not expired once its row is held
  (after any lock wait; `ResolveAsync(…, stillDecidable)`), so a late write never becomes durable and the waiting session gets a "no". A timer (`ApprovalStore.SweepInterval`) expires overdue approvals even when nobody
  opens the list, so a waiting session is released. A grant is usable only until `GrantExpiresAt` — checked again
  once the row is held before a use is committed (`ConsumeAsync(…, stillUsable)`), so a use that waited past the end
  of the grant does not count.
- **Bounded (C-19).** One principal has at most `ApprovalEligibility.MaxPendingPerPrincipal` approvals waiting;
  an approval counts against everyone in its requester chain (an agent session and the user it acts for), so more
  chats do not raise the bound. Beyond it `CreateAsync` throws `ApprovalLimitException` and the gate refuses the call.

A raised approval is announced with its id, its risk and its target as `ApprovalDisplay.NotifiedTarget` names it
(`ApprovalSubject.NotifiedTarget` / the agent decision's `NotifiedTarget`): the kind (`container`, `dns-zone`, …),
and — only for a target the resolver found, of a kind whose id it takes from the inventory (container, network, pod,
Git app, server, cloud server, scheduled task, webhook) — that id, plus the server where that comes from the
inventory too (container, network, pod, Git app); each only as a plain identifier the secret redactor leaves alone.
Never the arguments or any other value a caller can put into the call — a DNS zone, a host or a scheduled task's
server may be the caller's — since the bell may forward it to external channels. The approval's card in the app
shows the resolved target (`NotifiableTarget`, `TargetServer`/`TargetWorkload`). The event carries `ApprovalId`, so the bell opens the approval's card (`approvals#<id>`).
External channels get the absolute link (`<public url>/approvals#<id>`) only when the admin configured the public
address (`WhiskersPublicUrl`, `Whiskers:PublicUrl`; never a request's host) — otherwise the id and
"Whiskers → Approvals".

## Files

| File | Purpose |
|---|---|
| `ApprovalStore.cs` | `IApprovalStore` + `ApprovalStore`: create / list / decide / use / cancel approvals, the persisted history, a `Changed` event for the bell + page, expiry at decision time and on a timer, the per-principal bound, `LoadAsync` after a restart. |
| `ApprovalEligibility.cs` | `ApprovalResolver`, `ApprovalDecisionResult`, `ApprovalEligibility` (who may decide: level, requester, interactive kinds, `RequestersOf`), `ApprovalLimitException`, `ApprovalDecisionMessages` (UI resource keys). |
| `ApprovalPersistence.cs` | `IApprovalPersistence`: `DbApprovalPersistence` (table `Approvals`, conditional decide/use) and `InMemoryApprovalPersistence` (hosts without a database). |
| `ApprovalBinding.cs` | `ApprovalBinding.Of` (gateway calls) and `OfDirectCall` (direct MCP reads): the hash an approval is bound to. |
| `ApprovalGate.cs` | `IApprovalGate` + `ApprovalGate`, `ApprovalSubject` (with its `Requesters`; `WithholdPayloads` keeps a UI call's SQL, command or compose file off the stored card, showing only its length, R1-16), `ApprovalGateResult`: decides whether a `Confirm` may run now (`CheckAsync`), uses the approval (`TryConsumeAsync`) and names the operation an open approval was raised for (`OpenOperationOf`, R1-18). Single use, bound to the call's binding and arguments hash, valid ten minutes after the decision. |
| `ApprovalFingerprint.cs` | The hashes that bind an approval: arguments (keys sorted, raw JSON values) and caller + tool + arguments. |
| `ApprovalDisplay.cs` | Display-only card context: risk band from the level, best-effort target from the arguments. `NotificationText`: the notification of a raised approval (id, risk, target, link; no arguments). |

The agent-specific bridge, [`ApprovalCoordinator`](../Agent/Approvals/), stays in the Agent module.

## Related

- Model: [`../../Models/Agent/Approval.cs`](../../Models/Agent/Approval.cs)
- Gateway: [`../Mutations/`](../Mutations/)
- Tests: `ApprovalStoreTests`, `ApprovalGateTests`, `ApprovalBindingTests`, `ApprovalPersistenceTests`,
  `ApprovalConcurrencyTests`, `ToolAuthorizationTests`, `DirectMcpCallGuardTests`
