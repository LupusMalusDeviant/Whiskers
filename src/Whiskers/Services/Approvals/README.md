# Services/Approvals

**Human-in-the-Loop approvals**, core since ADR-0005 step 2.1 (PC-D8). When the guardrail policy answers `Confirm`,
a human must approve the call before it runs, whether it came from the agent, from a direct MCP client, or (from
WP2 step 2.2 on) from the UI. Unattended jobs (scheduler, AutoUpdate, AutoPatch) never come here: the gateway refuses
their `Confirm` outright, since nobody could approve it and a later run of the same job is not a retry (R1-01).

## How it fits together

1. **Agent session.** [`AgentSession`](../Agent/AgentSession.cs) pauses on `Confirm`; the page registers an approval
   through [`ApprovalCoordinator`](../Agent/Approvals/ApprovalCoordinator.cs) with the call's correlation id and the
   hash of its arguments. Once a human approves, the session continues, and the Mutation Gateway asks
   `ApprovalGate` whether an approval exists for **this operation** (correlation id + session + tool) with **these
   arguments**. The session pauses on the same level the gateway uses: the tool's level (stricter if stored in Settings → MCP)
   raised to the operation's **effective** level (`IEffectiveRequirementResolver`, R1-05 — e.g. Admin for a
   scheduled custom command). An approval below the current effective level is never used (`ApprovalGate`), and
   the approver must hold that level.
2. **Direct MCP call** (cannot wait): `ApprovalGate` raises an approval and refuses the call; once a human approved
   it, the identical call (same caller, tool and arguments) runs once within ten minutes (approve-then-retry).
   Concurrent identical first calls share one approval. Retry entries whose approval was rejected, expired or used
   are dropped, so the next call asks again.
3. **Checking is not using.** `CheckAsync` only finds (or raises) the approval. The caller uses it with
   `TryConsume` right before the call runs: the gateway after its audit intent is written, `DirectMcpCallGuard`
   immediately. A refusal in between leaves the approval valid; of two concurrent calls only one consumes it.
4. **Which decision.** An approval names the recorded policy decision that asked for it (`Approval.DecisionId`,
   INV-22) and that decision's preset (`GuardrailPreset` — a trigger's pinned one, not whichever is active by then).
   Before an agent session pauses it has the gateway decide and record the call (`IMutationGateway.DecideAsync`),
   so its approval names that decision too, also when the human rejects it. Binding the approval to that decision's
   policy version is WP3 (R1-14).
5. The notification bell and the **Freigaben** page read `GetPending()` and call `ResolveAsync(id, approved, resolvedBy)`.

There is no way to grant a call without an approval decided in `IApprovalStore`.

## In-memory for now

Approvals are not persisted yet: the agent's resolver targets a live in-process session, which does not survive a
restart either. Overdue approvals (default 15 min) are swept to `Expired`. Persisted, hash-bound approvals with
expiry checked at execution time follow in WP3 (INV-29…36).

## Files

| File | Purpose |
|---|---|
| `ApprovalStore.cs` | `IApprovalStore` + `ApprovalStore`: create / list / resolve / cancel approvals, a `Changed` event for the bell + page, automatic expiry. A resolver may only approve what its own level could do. |
| `ApprovalGate.cs` | `IApprovalGate` + `ApprovalGate`, `ApprovalSubject`, `ApprovalGateResult`: decides whether a `Confirm` may run now (`CheckAsync`) and uses the approval (`TryConsume`). Single use, bound to the arguments hash, valid ten minutes after the decision. |
| `ApprovalFingerprint.cs` | The hashes that bind an approval: arguments (keys sorted, raw JSON values) and caller + tool + arguments. |
| `ApprovalDisplay.cs` | Display-only card context: risk band from the level, best-effort target from the arguments. |

The agent-specific bridge, [`ApprovalCoordinator`](../Agent/Approvals/), stays in the Agent module.

## Related

- Model: [`../../Models/Agent/Approval.cs`](../../Models/Agent/Approval.cs)
- Gateway: [`../Mutations/`](../Mutations/)
- Tests: `ApprovalStoreTests`, `ApprovalGateTests`, `ToolAuthorizationTests`, `DirectMcpCallGuardTests`
