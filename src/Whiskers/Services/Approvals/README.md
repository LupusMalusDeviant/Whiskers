# Services/Approvals

**Human-in-the-Loop approvals**, core since ADR-0005 step 2.1 (PC-D8). When the guardrail policy answers `Confirm`,
a human must approve the call before it runs, whether it came from the agent, from a direct MCP client, or (from
WP2 step 2.2 on) from the UI or a job.

## How it fits together

1. **Agent session.** [`AgentSession`](../Agent/AgentSession.cs) pauses on `Confirm`; the page registers an approval
   through [`ApprovalCoordinator`](../Agent/Approvals/ApprovalCoordinator.cs) with the call's correlation id and the
   hash of its arguments. Once a human approves, the session continues, and the Mutation Gateway asks
   `ApprovalGate` whether an approval exists for **this operation** (correlation id + session + tool) with **these
   arguments**, and uses it once.
2. **Direct MCP call** (cannot wait): `ApprovalGate` raises an approval and refuses the call; once a human approved
   it, the identical call (same caller, tool and arguments) runs once within ten minutes (approve-then-retry).
3. The notification bell and the **Freigaben** page read `GetPending()` and call `ResolveAsync(id, approved, resolvedBy)`.

There is no way to grant a call without an approval decided in `IApprovalStore`.

## In-memory for now

Approvals are not persisted yet: the agent's resolver targets a live in-process session, which does not survive a
restart either. Overdue approvals (default 15 min) are swept to `Expired`. Persisted, hash-bound approvals with
expiry checked at execution time follow in WP3 (INV-29…36).

## Files

| File | Purpose |
|---|---|
| `ApprovalStore.cs` | `IApprovalStore` + `ApprovalStore`: create / list / resolve / cancel approvals, a `Changed` event for the bell + page, automatic expiry. A resolver may only approve what its own level could do. |
| `ApprovalGate.cs` | `IApprovalGate` + `ApprovalGate`, `ApprovalSubject`, `ApprovalGateResult`: decides whether a `Confirm` may run now. Single use, bound to the arguments hash, valid ten minutes after the decision. |
| `ApprovalFingerprint.cs` | The hashes that bind an approval: arguments (keys sorted, raw JSON values) and caller + tool + arguments. |
| `ApprovalDisplay.cs` | Display-only card context: risk band from the level, best-effort target from the arguments. |

The agent-specific bridge, [`ApprovalCoordinator`](../Agent/Approvals/), stays in the Agent module.

## Related

- Model: [`../../Models/Agent/Approval.cs`](../../Models/Agent/Approval.cs)
- Gateway: [`../Mutations/`](../Mutations/)
- Tests: `ApprovalStoreTests`, `ApprovalGateTests`, `ToolAuthorizationTests`, `DirectMcpCallGuardTests`
