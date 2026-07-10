# Agent (`agent`)

The acting AI agent + AI chat (RoadToSAP Phase 1 §3.8) — the last and largest §3 module. See the code-level
detail in [`src/Whiskers/Modules/Agent/README.md`](../../src/Whiskers/Modules/Agent/README.md).

| | |
|---|---|
| **Id** | `agent` |
| **Default** | on |
| **Toggle** | `Features:agent:Enabled` (env `Features__agent__Enabled=false`), restart-only |
| **Nav** | `agent`, `guardrails`, `approvals`, `ai-triggers` (Automatisierung) |
| **MCP tools** | `instruct_agent` (`AgentTools`) |

## What it owns

The multi-provider agent, its inescapable guardrails, human approvals, the read-only AI-chat advisor widget,
and the event-driven AI triggers — the DI wiring for ~17 services + `AiChat` + the two `IInitializable`
warm-ups (`GuardrailStore` 80, `AiTriggerStore` 90), all moved **verbatim** from `Program.cs`. The service
implementations stay under `Services/Agent/` + `Services/AiChat/`.

## When disabled

- Agent/AI-chat services unregistered; the four pages show the `ModuleGuard` "disabled" notice (their `*View`
  is never instantiated, so no `@inject` DI error).
- The global `<AiChat/>` widget is gated out of `MainLayout` app-wide; the `AI-Chat (Berater)` `Settings.razor`
  panel is gated too.
- `instruct_agent` drops off the MCP surface.

## Soft dependencies (Core, not `DependsOn`)

- **`NoopAiTriggerDispatcher`** (Core, registered before the module loop) — the notification composite
  (Modules/Notifications) resolves `IAiTriggerDispatcher` lazily on every event; the no-op lets that resolve
  cleanly when the agent is off (no autonomous dispatch, not a faked success). The real dispatcher wins by
  last-registration when the module is on.
- **`agent-history` stays Core** — it reads the Core `IMcpCallLogStore` (MCP-call observability, incl.
  external/direct calls), independent of the acting agent, so it is not part of this module.

## Deferred

`AgentToolRegistry`→`ModuleRegistry` (§2.3/§3.8): the agent's tool catalog still reflects a disabled module's
tools (call-time concern at the guardrail/permission gate, never a boot error). Tracked with C8/C10/C12.
