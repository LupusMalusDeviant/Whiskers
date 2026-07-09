# Module: logmonitor

Container-log search plus a background log-pattern monitor: full-text/regex search across container logs, alert
rules that fire notifications when a pattern appears, the `/logs` page and the log MCP tools.

| | |
|---|---|
| **Id** | `logmonitor` |
| **Enabled by default** | yes |
| **Toggle** | `Features:logmonitor:Enabled` (env `Features__logmonitor__Enabled=false`) — restart required |
| **Depends on** | — (soft dependency on Core via `ILogMonitorService` / `NoopLogMonitorService`) |
| **Nav** | `logs` — "Log-Suche" (group *Übersicht*) |
| **MCP tools** | `search_logs`, `create_log_alert`, `list_log_alerts` |
| **Services** | `ILogSearchService` + `ILogMonitorService` (hosted `LogMonitorService`) |

When **disabled**: the hosted monitor doesn't run, the `logs` nav entry and the three MCP tools disappear, and
`/logs` shows a "module disabled" notice (via `ModuleGuard`).

**Soft dependency:** the Core AI-triggers page reads/creates log-alert rules through `ILogMonitorService`, so a
`NoopLogMonitorService` default keeps that page working when the module is off (rules aren't persisted and
nothing scans). The real hosted monitor overrides the no-op when enabled. `ILogSearchService` has no Core
consumer and moves cleanly.

No settings section (search + rules are managed on the `/logs` page, not in *Settings*).

Code: [`src/Whiskers/Modules/LogMonitor/`](../../src/Whiskers/Modules/LogMonitor/) · services in
[`src/Whiskers/Services/LogMonitor/`](../../src/Whiskers/Services/LogMonitor/) · tools in
[`src/Whiskers/Mcp/Tools/LogTools.cs`](../../src/Whiskers/Mcp/Tools/LogTools.cs).
