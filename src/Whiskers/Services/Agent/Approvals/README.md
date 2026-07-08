# Agent / Approvals

**Human-in-the-Loop**: the central registry of pending approvals. When an agent tool call hits a guardrail `Confirm` verdict, the agent flow pauses and a human must approve or reject it before the tool runs.

## How it fits together

1. [`AgentSession`](../AgentSession.cs) evaluates a tool call > `Confirm` > emits `ConfirmationRequired` and awaits a `TaskCompletionSource<bool>` (via `ResolveConfirmationAsync`).
2. The flow registers an [`Approval`](../../../Models/Agent/Approval.cs) here with a **resolver** callback that bridges back to that waiting session.
3. The notification bell and the **Freigaben** page read `GetPending()` and call `ResolveAsync(id, approved, resolvedBy)`; the resolver unblocks the session.

`ResolveAsync` > resolver(true/false) > the session continues or records "rejected".

## In-memory by design

Approvals are **not** persisted: the resolver targets a live in-process `AgentSession`, which itself does not survive a restart. A process restart cancels pending agent runs, so there is nothing meaningful to resume. Overdue approvals (default 15 min) are swept to `Expired` and resolved as *deny* so a waiting session never hangs; `CancelForSessionAsync` does the same when a session aborts.

## Files

| File | Purpose |
|---|---|
| `ApprovalStore.cs` | `IApprovalStore` + `ApprovalStore`, create / list / resolve / cancel pending approvals, with a `Changed` event for the bell + page and automatic expiry. Registered interface-first as a singleton. |
| `ApprovalCoordinator.cs` | `IApprovalCoordinator` + `ApprovalCoordinator`, bridges a `Confirm` verdict into an approval: registers it in the store (resolver wired to the waiting session) and pushes a notification (in-app bell + Mattermost/Matrix if active). Used by [`Agent.razor`](../../../Components/Pages/Agent.razor) on `ConfirmationRequired`. |

## Related

- Model: [`../../../Models/Agent/Approval.cs`](../../../Models/Agent/Approval.cs)
- Session + confirmation flow: [`../AgentSession.cs`](../AgentSession.cs), [`../IAgentService.cs`](../IAgentService.cs)
- Guardrail verdicts: [`../Guardrails/`](../Guardrails/)
- Notifications: [`../../Notifications/`](../../Notifications/)
