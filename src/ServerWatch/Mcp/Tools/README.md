# Mcp/Tools

The MCP **tool definitions** — the operations exposed to AI agents. Each file is a `[McpServerToolType]` class whose `[McpServerTool]` static methods become snake_case tools (e.g. `GetContainerDetails` → `get_container_details`). Each method gates itself with `McpPermissionCheck` ([`../McpPermissionCheck.cs`](../McpPermissionCheck.cs)) and delegates to the relevant [`Services/`](../../Services/) implementation.

The canonical tool→permission-level map lives in [`../../Models/McpPermission.cs`](../../Models/McpPermission.cs); the full live list is in the UI under *Settings → MCP*.

## Files

| File | Tool group |
|---|---|
| `ContainerTools.cs` | Containers — list/inspect/logs/metrics/env, start/stop/restart/update |
| `ServerTools.cs` | Host & server — info, logs, metrics, health, `execute_command`, firewall, Nginx, systemd, SSL |
| `NetworkTools.cs` | Docker networks — list/create/remove, connect/disconnect |
| `DatabaseTools.cs` | In-container databases — detect, list, schema, query, backup |
| `MonitoringTools.cs` | Deployment + health/update summaries (`deploy_app`, `deploy_compose`, `get_health_summary`, `get_update_status`) |
| `LogTools.cs` | Log search and log alerts |
| `SchedulerTools.cs` | Scheduled tasks — list/create/delete/run |
| `CveTools.cs` | CVE summaries (server/container) |
| `CloudTools.cs` | Out-of-band cloud control (provider-agnostic) |
| `HetznerTools.cs` | Hetzner-specific extras (rescue, backups, snapshots, server type) |
| `AgentTools.cs` | `instruct_agent` — delegate a natural-language task to the in-process agent |

## Related

- Permission gate: [`../McpPermissionCheck.cs`](../McpPermissionCheck.cs)
- Tool discovery for the agent (reflection over these classes): [`../../Services/Agent/AgentToolRegistry.cs`](../../Services/Agent/AgentToolRegistry.cs)
- Business logic: [`../../Services/`](../../Services/)
