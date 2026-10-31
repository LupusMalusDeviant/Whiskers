# Components/Pages

The routable Blazor pages. Each `.razor` here is a screen in the app; some have a co-located dialog component. Pages depend on the [`Services/`](../../Services/) interfaces and are protected by role/auth where appropriate.

**Mutations go through the gateway.** A page never calls a mutating service method (one with `[MutatingOperation]`) directly; it wraps the call in `IUiMutationRunner.RunAsync("<capability>", new { … }, _ => Service.Call(…))` and catches `MutationRefusedException` to show the reason (ADR-0005 step 2.2, see [`Services/Mutations`](../../Services/Mutations/)). The page keeps its own role check; `MutationBypassArchitectureTests` fails on a new direct call. Container shells and the database query need Admin (PC-D2, PC-D3). Scheduled Tasks records the creator as the task's owner and lets an Admin take over tasks without one (PC-D6).

**One audit event per attempt (R1-21).** The gateway's event is the only record of an infrastructure action: a page writes no audit row of its own (no `IAuditLogService`). The role check in front of a mutation is `await RequireAsync(role, "<capability>", new { … })`, which on refusal records the attempt through `IUiMutationRunner.RefuseAsync` (one Denied event, nothing runs); a plain `Require` is left only in front of a read. What the page learns inside the delegate — a created container, network or app id, a backup file, deployment output — goes into that event with `MutationScope.Note(...)`; a failure the service reports without throwing (`false`, a `(false, …)` tuple, a result with `Success == false`) makes the event Failed, and a provider answer that says the action failed throws rather than returning text. `UiAuditCompletenessTests` and `ComponentTests/UiCreatedResourceAuditTests` hold this. Since R1-22 the configuration pages (Settings, Servers, WebhooksView, SelfBackupPanel, LogSearchView, AgentView, AiOperationsView, `Shared/AutomationOwnersPanel`, `Shared/RegistriesPanel`) follow it too; what changed is recorded by the stores (`ConfigChange`), and a backup download is audited by its endpoint.

## Overview & containers

| Page | Purpose |
|---|---|
| `Dashboard.razor` | The main live dashboard, all containers, health, metrics, update badges. The "stopped" / "unhealthy" stat tiles double as filters (same predicate as their count, combined with the group/tag filter). |
| `ContainerDetail.razor` | Deep view of one container: overview, stats, logs, health, terminal, env, and (for DB containers) a database tab. |
| `ContainerGraph.razor` | Visual graph of containers and their relationships. |
| `ContainerDiff.razor` | Compare container configurations. |
| `HealthReports.razor` | Historical health reports. |

## Servers & infrastructure

| Page | Purpose |
|---|---|
| `Servers.razor` | The fleet: configured servers, add/edit, onboarding. Saving a Kubernetes server writes a changed kubeconfig as its own gateway decision (`vault.secret_set`, R1-11) under a new vault key (`ServerConfig.KubeconfigRef`) that takes effect only with the saved server record; the unused key is then removed (`vault.secret_delete`). Removing a server removes its kubeconfig within the one `server.remove` decision (`ServerRemoval`). The cloud API key (R1-29) follows the same pattern: written to the vault under a new key (`ServerConfig.CloudKeyRef`), the old key removed after the save; a save needs a usable vault (refused otherwise), a legacy plaintext key moves into the vault with the next save (only while the record still holds it), and removal also removes the cloud key. A key that cannot be removed because the vault is disabled or locked is reported. |
| `Terminal.razor` / `ServerTerminal.razor` | Web terminal (container / host); the views open the session through the gateway (`terminal.container_open` / `terminal.host_open`, R1-17). |
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
| `Cves.razor` | CVE dashboard, **one row per CVE** (de-duplicated), expandable to all affected container/server instances with OS context + a verified flag; shows how long each CVE has been open and whether a fix exists. |
| `AuditLog.razor` | Audit trail of privileged actions — Admin only (R1-23), with *Verify integrity* (`IAuditChain.VerifyAsync`) and *Export* (`/audit/export`). |
| `AgentHistory.razor` | Observability dashboard for every agent/MCP tool call (`/agent-history`, Admin only since R1-23): filter by actor, tool, period, writes-only or denies-only; row click opens `AgentCallDetailDialog`. Backed by [`IMcpCallLogStore`](../../Services/Observability/). |
| `AgentCallDetailDialog.razor` | Detail dialog for a single recorded tool call, metadata, redacted parameters, result summary, error. |
| `Agent.razor` | The acting agent's chat/console (markdown rendering, file attachments, provider + system-prompt editor). Leaving the page or clearing the chat ends the session and cancels its pending approvals (R1-15). |
| `Guardrails.razor` | Multi-preset guardrail editor (per-preset tool grid + mode, free-text rules, "Require a second approver"). Core page since ADR-0005 step 2.1 — available with the Agent module off. |
| `Approvals.razor` | Human-in-the-Loop **Freigaben** (`/approvals`): live list of pending approvals (agent and direct MCP calls, and since R1-16 an Admin's web-UI change of a protected control-plane resource — the card then shows only the length of its command, SQL or compose file) with Genehmigen/Ablehnen, plus recently-decided history. Backed by [`IApprovalStore`](../../Services/Approvals/). Core page since ADR-0005 step 2.1. R1-15: each card (anchor `#<approval id>`, the link in the notification) says why the signed-in user cannot decide it (level, own request, expired); arguments only from Operator up; a self-decided approval is marked in the history. |
| `AiTriggers.razor` | Manage AI triggers: events that run the agent autonomously, with quick-setup templates. |

## Settings & system

| Page | Purpose |
|---|---|
| `Settings.razor` | All configuration, auth, whitelist, MCP keys, notifications, integrations, agent, guardrails, vault. Every save runs through the Mutation Gateway as an Admin-only capability (`config.*`, `mcp.key_*`, `vault.secret_*`, R1-11); `GuardrailsView`/`AiOperationsView` use `guardrails.save`, `guardrails.kill_switch_off` and `mcp.key_create`. The DNS token (R1-29) is written to the vault under a new key (`vault.secret_set`), then the `Dns` section is rebuilt under the store's lock naming that key (`config.save`), and the old key is removed (`vault.secret_delete`); without a usable vault a save that would store a token is refused. |
| `Login.razor` | Sign-in page. |
| `Error.razor` | Error page. |

## Related

- Shared widgets: [`../Shared/`](../Shared/)
- Layout/navigation: [`../Layout/`](../Layout/)
- Business logic: [`../../Services/`](../../Services/)
