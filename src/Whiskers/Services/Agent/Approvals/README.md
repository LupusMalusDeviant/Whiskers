# Agent / Approvals

The agent's bridge into the core [approvals](../../Approvals/): when an agent tool call hits a guardrail `Confirm`,
the session pauses and a human must approve or reject the call before it runs.

## How it fits together

1. [`AgentSession`](../AgentSession.cs) evaluates a tool call > `Confirm` > emits `ConfirmationRequired` and awaits a `TaskCompletionSource<bool>` keyed by the call's **operation id** (`AgentToolCall.CorrelationId`, unique per call — never the provider's tool-call id, which providers reuse across turns; R1-15, C-18).
2. The page calls `ApprovalCoordinator.RaiseAsync`, which registers an [`Approval`](../../../Models/Agent/Approval.cs) in the core `IApprovalStore` with the call's correlation id, the hash of its arguments, the person chatting as requester, and a **resolver** callback that bridges back to the waiting session.
3. The chat, the notification bell and the **Approvals** page call `DecideAsync(id, approved, ApprovalResolver)` as the signed-in user at their current role; the store applies the eligibility rules (see [`../../Approvals/`](../../Approvals/)) and the resolver unblocks the session.
4. For a mutating tool, the Mutation Gateway then checks that this approval exists, was approved, matches the arguments and is used only once (`ApprovalGate`). A session that continues without a registered approval does not execute the mutation.
5. **The chat ends, the session ends (R1-15, INV-36).** The chat page and the chat widget hold an `AgentChatLifetime`. Clearing the chat, closing the widget, leaving the page or a dropped browser connection (`ChatConnectionWatcher`, a `CircuitHandler`, fires before Blazor disposes the kept circuit) ends it: the session's run is cancelled first (an ended run takes no further step, not even the next call of the same turn), then its pending approvals are cancelled — approving one elsewhere afterwards changes nothing.

## Files

| File | Purpose |
|---|---|
| `ApprovalCoordinator.cs` | `IApprovalCoordinator` + `ApprovalCoordinator`: registers the approval (resolver wired to the waiting session by operation id, arguments hash for the gateway, the requester) and pushes a notification with the approval's id, risk, target and link (in-app bell + Mattermost/Matrix if active). Used by [`AgentView.razor`](../../../Components/Pages/AgentView.razor) and the chat widget on `ConfirmationRequired`. |
| `AgentChatLifetime.cs` | `AgentChatLifetime`: the run token of a chat's session and the end of the chat — stops the run, then cancels the session's pending approvals; also on a dropped connection. The next message starts a new run. |
| `ChatConnectionWatcher.cs` | `IChatConnectionWatcher` + `ChatConnectionWatcher` (scoped `CircuitHandler`): tells the chats of a circuit that its browser connection dropped. |

## Related

- Core approvals (store, gate): [`../../Approvals/`](../../Approvals/)
- Session + confirmation flow: [`../AgentSession.cs`](../AgentSession.cs), [`../IAgentService.cs`](../IAgentService.cs)
- Guardrail verdicts: [`../../Guardrails/`](../../Guardrails/)
- Notifications: [`../../Notifications/`](../../Notifications/)
