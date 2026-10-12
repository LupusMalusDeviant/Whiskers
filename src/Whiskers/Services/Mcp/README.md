# Services/Mcp

The **authorization layer for MCP tools**. Validates incoming MCP API keys and enforces per-tool permission levels (Read / Write / Admin). This is the gate every MCP tool call passes through (`McpPermissionCheck`), and the same level model the acting agent's guardrails build on.

## Files

| File | Purpose |
|---|---|
| `IMcpPermissionService.cs` / `McpPermissionService.cs` | Validates MCP API keys and enforces per-tool permission levels; backs the store persisted in `/app/data/mcp-permissions.json`. **A stored tool level may be stricter than the code's `DefaultToolLevels`, never looser** — looser values are raised on load and on save (`EnforceCodeFloor`), so tightening a tool in code also reaches existing installs. |

## Related

- The tools themselves and the `McpPermissionCheck` helper: [`../../Mcp/`](../../Mcp/)
- Canonical tool > level map: [`../../Models/McpPermission.cs`](../../Models/McpPermission.cs)
- Agent inherits these rights: [`../Agent/AgentPrincipalResolver.cs`](../Agent/AgentPrincipalResolver.cs)
- UI: *Settings > MCP*
