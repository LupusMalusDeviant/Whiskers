# Mcp/Tools

The MCP **tool definitions**: the operations exposed to AI agents. Each file is a `[McpServerToolType]` class whose `[McpServerTool]` static methods become snake_case tools (e.g. `GetContainerDetails` > `get_container_details`). Each method gates itself with `McpPermissionCheck` ([`../McpPermissionCheck.cs`](../McpPermissionCheck.cs)) and delegates to the relevant [`Services/`](../../Services/) implementation.

The authoritative level of every mutating tool is the capability table [`../../Services/Mutations/CapabilityCatalog.cs`](../../Services/Mutations/CapabilityCatalog.cs) (R1-05); `DefaultToolLevels` in [`../../Models/McpPermission.cs`](../../Models/McpPermission.cs) takes its mutating entries from it and adds the read tools; the full live list is in the UI under *Settings > MCP*.

## Files

| File | Tool group |
|---|---|
| `ContainerTools.cs` | Containers, list/inspect/logs/metrics/env, start/stop/restart/update |
| `ServerTools.cs` | Host & server, info, logs, metrics, health, `execute_command`, firewall, Nginx, systemd, SSL |
| `NetworkTools.cs` | Docker networks, list/create/remove, connect/disconnect |
| `DatabaseTools.cs` | In-container databases, detect, list, schema, query, backup |
| `MonitoringTools.cs` | Deployment + health/update summaries (`deploy_app`, `deploy_compose`, `get_health_summary`, `get_update_status`) |
| `LogTools.cs` | Log search and log alerts |
| `SchedulerTools.cs` | Scheduled tasks, list/create/delete/run |
| `CveTools.cs` | CVE summaries (server/container) + `list_cve_groups` (de-duplicated: one CVE-ID with all affected targets, age, fix availability) |
| `CloudTools.cs` | Out-of-band cloud control (provider-agnostic) |
| `HetznerTools.cs` | Hetzner-specific extras (rescue, backups, snapshots, server type) |
| `DnsTools.cs` | `list_dns_records`, `set_dns_record`, `delete_dns_record` — zone records at the configured DNS provider (Infomaniak); one global token under *Settings > DNS*, zone-fenced, A/AAAA/CNAME/TXT only, `set` is idempotent |
| `AgentTools.cs` | `instruct_agent`, delegate a natural-language task to the in-process agent |
| `GitDeployTools.cs` | `list_git_deploy_apps` — repos, branches, last deploy outcome. **Read-only** (Plan-0013 WP4) |
| `VolumeBackupTools.cs` | `list_volume_backups`, `list_volumes` — answers "when was this volume last backed up?". **Read-only** |
| `NotificationTools.cs` | `list_recent_alerts` — the alert history Whiskers has raised. **Read-only** |
| `AutoPatchTools.cs` | `get_auto_patch_status` (read) — last nightly security auto-patch report; `preview_auto_patch` (write) — dry run in the background, recreates nothing. No tool applies patches. See [`../../Services/AutoPatch/`](../../Services/AutoPatch/) |
| `McpInputValidation.cs` | Boundary input-validation helpers (safe project name, unambiguous container resolution) |

The three read-only groups closed real gaps: their modules contributed no tools at all, so the agent could not
see deploy outcomes, backup age, or the alerts Whiskers itself had already raised — it re-derived them from raw
logs or missed them. Their write counterparts are deliberately absent: starting a deploy belongs with GAP-3's
health check and automatic rollback, restoring a volume overwrites live data, and an agent that can send
notifications can flood the one channel that has to stay trustworthy.

Every tool declares its permission level via `[McpToolLevel]` and appears in
[`docs/mcp-tool-catalog.md`](../../../../docs/mcp-tool-catalog.md); both are build-enforced — see
[`../README.md`](../README.md).

## Behaviour notes

- **Compose plans** (R1-07): `deploy_compose` and `set_container_env` are planned by the gateway's resolver before policy — every existing container the deployment recreates, removes or collides with is a related resource of the decision — and the tool plans again, refuses if a container was not decided, and runs the plan with explicit `-p` / `-f`. `DotEnvMerge` (in `ContainerTools.cs`) is the one merge both use.
- **Input validation** at the boundary (`McpInputValidation.cs`): `deploy_compose` rejects unsafe project names (`..` / leading-non-alphanumeric) and quotes the target directory; container ids resolve to a **unique** match (exact id/name, else a single id-prefix) — an ambiguous prefix or no match returns a clear error instead of acting on the wrong container.

## Related

- Permission gate: [`../McpPermissionCheck.cs`](../McpPermissionCheck.cs)
- Tool discovery for the agent (reflection over these classes): [`../../Services/Agent/AgentToolRegistry.cs`](../../Services/Agent/AgentToolRegistry.cs)
- Business logic: [`../../Services/`](../../Services/)
