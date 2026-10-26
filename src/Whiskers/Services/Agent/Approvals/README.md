# Agent / Approvals

The agent's bridge into the core [approvals](../../Approvals/): when an agent tool call hits a guardrail `Confirm`,
the session pauses and a human must approve or reject the call before it runs.

## How it fits together

1. [`AgentSession`](../AgentSession.cs) evaluates a tool call > `Confirm` > emits `ConfirmationRequired` and awaits a `TaskCompletionSource<bool>` (via `ResolveConfirmationAsync`).
2. The page calls `ApprovalCoordinator.RaiseAsync`, which registers an [`Approval`](../../../Models/Agent/Approval.cs) in the core `IApprovalStore` with the call's correlation id, the hash of its arguments, and a **resolver** callback that bridges back to the waiting session.
3. The notification bell and the **Freigaben** page call `ResolveAsync(id, approved, resolvedBy)`; the resolver unblocks the session.
4. For a mutating tool, the Mutation Gateway then checks that this approval exists, was approved, matches the arguments and is used only once (`ApprovalGate`). A session that continues without a registered approval does not execute the mutation.

## Files

| File | Purpose |
|---|---|
| `ApprovalCoordinator.cs` | `IApprovalCoordinator` + `ApprovalCoordinator`: registers the approval (resolver wired to the waiting session, arguments hash for the gateway) and pushes a notification (in-app bell + Mattermost/Matrix if active). Used by [`AgentView.razor`](../../../Components/Pages/AgentView.razor) and the chat widget on `ConfirmationRequired`. |

## Related

- Core approvals (store, gate): [`../../Approvals/`](../../Approvals/)
- Session + confirmation flow: [`../AgentSession.cs`](../AgentSession.cs), [`../IAgentService.cs`](../IAgentService.cs)
- Guardrail verdicts: [`../../Guardrails/`](../../Guardrails/)
- Notifications: [`../../Notifications/`](../../Notifications/)
