# Mcp

The **MCP (Model Context Protocol) server**: how external AI agents (e.g. Claude Code) operate ServerWatch. This folder holds the request-authentication pipeline and the permission-check helper; the tools themselves live in [`Tools/`](Tools/).

Every MCP request carries a `Bearer <API-KEY>` header. The middleware authenticates it before ASP.NET's default OAuth challenge runs, and each tool calls `McpPermissionCheck` to enforce its Read/Write/Admin level for that key.

## Files

| File | Purpose |
|---|---|
| `McpBearerAuthMiddleware.cs` | Authenticates MCP requests by Bearer token before the Google OAuth challenge kicks in (so API clients aren't redirected to a login page). |
| `McpCallLogMiddleware.cs` | Records every external/direct `tools/call` (callers that bypass the in-process agent) into the Agent-History log via [`IMcpCallLogStore`](../Services/Observability/). Sniffs the JSON-RPC envelope only, never alters the request, never throws. The in-process agent path is logged separately in [`AgentToolInvoker`](../Services/Agent/AgentToolInvoker.cs). |
| `McpApiKeyAuth.cs` | `McpApiKeyStore`, the API-key store backing authentication. |
| `IMcpApiKeyStore.cs` | Legacy flat API-key store interface (kept for backwards compatibility). |
| `McpPermissionCheck.cs` | Helper called from inside tool methods: extracts the API key (or the web user's role) from the HTTP context and checks the required permission level. Returns a denial message or `null` if allowed. |

## Subfolder

- [`Tools/`](Tools/): the `[McpServerToolType]` classes exposing the actual tools.

## Related

- Permission service & levels: [`../Services/Mcp/`](../Services/Mcp/), [`../Models/McpPermission.cs`](../Models/McpPermission.cs)
- The acting agent reuses this authorization model: [`../Services/Agent/`](../Services/Agent/)
