# Modules/Agent

The **acting AI agent + AI chat** as a toggleable module (RoadToSAP Phase 1 §3.8) — the largest and most
security-sensitive extraction: the multi-provider agent, its approval bridge
([`ApprovalCoordinator`](../../Services/Agent/Approvals/)), the read-only AI-chat advisor widget, and the event-driven
[AI triggers](../../Services/Agent/Triggers/). The [guardrails](../../Services/Guardrails/) and the
[approval store](../../Services/Approvals/) the agent obeys are **core** since ADR-0005 step 2.1 (PC-D8): they guard
direct MCP calls too and must not switch off with this module.

## What it registers (`ConfigureServices`, moved verbatim)

- **AI chat:** `AiChatSettings`, the `AiChatService` HTTP client + `IAiChatService`, `IChatHistoryStore`.
- **Agent:** `AgentSettings`, `IAgentProviderFactory`, `IAgentToolCatalog`, `IAgentToolInvoker`,
  `IApprovalCoordinator`, `IChatWidgetParser`, `IAgentService`, `IClaudeCodeRuntime`, `IAgentTranscriptStore`,
  `IAgentSettingsStore`.
- **Not here (core):** the guardrail engine, `IGuardrailStore`, `IGuardrailRuleCatalog`, `IAgentToolRegistry` (tool
  metadata for the guardrail editor), `IAgentPrincipalResolver`, `IApprovalStore`, `IApprovalGate`,
  `IDirectMcpCallGuard` — see `AddGuardrailPolicy` in [`Startup/WhiskersMutationGatewayExtensions.cs`](../../Startup/WhiskersMutationGatewayExtensions.cs).
- **AI triggers:** `AiTriggerStore`/`IAiTriggerStore`, `IAiTriggerDispatcher`.
- **Warm-up:** the `AiTriggerStore` `IInitializable` (order 90), run only when the module is enabled. The
  `GuardrailStore` warm-up (order 80) is core.

The service **implementation** classes stay in [`../../Services/Agent/`](../../Services/Agent/) and
[`../../Services/AiChat/`](../../Services/AiChat/); only their DI wiring lives here.

## Nav + MCP

- **Nav** (Automatisierung): `agent` (340), `ai-operations` (345), `ai-triggers` (370). `guardrails` (350) and
  `approvals` (360) are core entries.
- **MCP:** [`AgentTools`](../../Mcp/Tools/AgentTools.cs) — the `instruct_agent` tool. Disabling the module
  drops it from both the external MCP surface and the in-process agent's own catalog.

## What "disabled" does (`Features:agent:Enabled=false`, restart-only)

The whole surface goes away at once, cleanly:

- The agent/AI-chat services are never registered (no provider clients, no hosted work).
- The `agent` / `ai-operations` / `ai-triggers` pages are thin `ModuleGuard` wrappers over their
  interactive `*View.razor` — the view (which `@inject`s the agent services) is only instantiated when enabled,
  so a disabled route shows the "module disabled" notice instead of a DI error.
- The global `<AiChat/>` widget is gated in [`MainLayout.razor`](../../Components/Layout/MainLayout.razor) with
  `@if (ModuleRegistry.IsEnabled("agent"))`, so it disappears app-wide instead of throwing on every page.
- The `AI-Chat (Berater)` panel in `Settings.razor` is gated the same way.
- `instruct_agent` drops off the MCP surface.
- Guardrails and approvals stay: direct MCP calls are still decided by the guardrail policy and the Mutation
  Gateway, and the Guardrails and Approvals pages stay reachable (`GuardrailModuleIndependenceTests`, INV-26).

## Two soft dependencies (handled in Core, not via `DependsOn`)

1. **Notification composite → `IAiTriggerDispatcher`.** `CompositeNotificationService` (Modules/Notifications)
   resolves the dispatcher **lazily** on every event (to avoid a DI cycle). Core registers a
   [`NoopAiTriggerDispatcher`](../../Services/Agent/Triggers/NoopAiTriggerDispatcher.cs) **before** the module
   loop; the real `AiTriggerDispatcher` (registered inside the loop) wins by last-registration when the module
   is on. With the agent off there is deliberately no autonomous trigger dispatch — a genuine absence of
   behaviour, not a faked success.
2. **`agent-history` stays Core.** That page reads the Core `IMcpCallLogStore` (MCP-call observability, which
   records external/direct MCP calls too), so it is **not** part of this module — it works regardless of the
   agent.

## Known limitation (roadmap-deferred)

The `AgentToolRegistry`→`ModuleRegistry` change (RoadToSAP §2.3/§3.8) is **deferred** (like C8/C10/C12): the
registry still discovers tool methods via reflection, so the agent's own tool *catalog* can list a disabled
module's tools. That is a call-time concern (a call fails cleanly at the guardrail/permission gate), never a
boot error, and it does not affect the agent module's own on/off behaviour.

See [`docs/modules/agent.md`](../../../../docs/modules/agent.md) and the module-system chapter in
[`docs/ARCHITECTURE.md`](../../../../docs/ARCHITECTURE.md).
