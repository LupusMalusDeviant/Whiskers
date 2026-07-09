# Module catalog

Per-module reference for the RoadToSAP module system — see [../roadmap/RoadToSAP.md](../roadmap/RoadToSAP.md)
and the framework overview in [`src/Whiskers/Modules/README.md`](../../src/Whiskers/Modules/README.md).

Toggle a module with `Features:{id}:Enabled` (e.g. `Features__terminal__Enabled=false`), restart-only.

| Module | Id | Default | Nav | MCP tools |
|---|---|---|---|---|
| [Terminal](terminal.md) | `terminal` | on | — | — |
| [Notifications](notifications.md) | `notifications` | on | — | — |
| [Scheduler](scheduler.md) | `scheduler` | on | `tasks` | `list/create/delete/run_scheduled_task` |

Everything not yet extracted still lives in the transitional `AllInOnePseudoModule`; this index grows one row
per module PR.
