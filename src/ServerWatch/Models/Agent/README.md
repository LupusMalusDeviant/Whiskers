# Models/Agent

Data types for the acting agent ([`../../Services/Agent/`](../../Services/Agent/)).

## Files

| File | Purpose |
|---|---|
| `AgentDtos.cs` | The agent's **provider-neutral language** — roles, messages, tool calls/definitions, completion requests and streaming deltas. Each provider impl translates these to/from its wire format. |
| `AgentRuntime.cs` | Runtime types — `AgentPrincipal` (who triggered the agent + their rights), `AgentOrigin`, `AgentContext`, `AgentToolResult`, `AgentRunState`, and the UI-directed `AgentEvent` hierarchy. |

## Related

- Implementation: [`../../Services/Agent/`](../../Services/Agent/)
- Providers: [`../../Services/Agent/Providers/`](../../Services/Agent/Providers/)
