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
   was rejected, expired or used answers no retry, so the next call asks again.
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
6. The notification bell and the **Freigaben** page read `GetPending()` and call `ResolveAsync(id, approved, resolvedBy)`.

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

## Files

| File | Purpose |
|---|---|
| `ApprovalStore.cs` | `IApprovalStore` + `ApprovalStore`: create / list / resolve / use / cancel approvals, the persisted history, a `Changed` event for the bell + page, automatic expiry, `LoadAsync` after a restart. A resolver may only approve what its own level could do. |
| `ApprovalPersistence.cs` | `IApprovalPersistence`: `DbApprovalPersistence` (table `Approvals`, conditional decide/use) and `InMemoryApprovalPersistence` (hosts without a database). |
| `ApprovalBinding.cs` | `ApprovalBinding.Of` (gateway calls) and `OfDirectCall` (direct MCP reads): the hash an approval is bound to. |
| `ApprovalGate.cs` | `IApprovalGate` + `ApprovalGate`, `ApprovalSubject`, `ApprovalGateResult`: decides whether a `Confirm` may run now (`CheckAsync`) and uses the approval (`TryConsumeAsync`). Single use, bound to the call's binding and arguments hash, valid ten minutes after the decision. |
| `ApprovalFingerprint.cs` | The hashes that bind an approval: arguments (keys sorted, raw JSON values) and caller + tool + arguments. |
| `ApprovalDisplay.cs` | Display-only card context: risk band from the level, best-effort target from the arguments. |

The agent-specific bridge, [`ApprovalCoordinator`](../Agent/Approvals/), stays in the Agent module.

## Related

- Model: [`../../Models/Agent/Approval.cs`](../../Models/Agent/Approval.cs)
- Gateway: [`../Mutations/`](../Mutations/)
- Tests: `ApprovalStoreTests`, `ApprovalGateTests`, `ApprovalBindingTests`, `ApprovalPersistenceTests`,
  `ApprovalConcurrencyTests`, `ToolAuthorizationTests`, `DirectMcpCallGuardTests`
