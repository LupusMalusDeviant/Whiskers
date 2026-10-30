# Services/Mcp

The **authorization layer for MCP tools**. Validates incoming MCP API keys and enforces per-tool permission levels (Read / Write / Admin). This is the gate every MCP tool call passes through (`McpPermissionCheck`), and the same level model the acting agent's guardrails build on.

## Files

| File | Purpose |
|---|---|
| `IMcpPermissionService.cs` / `McpPermissionService.cs` | Validates MCP API keys and enforces per-tool permission levels; backs the store persisted in `/app/data/mcp-permissions.json`. **A stored tool level may be stricter than the code's `DefaultToolLevels`, never looser** — looser values are raised on load and on save (`EnforceCodeFloor`), so tightening a tool in code also reaches existing installs. Saves work on a copy published after the file is written (never aliasing the caller's objects) and run as `config.mcp_permissions`, `mcp.key_create`, `mcp.key_delete` (Admin, R1-11). |

## Related

- The tools themselves and the `McpPermissionCheck` helper: [`../../Mcp/`](../../Mcp/)
- Tool > level map: [`../../Models/McpPermission.cs`](../../Models/McpPermission.cs) — for mutating tools it is derived from the
  capability catalog [`../Mutations/CapabilityCatalog.cs`](../Mutations/CapabilityCatalog.cs) (R1-05): change a write / admin
  level there first, then the tool's `[McpToolLevel]`; read tools are declared by the attribute alone
- Agent inherits these rights: [`../Agent/AgentPrincipalResolver.cs`](../Agent/AgentPrincipalResolver.cs)
- UI: *Settings > MCP*
- Audit (R1-22): key, tool and level changes record the versioned diff ([`../Mutations/ConfigChange.cs`](../Mutations/ConfigChange.cs)); keys are classified field by field, so
  a key's level change is readable and its hash never shown.
