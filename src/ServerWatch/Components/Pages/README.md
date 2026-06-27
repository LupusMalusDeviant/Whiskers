# Components/Pages

The routable Blazor pages. Each `.razor` here is a screen in the app; some have a co-located dialog component. Pages depend on the [`Services/`](../../Services/) interfaces and are protected by role/auth where appropriate.

## Overview & containers

| Page | Purpose |
|---|---|
| `Dashboard.razor` | The main live dashboard — all containers, health, metrics, update badges. |
| `ContainerDetail.razor` | Deep view of one container: overview, stats, logs, health, terminal, env, and (for DB containers) a database tab. |
| `ContainerGraph.razor` | Visual graph of containers and their relationships. |
| `ContainerDiff.razor` | Compare container configurations. |
| `HealthReports.razor` | Historical health reports. |

## Servers & infrastructure

| Page | Purpose |
|---|---|
| `Servers.razor` | The fleet: configured servers, add/edit, onboarding. |
| `Terminal.razor` / `ServerTerminal.razor` | Web terminal (container / host). |
| `Firewall.razor` (+ `AddFirewallRuleDialog.razor`) | ufw firewall management. |
| `NginxManager.razor` | Nginx site configuration editor. |
| `SystemdManager.razor` | systemd service control. |
| `SslCerts.razor` | SSL certificate status and renewal. |
| `Networks.razor` | Docker network management. |
| `Cloud.razor` | Out-of-band cloud control (Hetzner/Hostinger). |

## Deployment

| Page | Purpose |
|---|---|
| `Deploy.razor` | Container / Compose deployment form. |
| `ComposeEditor.razor` | Visual Docker Compose editor. |
| `AppStore.razor` | One-click deployment from app templates. |
| `VolumeBackups.razor` | Volume backup management. |

## Automation, security & ops

| Page | Purpose |
|---|---|
| `ScheduledTasks.razor` | Cron-style scheduled tasks. |
| `Webhooks.razor` | CI/CD webhooks. |
| `LogSearch.razor` | Log search and alerts. |
| `Cves.razor` | CVE findings dashboard. |
| `AuditLog.razor` | Audit trail of privileged actions. |
| `AgentHistory.razor` | Observability dashboard for every agent/MCP tool call (`/agent-history`): filter by actor, tool, period, writes-only or denies-only; row click opens `AgentCallDetailDialog`. Backed by [`IMcpCallLogStore`](../../Services/Observability/). |
| `AgentCallDetailDialog.razor` | Detail dialog for a single recorded tool call — metadata, redacted parameters, result summary, error. |
| `Agent.razor` | The acting agent's chat/console (markdown rendering, file attachments, provider + system-prompt editor). |
| `Guardrails.razor` | Multi-preset guardrail editor (per-preset tool grid + mode, free-text rules). |
| `AiTriggers.razor` | Manage AI triggers: events that run the agent autonomously, with quick-setup templates. |

## Settings & system

| Page | Purpose |
|---|---|
| `Settings.razor` | All configuration — auth, whitelist, MCP keys, notifications, integrations, agent, guardrails, vault. |
| `Login.razor` | Sign-in page. |
| `Error.razor` | Error page. |

## Related

- Shared widgets: [`../Shared/`](../Shared/)
- Layout/navigation: [`../Layout/`](../Layout/)
- Business logic: [`../../Services/`](../../Services/)
