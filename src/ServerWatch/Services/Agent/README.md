# Services/Agent

The **acting agent**: you give it an operations task in natural language and it plans and executes using ServerWatch's own MCP tools. It is provider-agnostic (OpenAI, OpenRouter, Ollama, Gemini, Anthropic, and Claude Code) and bounded by inescapable [guardrails](Guardrails/) enforced at the tool-execution boundary — never in the prompt.

Two entry points feed the same loop: the UI ([`../../Components/Pages/Agent.razor`](../../Components/Pages/Agent.razor)) and the `instruct_agent` MCP tool ([`../../Mcp/Tools/AgentTools.cs`](../../Mcp/Tools/AgentTools.cs)). The agent inherits exactly the rights of whoever triggered it (web user or MCP key) and can never exceed them.

## Files

| File | Purpose |
|---|---|
| `IAgentService.cs` | Public surface: `IAgentService` (create/resume sessions) and `IAgentSession` (drive a turn, resolve confirmations, expose history). |
| `AgentService.cs` | Creates/manages bounded in-memory `AgentSession`s, resolves the provider from settings, holds the German system prompt. |
| `AgentSession.cs` | Drives the agentic loop for one conversation: stream the provider → run tool calls through the guardrail gate → pause on Confirm → feed results back, until Stop or `MaxToolIterations`. Enforces the per-session action rate limit. |
| `IAgentTooling.cs` | Interfaces for the tool layer: `IAgentToolRegistry`, `IAgentToolCatalog`, `IAgentToolInvoker`, `IAgentPrincipalResolver`. |
| `AgentToolRegistry.cs` | Discovers the `[McpServerTool]` methods once via reflection, derives LLM function schemas, cross-checks against the canonical permission levels. Excludes agent-disallowed tools (e.g. `instruct_agent`). |
| `AgentToolCatalog.cs` | Returns the function definitions visible to a principal given role + guardrails (hard-blocked tools are never shown to the model). |
| `AgentToolInvoker.cs` | Executes exactly one tool call **after** the guardrail gate; invokes the real MCP method via reflection under a synthetic HttpContext that records the agent in the audit log. |
| `AgentArgumentBinder.cs` | Converts LLM JSON argument values into the .NET parameter types of tool methods (tolerant of LLM quirks, no silent data loss). |
| `AgentPrincipalResolver.cs` | Derives the `AgentPrincipal` from the HTTP context (bearer MCP key or cookie web user), mirroring `McpPermissionCheck`. |
| `AgentSettingsStore.cs` | Writes the UI-editable provider settings to `/app/data/agent-settings.json` (a reload-on-change config source). |
| `AgentTranscriptStore.cs` | Per-user persistence of the agent conversation (survives reloads; seeds new sessions with context). |
| `IClaudeCodeRuntime.cs` | Interface for the Claude Code runtime (deliberately **not** an LLM provider — it brings its own loop). |
| `ClaudeCodeRuntime.cs` | Orchestrates the Claude Code CLI as a subprocess, configured via `--mcp-config` to point back at ServerWatch's `/mcp` endpoint so its tool calls run through the same guardrail gate. |
| `ClaudeCodeOutputParser.cs` | Pure, testable translation of Claude Code stream-json lines into `AgentEvent`s. |

## Subfolders

- [`Guardrails/`](Guardrails/) — the code-enforced security policy, presets and rule engine.
- [`Providers/`](Providers/) — one LLM wire-format implementation per provider.
- [`Triggers/`](Triggers/) — AI triggers: run the agent autonomously on events.

## Related

- Models: [`../../Models/Agent/`](../../Models/Agent/) (`AgentDtos`, `AgentRuntime`)
- Config: [`../../Configuration/AgentSettings.cs`](../../Configuration/AgentSettings.cs)
- Tests: [`../../../ServerWatch.Tests/`](../../../ServerWatch.Tests/) (`AgentService*`, `AgentSession`, `AgentToolInvoker`, `AgentToolRegistry`, `ClaudeCodeOutputParser`, …)
