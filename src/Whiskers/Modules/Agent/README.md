# Modules/Agent

The **acting AI agent + AI chat** as a toggleable module (RoadToSAP Phase 1 §3.8) — the largest and most
security-sensitive extraction: the multi-provider agent, its inescapable [guardrails](../../Services/Agent/Guardrails/),
human [approvals](../../Services/Agent/Approvals/), the read-only AI-chat advisor widget, and the event-driven
[AI triggers](../../Services/Agent/Triggers/). All registrations are moved **verbatim** from `Program.cs`
(relocation, not a rewrite) — the guardrail/approval/tool-invoker boundary is unchanged, only *where* it is wired.

## What it registers (`ConfigureServices`, moved verbatim)

- **AI chat:** `AiChatSettings`, the `AiChatService` HTTP client + `IAiChatService`, `IChatHistoryStore`.
- **Agent:** `AgentSettings`, `IAgentToolRegistry`, the `IAgentGuardrailEngine` default, `GuardrailStore`/
  `IGuardrailStore`, `IGuardrailRuleCatalog`, `IAgentProviderFactory`, `IAgentToolCatalog`, `IAgentToolInvoker`,
  `IAgentPrincipalResolver`, `IApprovalStore`, `IApprovalCoordinator`, `IChatWidgetParser`, `IAgentService`,
  `IClaudeCodeRuntime`, `IAgentTranscriptStore`, `IAgentSettingsStore`.
- **AI triggers:** `AiTriggerStore`/`IAiTriggerStore`, `IAiTriggerDispatcher`.
- **Warm-ups:** the two `IInitializable`s — `GuardrailStore` (order 80) and `AiTriggerStore` (order 90) — moved
  from the Core init block; they run in order only when the module is enabled.

The service **implementation** classes stay in [`../../Services/Agent/`](../../Services/Agent/) and
[`../../Services/AiChat/`](../../Services/AiChat/); only their DI wiring lives here.

## Nav + MCP

- **Nav** (Automatisierung): `agent` (340), `guardrails` (350), `approvals` (360), `ai-triggers` (370).
- **MCP:** [`AgentTools`](../../Mcp/Tools/AgentTools.cs) — the `instruct_agent` tool. Disabling the module
  drops it from both the external MCP surface and the in-process agent's own catalog.

## What "disabled" does (`Features:agent:Enabled=false`, restart-only)

The whole surface goes away at once, cleanly:

- The agent/AI-chat services are never registered (no provider clients, no hosted work).
- The `agent` / `guardrails` / `approvals` / `ai-triggers` pages are thin `ModuleGuard` wrappers over their
  interactive `*View.razor` — the view (which `@inject`s the agent services) is only instantiated when enabled,
  so a disabled route shows the "module disabled" notice instead of a DI error.
- The global `<AiChat/>` widget is gated in [`MainLayout.razor`](../../Components/Layout/MainLayout.razor) with
  `@if (ModuleRegistry.IsEnabled("agent"))`, so it disappears app-wide instead of throwing on every page.
- The `AI-Chat (Berater)` panel in `Settings.razor` is gated the same way.
- `instruct_agent` drops off the MCP surface.

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
