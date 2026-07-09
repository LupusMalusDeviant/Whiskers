# Module: terminal

The interactive web terminal — a container shell (`/terminal/{serverId}/{containerId}`) and a host/server
shell (`/server-terminal/{serverId}`).

| | |
|---|---|
| **Id** | `terminal` |
| **Enabled by default** | yes |
| **Toggle** | `Features:terminal:Enabled` (env `Features__terminal__Enabled=false`) — restart required |
| **Depends on** | — |
| **Nav** | none (opened from a container/server page) |
| **MCP tools** | none |
| **Services** | `ITerminalSessionManager` (+ `TerminalSettings`) |

When disabled, the pages render a "module disabled" notice (via `ModuleGuard`), the terminal service is not
registered, and the Terminal panel in *Settings* is hidden. Settings: `TerminalSettings` (`Terminal` config
section) — default shell, max sessions, idle timeout.

Code: [`src/Whiskers/Modules/Terminal/`](../../src/Whiskers/Modules/Terminal/) · session service in
[`src/Whiskers/Services/Terminal/`](../../src/Whiskers/Services/Terminal/).
