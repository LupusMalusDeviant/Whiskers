# Module catalog

Per-module reference for the RoadToSAP module system — see [../roadmap/RoadToSAP.md](../roadmap/RoadToSAP.md)
and the framework overview in [`src/Whiskers/Modules/README.md`](../../src/Whiskers/Modules/README.md).

Toggle a module with `Features:{id}:Enabled` (e.g. `Features__terminal__Enabled=false`), restart-only.

| Module | Id | Default | Nav | MCP tools |
|---|---|---|---|---|
| [Terminal](terminal.md) | `terminal` | on | — | — |
| [Notifications](notifications.md) | `notifications` | on | — | — |
| [Scheduler](scheduler.md) | `scheduler` | on | `tasks` | `list/create/delete/run_scheduled_task` |
| [LogMonitor](logmonitor.md) | `logmonitor` | on | `logs` | `search_logs`, `create/list_log_alert(s)` |
| [VolumeBackups](volumebackups.md) | `volumebackups` | on | `backups` | — |
| [Webhooks](webhooks.md) | `webhooks` | on | `webhooks` | — |
| [HostManagement](host-management.md) | `host-management` | on | — (per-server pages) | — (in Core `ServerTools`) |
| [Deployment](deployment.md) | `deployment` | on | `deploy`, `apps` | — (in Core `ContainerTools`) |
| [Cve](cve.md) | `cve` | on | `cves` | `get_cve_summary`, `list_cve_groups`, `get_container/server_cves` |
| [CloudControl](cloud-control.md) | `cloud-control` | on | `cloud` | `list_cloud_servers`, `cloud_*`, `hetzner_*` |
| [ImageUpdate](image-updates.md) | `image-updates` | on | — (Dashboard) | — (`check_updates`/`update_container` in Core `ContainerTools`) |
| [HelloWorld](../../src/Whiskers/Modules/HelloWorld/README.md) | `hello-world` | **off** | `hello-world` | — |

Everything not yet extracted still lives in the transitional `AllInOnePseudoModule` (e.g. the `compose`
editor, which uses only Core Docker/host services). This index grows one row per module PR.

**HelloWorld** is the odd one out: a deliberately minimal, **disabled-by-default** example module (RoadToSAP §6
DoD) — living documentation for the contract, not a feature. Copy `Modules/HelloWorld/` to start a new module.
