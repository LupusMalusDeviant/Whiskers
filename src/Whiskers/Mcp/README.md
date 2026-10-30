# Mcp

The **MCP (Model Context Protocol) server**: how external AI agents (e.g. Claude Code) operate Whiskers. This folder holds the request-authentication pipeline and the permission-check helper; the tools themselves live in [`Tools/`](Tools/).

Every MCP request carries a `Bearer <API-KEY>` header. The middleware authenticates it before ASP.NET's default OAuth challenge runs, and each tool calls `McpPermissionCheck` to enforce its Read/Write/Admin level for that key. Keys live in the permission service ([`Services/Mcp`](../Services/Mcp/), created in the UI under *Settings → MCP*); the ServerWatch-era flat `api-keys.json` store and its unscoped fallback were removed in 0.15.0.

## Files

| File | Purpose |
|---|---|
| `McpBearerAuthMiddleware.cs` | Authenticates MCP requests by Bearer token before the Google OAuth challenge kicks in (so API clients aren't redirected to a login page). |
| `McpGuardrailFilter.cs` | `tools/call` filter registered on the MCP server. A tool that maps to a capability (a mutation) runs inside the [Mutation Gateway](../Services/Mutations/): tool switch and level, target resolution, guardrail policy, approval, audit intent and outcome. Any other tool passes `DirectMcpCallGuard`. Reports each call's outcome (executed, denied, failed, pending approval, verification failed; with the gateway's operation id) to `McpCallOutcomes`, keyed by its JSON-RPC id (R1-20). Fails closed when the caller cannot be identified or the guardrails are missing. |
| `Prompts/OperationsPrompts.cs` | User-invokable MCP prompts (`triage_unhealthy_containers`, `security_review_server`, `plan_safe_update`, `investigate_host_load`). Text only — they run nothing; the tools they lead to stay scope- and guardrail-checked. |
| `Resources/WhiskersResources.cs` | Read-only MCP resources about the MCP surface itself: `whiskers://tools/catalog` and `whiskers://me/permissions` (the caller's own scope). No secrets, no infrastructure data. |
| *(repo root)* `lhm.plugin.json` | LobeHub Marketplace manifest. Regenerate after the tool/prompt/resource surface changes or on a release: run Whiskers locally, then `npx -y @lobehub/market-cli plugin init --url http://127.0.0.1:<port>/mcp --dir <tmp> --force`, copy the capability arrays over and set `version`. Never publish a `cloudEndpoint` — Whiskers is self-hosted. |
| `McpCallOutcomes.cs` | `McpCallResult`, `McpCallOutcome` and the per-request list the filter writes and the call log reads (R1-20). |
| `McpCallLogMiddleware.cs` | Records every external/direct `tools/call` (callers that bypass the in-process agent) into the Agent-History log via [`IMcpCallLogStore`](../Services/Observability/). Sniffs the JSON-RPC envelope only, never alters the request, never throws. A call's success, verdict and operation id (`CorrelationId`) are the outcome the filter reported for its JSON-RPC id — each call of a batch its own — never the HTTP status; a call with no reported outcome is recorded as not successful (R1-20). The in-process agent path is logged separately in [`AgentToolInvoker`](../Services/Agent/AgentToolInvoker.cs). |
| `McpPermissionCheck.cs` | Helper called from inside tool methods: extracts the API key (or the web user's role) from the HTTP context and checks the required permission level. Returns a denial message or `null` if allowed. |
| `McpToolLevelAttribute.cs` | `[McpToolLevel(...)]` — declares a tool's minimum permission level **on the method**, next to `[McpServerTool]`. A declaration only: the request path still reads `McpPermissionLevels.DefaultToolLevels`. For a mutating tool it must equal the capability catalog's level ([`../Services/Mutations/CapabilityCatalog.cs`](../Services/Mutations/CapabilityCatalog.cs), R1-05; change the catalog first). It exists because an unlisted tool falls back to `admin`, so a forgotten dictionary entry yields a tool that is registered and listed but denied to the agent on every call, with nothing logged. |
| `McpToolLevelCatalog.cs` | Reads those declarations by reflection and shapes them like `DefaultToolLevels` so the two can be compared, plus the method-name → wire-name (snake_case) derivation. Never on the request path — a bug here fails a test, not a request. Feeds the tool-catalog snapshot (Plan-0013 WP3). |
| `McpToolParameters.cs` | Which tools declare a `serverId` parameter (read once by reflection). The gateway's MCP adapter passes the resolved server to each of them, also when the client left it out, so a tool never acts on another machine than the one the gateway decided on (R1-06). |

| `McpToolCatalogRenderer.cs` | Renders the tool catalog (name, level, module, description) as the Markdown checked in at [`../../../docs/mcp-tool-catalog.md`](../../../docs/mcp-tool-catalog.md). Deterministic output, so a change to the served surface always shows up as a reviewable diff. |

Enforced by three test classes, each answering a different question:

| Test | Question |
|---|---|
| `McpToolLevelTests` | Does every tool declare a level, does it match the dictionary, and does the method's own name reach the permission gate? |
| `McpToolRegistrationTests` | Does each **module** still contribute the tools it is pinned to? |
| `McpToolCatalogSnapshotTests` | Did the surface change without the catalog being updated? |
| `McpServedSurfaceTests` | Does the **running server** answer `tools/list` with exactly those tools? |

The last one is the only one that would have caught the 0.12.0–0.13.0 regression, where the shipped server served zero tools while every code-inspecting check stayed green. Verified by reintroducing that bug: only `McpServedSurfaceTests` went red.

## Two numbers worth looking at now and then

Tests prove the surface is *consistent*. They cannot tell you it is *usable* — that only shows in how the tools
are actually called. Both numbers come from `McpToolCalls` (see [`../Services/Observability/`](../Services/Observability/)):

```sql
-- 1. Tools that are always refused. A rate near 100% is not strictness, it is a wrong level:
--    the tool exists, callers reach for it, and never get through.
SELECT ToolName,
       SUM(CASE WHEN Verdict <> 'allow' THEN 1 ELSE 0 END) * 100.0 / COUNT(*) AS DeniedPct,
       COUNT(*) AS Calls
FROM McpToolCalls
WHERE Timestamp > datetime('now', '-90 days')   -- a look-back window; the history itself is kept Audit:RetentionDays (365)
GROUP BY ToolName
HAVING Calls > 5
ORDER BY DeniedPct DESC;

-- 2. Tools nobody ever calls. A few are fine; many mean the catalog is bloated or the
--    descriptions do not say what the tool is for — the agent picks by description.
SELECT ToolName FROM McpToolCalls
WHERE Timestamp > datetime('now', '-90 days')   -- look-back window, as above
GROUP BY ToolName;   -- compare against docs/mcp-tool-catalog.md
```

There is no dedicated view for this yet; it is a periodic manual check.

## Subfolder

- [`Tools/`](Tools/): the `[McpServerToolType]` classes exposing the actual tools.

## Related

- Permission service & levels: [`../Services/Mcp/`](../Services/Mcp/), [`../Models/McpPermission.cs`](../Models/McpPermission.cs)
- The acting agent reuses this authorization model: [`../Services/Agent/`](../Services/Agent/)
