# B — Mutation-Surface Inventory (Whiskers, read-only audit)

Scope: every code path that can change infrastructure (containers, K8s workloads, hosts, firewall, nginx, systemd, certs, cloud, DNS, DBs, volumes, networks, backups, deploys, scheduled/automatic actions, AI agent, terminals).
All paths are relative to `src/Whiskers/`. Audit date 2026-10-26. No repository file was modified.

## 0. Control model (what "passing the controls" means)

| Control | Where | Applies to |
|---|---|---|
| **AuthN** | Cookie (Google/OIDC/local) or MCP Bearer key (`Mcp/McpBearerAuthMiddleware.cs:20-43`, `Startup/WhiskersPipelineExtensions.cs:254-269`); webhook = HMAC only (`Startup/WhiskersPipelineExtensions.cs:293-302`); `Auth:Disabled` = synthetic admin for every request (`Startup/WhiskersPipelineExtensions.cs:136-148`) | all |
| **AuthZ (MCP)** | `McpPermissionCheck.CheckAccess` inside each tool (`Mcp/McpPermissionCheck.cs:18-50`), levels from `Models/McpPermission.cs:70-196` (unlisted = admin) | MCP direct + agent (synthetic ctx `Services/Agent/AgentToolInvoker.cs:221-235`) |
| **AuthZ (UI)** | Per-page `Require(AppRole.X)` in the Blazor handler (server-side, role cached per circuit at `OnInitializedAsync`, e.g. `Components/Pages/Dashboard.razor:579-591`) + markup hiding | UI |
| **Guardrail / policy** | `IAgentGuardrailEngine` (`Services/Agent/Guardrails/GuardrailEngine.cs:28-56`, rules `BuiltInGuardrailRules.cs`): principal ceiling, ReadOnly kill switch, deny/allow lists, protected resources (`whiskers*`), forbidden-arg regex, Confirm. Agent: `AgentSession.cs:130-177` + `AgentToolInvoker.cs:61-67`. Direct MCP: `Mcp/McpGuardrailFilter.cs:96-118` → `DirectMcpCallGuard.cs:63-106` (**only registered when Agent module is on**, `Modules/Agent/AgentModule.cs:82-83`) | agent + MCP direct ONLY |
| **Approval** | Agent: `AgentSession` Confirm → `ApprovalCoordinator`/`ApprovalStore` (in-memory). Direct MCP: approve-then-retry grant (10 min, one use) `DirectMcpCallGuard.cs:86-106`. Trigger/`instruct_agent`: Confirm auto-**denied** (`AiTriggerDispatcher.cs:122-127`, `AgentTools.cs:59-65`) | agent + MCP direct |
| **Audit** | `IAuditLogService.LogAsync` — called **only** from MCP tools, Razor pages, `AiTriggerDispatcher` (no service/background class writes AuditLog). Agent History = `IMcpCallLogStore` via `McpCallLogMiddleware` (direct MCP) and `AgentToolInvoker.RecordAsync` (agent) | per path (see table) |

Abbreviations: **AL** = AuditLog entry, **AH** = Agent History (`McpToolCalls`) entry, **GR** = guardrail engine evaluated, **Appr** = approval possible, **Verify** = post-execution verification. "GR (if Agent mod.)" = only when `DirectMcpCallGuard` is registered.

## 1. Inventory

### 1a. MCP tools (direct `/mcp` calls; the same methods are what the in-process agent invokes)

Common columns for every MCP row unless stated: Actor/AuthN = API key (Bearer) or cookie web user (`McpPermissionCheck.cs:28-35`); AuthZ = server-side scope/role check in tool; GR = `McpGuardrailFilter` (if Agent module) → Confirm = approve-then-retry; AH = yes (`McpCallLogMiddleware.cs:29-60`, success = HTTP transport status only). Agent invocations add GR+Appr via `AgentSession` and AH via `AgentToolInvoker.cs:107-134`.

| ID | Tool → execution | Level | AL? | Verify? | Notes |
|---|---|---|---|---|---|
| M-01 | `start_container` `Mcp/Tools/ContainerTools.cs:105` → `IDockerService.StartContainerAsync` | write | yes :129/:132 | no | |
| M-02 | `stop_container` `ContainerTools.cs:138` → `StopContainerAsync` | write | yes :162/:165 | no | |
| M-03 | `restart_container` `ContainerTools.cs:171` → `RestartContainerAsync` | write | yes :195/:198 | no | |
| M-04 | `update_container` `ContainerTools.cs:204` → `CaptureSnapshotAsync` + `RecreateContainerAsync` | write | yes :248/:252 | **no health check**; rollback snapshot only | |
| M-05 | `deploy_app` `ContainerTools.cs:280` → `IDeploymentService` | admin | yes :347/:357 | no | admin because bind-mounts = host root (`Models/McpPermission.cs:107-110`) |
| M-06 | `set_container_env` `ContainerTools.cs:395` → write .env + `docker compose up` | write | yes :482/:486 | no | |
| M-07 | `deploy_compose` `ContainerTools.cs:492` | admin | yes :520/:529 | no | |
| M-08 | `execute_command` `Mcp/Tools/ServerTools.cs:117` → `IHostCommandExecutor` (local = `nsenter` root, `Services/Server/HostCommandExecutor.cs:52-79`) | admin | yes :143 (redacted) | no | root shell on any host |
| M-09 | `add_firewall_rule` `ServerTools.cs:178` → UFW | write | yes :207 | no | UI requires Admin for same |
| M-10 | `remove_firewall_rule` `ServerTools.cs:215` | write | yes :232 | no | UI requires Admin |
| M-11 | `update_nginx_config` `ServerTools.cs:276` | write | yes :296 | pre-check `nginx -t` | UI requires Admin |
| M-12 | `manage_systemd_service` `ServerTools.cs:320` | write | yes :352 | no | UI requires Admin |
| M-13 | `renew_ssl_certificate` `ServerTools.cs:380` (certbot) | write | yes :399 | no | |
| M-14 | `renew_mtls_certificate` `ServerTools.cs:21` | admin | yes :36 | validator in service | |
| M-15 | `execute_query` `Mcp/Tools/DatabaseTools.cs:114` → `IDatabaseService.ExecuteQueryAsync` (arbitrary SQL) | write | **NO** | no | DROP only blocked by regex if GR active |
| M-16 | `backup_database` `DatabaseTools.cs:155` | write | yes :175 | no | |
| M-17 | `set_dns_record` `Mcp/Tools/DnsTools.cs:65` | write | yes :86 | no | |
| M-18 | `delete_dns_record` `DnsTools.cs:105` | write | yes :124 | no | |
| M-19 | `create_network` `Mcp/Tools/NetworkTools.cs:41` | write | yes :64/:67 | no | UI Admin |
| M-20 | `remove_network` `NetworkTools.cs:73` | write | yes :91/:94 | no | UI Admin |
| M-21 | `connect_container_to_network` `NetworkTools.cs:100` | write | **NO** | no | |
| M-22 | `disconnect_container_from_network` `NetworkTools.cs:117` | write | **NO** | no | |
| M-23 | `create_scheduled_task` `Mcp/Tools/SchedulerTools.cs:35` (CustomCommand also checks `execute_command` :57-61) | write(+admin) | yes :83 | n/a | creates a **future, unattended** mutation (see S-01) |
| M-24 | `delete_scheduled_task` `SchedulerTools.cs:90` | write | yes :103 | n/a | |
| M-25 | `run_scheduled_task` `SchedulerTools.cs:109` (CustomCommand checks `execute_command` :123-127) | write(+admin) | yes :131 | no | GR sees only `taskId` → protected-resource/forbidden-arg rules blind to the real target |
| M-26 | `cloud_power_on` `Mcp/Tools/CloudTools.cs:78` → `ICloudControlService` | write | **NO** | no | |
| M-27 | `cloud_shutdown` `CloudTools.cs:85` | write | **NO** | no | UI requires Admin |
| M-28 | `cloud_reboot` `CloudTools.cs:92` | write | **NO** | no | |
| M-29 | `cloud_hard_reset` `CloudTools.cs:99` | write | **NO** | no | forceful power-cycle |
| M-30 | `cloud_create_snapshot` `CloudTools.cs:106` | write | **NO** | no | Hostinger: replaces only snapshot |
| M-31 | `hetzner_enable_rescue` `Mcp/Tools/HetznerTools.cs:24` | admin | yes :38 | no | returns root password |
| M-32 | `hetzner_disable_rescue` `HetznerTools.cs:47` | write | **NO** | no | |
| M-33 | `hetzner_enable_backups` `HetznerTools.cs:63` | write | **NO** | no | cost |
| M-34 | `hetzner_disable_backups` `HetznerTools.cs:79` | write | **NO** | no | **deletes existing backups** |
| M-35 | `hetzner_change_server_type` `HetznerTools.cs:95` | write | **NO** | no | |
| M-36 | `hetzner_delete_snapshot` `HetznerTools.cs:135` | write | **NO** | no | irreversible |
| M-37 | `create_log_alert` `Mcp/Tools/LogTools.cs:38` | write | **NO** | n/a | indirect: new rule → events → AI triggers |
| M-38 | `pause_server_checks` `Mcp/Tools/LoopSuspensionTools.cs:56` | write | **NO** (service has no AL) | n/a | disables monitoring/remediation for a host |
| M-39 | `resume_server_checks` `LoopSuspensionTools.cs:90` | write | **NO** | n/a | |
| M-40 | `preview_auto_patch` `Mcp/Tools/AutoPatchTools.cs:33` (pulls images, restores tags) | write | **NO** | n/a | mutates image store |
| M-41 | `instruct_agent` `Mcp/Tools/AgentTools.cs:22` → `AgentService.StartSessionAsync` | read | AL per tool, AH per tool | — | Confirm auto-denied :59-65; principal = caller |

### 1b. Agent paths

| ID | Entry → actor | AuthZ | GR | Approval | Execution | AL | AH |
|---|---|---|---|---|---|---|---|
| A-01 | Web agent chat (`Components/Shared/AiChat.razor:257-311`, `Components/Pages/AgentView.razor:434-447`) → cookie user | principal ceiling = user role (`BuiltInGuardrailRules.cs:34-53`) + tool CheckAccess with synthetic claim (`McpPermissionCheck.cs:56-57`) | yes `AgentSession.cs:130` + `AgentToolInvoker.cs:61` | yes, user self-approves (by design) via `ApprovalStore.ResolveAsync` (no resolver-level arg in AgentView :445 / AiChat :308) | `AgentToolInvoker.ExecuteAsync` :139 (reflection on MCP tool) | per tool (actor `agent:<email>`) | yes |
| A-02 | `instruct_agent` (M-41) → MCP key/web user | as above | yes | Confirm → denied | same | per tool | yes |
| A-03 | **AI trigger** `Services/Agent/Triggers/AiTriggerDispatcher.cs:47-149` ← `CompositeNotificationService.cs:56` ← HealthMonitor / LogMonitor (`LogMonitorService.cs:194-209`) / CVE / cert events → synthetic principal `ai-trigger:<name>` capped at `t.MaxLevel` (:107-110) | trigger config Admin-only (`AiTriggerStore.cs:53-57`) | yes — **but with the global active policy, not the trigger's preset** (see B-05) | Confirm → auto-denied :122-127; anything the policy marks Allow runs **without any human** | `AgentSession` → invoker | per tool + one `agent.run` summary :174-183 | yes (`trigger`) |
| A-04 | Claude Code CLI runtime `Services/Agent/ClaudeCodeRuntime.cs:46-61,122-160` → subprocess with configured MCP key → `/mcp` | key level ≤ caller (:52-57) | via direct-MCP filter (if Agent mod.) | approve-then-retry | MCP tools | per tool | yes (`mcp-direct`) |

### 1c. Background services, schedulers, webhooks (no interactive human)

| ID | Entry | Actor / AuthN | AuthZ at execution | GR | Approval | Execution | Verify | AL |
|---|---|---|---|---|---|---|---|---|
| S-01 | **Scheduler cron loop** `Services/Scheduler/SchedulerService.cs:100-160` (also `RunNowAsync` :240) | system (task creator's identity not stored/re-checked) | **none** | **no** | **no** | `Services/Scheduler/TaskExecutor.cs:38-147`: ContainerRestart :62, DbBackup :71, VolumeBackup :100, SelfBackup :114, **CustomCommand root shell** :125-136, Cleanup `docker system prune` :138-147, retention `rm -f` :149-182 | no | **NO** (only `TaskRunHistory`) |
| S-02 | **AutoUpdate** `Services/AutoUpdate/AutoUpdateService.cs:70-191` (hosted, `Modules/ImageUpdate/ImageUpdateModule.cs:54`) | system | DB `UpdatePolicies.AutoUpdate` (no UI setter found; `SetPolicyAsync` :206 has no caller) | **no** | **no** | `RecreateContainerAsync` :141 | 15 s state check :144-152; **no rollback** (TODO :171) | **NO** (`UpdateHistory` only) |
| S-03 | **AutoPatch nightly** `Services/AutoPatch/AutoPatchService.cs:74-170` (`Enabled`/`DryRun` in config `Configuration/AutoPatchSettings.cs:14-31`) | system | config opt-in | **no** (own exclude list `AutoPatchPlanner.cs:30,111` only; ignores `ProtectedResources` and ReadOnly kill switch) | **no** | `ScriptAutoPatchRunner.cs:49` runs `img-update.sh` as root | yes — script verify + rollback; stops on `RollbackNotVerified` :166-168 | **NO** (notification only) |
| S-04 | **Webhook** `POST /api/webhooks/{id}` `Startup/WhiskersPipelineExtensions.cs:293-302` → `Services/Webhooks/WebhookService.cs:87-153` | anonymous + HMAC-SHA256 over body (:99-112) | webhook config only | **no** | **no** | restart :155, rebuild/recreate :165, compose pull+up as root :182-195, git-deploy :175-180 | no | **NO** (`WebhookLogs` only) |
| S-05 | Git deploy (via S-04 or U-29) `Services/GitDeploy/GitDeployService.cs:132-160` | inherits caller | none in service | **no** | **no** | clone/reset `GitDeployCommands.cs:36-44` + compose up (repo content = what runs as root) | no | UI only (`GitDeployView.razor:248/254`); webhook path none |
| S-06 | HealthMonitor / LogMonitor / CVE / CertificateMonitor (`Services/HealthMonitor/ContainerHealthMonitor.cs:129`, `LogMonitor/LogMonitorService.cs:185-209`, `Certificates/CertificateMonitor.cs:63`) | system | — | — | — | **no direct mutation**; emit events → A-03 (auto-remediation lives only in AI triggers) | — | — |
| S-07 | `VpnBootstrapHostedService.cs:19-38` | system at startup | — | no | no | `provider.EnsureUpAsync` (local) | no | no (log only) |
| S-08 | `MetricsCollectorService.cs:265-280` | system | — | — | — | deletes AuditLog + McpToolCalls older than 90 d | — | n/a (evidence retention) |

### 1d. Interactive terminals

| ID | Entry | AuthZ (server-side) | GR | Appr | Execution | AL |
|---|---|---|---|---|---|---|
| T-01 | Container terminal `/terminal/{srv}/{ctr}` `Components/Pages/TerminalView.razor:51-63` | Operator (`:51`) | **no** | no | `TerminalSessionManager.CreateSession` `Services/Terminal/TerminalSessionManager.cs:33-93` → `docker exec -i` (local) or **`ssh root@host docker exec`** (:44-74) | **NO** (ILogger only) |
| T-02 | Embedded terminal in container detail `Components/Pages/ContainerDetail.razor:1109-1129` | Operator (`:1112`) | no | no | same | **NO** |
| T-03 | **Host shell** `/server-terminal/{srv}` `Components/Pages/ServerTerminalView.razor:48-78` | Admin (`:48`) | **no** | no | local: `nsenter` into host PID ns (`Services/Terminal/TerminalSession.cs:32-34`); remote: SSH/Tailscale SSH as root (`TerminalSessionManager.cs:95-140`) | **NO** — no session open/close, no transcript |

### 1e. Web UI (Blazor Server handlers calling services directly — **none passes the guardrail engine, none can raise an approval**)

| ID | Page:handler | AuthZ (server-side `Require`) | Execution | AL | Verify |
|---|---|---|---|---|---|
| U-01 | `Dashboard.razor:791/813/835` start/stop/restart (Docker, **and K8s** scale-to-0/1 & rollout-restart via `KubernetesWorkloadProvider.cs:84-178`) | Operator | Docker / K8s API | yes :802/:824/:846 | no |
| U-02 | `Dashboard.razor:857` remove (force) | Admin | `RemoveContainerAsync` :868 | yes :869 | no |
| U-03 | `Dashboard.razor:892` update | Operator | snapshot + `RecreateContainerAsync` :904 | yes :906 | no |
| U-04 | `Dashboard.razor:920` rollback | Operator | `AutoUpdate.RollbackAsync` :935 | yes :936 | no |
| U-05 | `ContainerDetail.razor:1218/1228/1238/1248` start/stop/restart/remove | Operator/Admin | Docker | yes | no |
| U-06 | `ContainerDetail.razor:1410` save .env + restart | Admin | host cmd :1435/:1446 | yes :1454 | restart exit code |
| U-07 | `ContainerDetail.razor:1796` DB query (arbitrary SQL) | **Operator** | `DatabaseService.ExecuteQueryAsync` :1816 | **NO** | no |
| U-08 | `ContainerDetail.razor:1655/1684/1727/1764` artisan migrate / rollback / SQL file / seed file | Admin | exec in container via host :1647-1651 | **NO** | no |
| U-09 | `ContainerDetail.razor:1828` DB backup | Admin | `BackupDatabaseAsync` :1837 | **NO** | no |
| U-10 | `Deploy.razor:226/285` form / raw compose YAML | **Operator** | `DeploymentService.DeployFrom*` :259/:297 | yes :262/:299 | no |
| U-11 | `AppStore.razor:498/613` template / registry image | **Operator** | host `mkdir … docker compose up` :536/:647 (template vars string-replaced into YAML :529-531) | yes :541/:652 | no |
| U-12 | `ComposeEditor.razor:249` | **Operator** | host compose up :265 | yes :270 | no |
| U-13 | `Networks.razor:168/188` | Admin | create/remove network | yes | no |
| U-14 | `Firewall.razor:129` **UFW enable/disable** | Admin | `FirewallService.SetStatusAsync` :144 | **NO** | no |
| U-15 | `Firewall.razor:157`, `AddFirewallRuleDialog.razor:44` | Admin (dialog opened after `Require` :183; `Submit` itself unchecked) | UFW rule add/remove | yes :169 / :54 | no |
| U-16 | `NginxManager.razor:144/173` save/enable/disable site | Admin | | yes :159/:183 | nginx -t in service |
| U-17 | `NginxManager.razor:208` reload nginx | Admin | `ReloadAsync` :214 | **NO** | no |
| U-18 | `SystemdManager.razor:148` | Admin | start/stop/restart | yes :176 | no |
| U-19 | `SslCerts.razor:113/131` | Admin | certbot renew | yes :122/:140 | no |
| U-20 | `Servers.razor:543/638` add/update/delete server (+ vault secrets, SSH keys) | Admin | config | yes :523/:536/:656 | — |
| U-21 | `Servers.razor:715` **onboarding** (installs proxy/certs/Tailscale as root) | Admin | `OnboardingService.OnboardServerAsync` :734 | **NO** | result object |
| U-22 | `Servers.razor:359/373/387` pause/resume checks (single/all) | **UI-hidden only** (`:35`, `:124`), no `Require` in handler | `LoopSuspensionService` | yes :364/:377/:395 | — |
| U-23 | `CloudView.razor:126` power on/shutdown/reboot/reset/snapshot | Admin | `ICloudControlService` :150-154 | yes :170-175 | no |
| U-24 | `VolumeBackupsView.razor:215/241/263` backup / **restore** / delete | Operator / Admin / Admin | `IVolumeBackupService` | yes | no |
| U-25 | `SelfBackupPanel.razor:137/165/224` create/delete/**restore Whiskers** | Admin | `IBackupService.BeginRestoreAsync` :248 | yes (before restore :243) | archive validation :229 |
| U-26 | `ScheduledTasksView.razor:320/373/383` create/toggle/delete | Admin | `ISchedulerService` | yes :338/:376/:392 | — |
| U-27 | `ScheduledTasksView.razor:350` **Run now** (any type incl. CustomCommand) | **Operator** | `RunNowAsync` :358 | yes :364 | no |
| U-28 | `WebhooksView.razor:241/279/298/329` create/regen/toggle/delete; `:264` signed test (fires the real action) | Admin; test = Operator | `IWebhookService` | yes :255/:272/:290/:304/:340 | — |
| U-29 | `GitDeployView.razor:198/215` save/delete app (repo URL, branch, compose path, token); `:234` deploy | Admin; deploy = Operator | `IGitDeployService` | save/delete **NO**; deploy yes :248/:254 | no |
| U-30 | `LogSearchView.razor:207/228/240` + `AiTriggersView.razor:159` log-alert rules | Operator / Admin | `ILogMonitorService` | **NO** | — |
| U-31 | `AiTriggersView.razor:179` save AI triggers (prompt, preset, MaxLevel) | Admin (page :121 + store :55) | `AiTriggerStore.SaveAsync` | **NO** (ILogger only) | — |
| U-32 | `GuardrailsView.razor:306`, `AiOperationsView.razor:159` edit/activate guardrail policy (incl. ReadOnly kill switch) | Admin (page + `GuardrailStore.RequireAdmin` :134-138) | `GuardrailStore.SaveConfigAsync` | **NO** (ILogger warning `GuardrailStore.cs:86`) | — |
| U-33 | `ApprovalsView.razor:166` approve/reject (agent + direct MCP) | resolver level ≥ approval level (`ApprovalStore.cs:118-125`) | releases pending action / grants retry | **NO** (in-memory store only) | — |
| U-34 | `Settings.razor` MCP keys/perm levels/roles/whitelist/vault/terminal settings | Admin (`:52`) | config stores | yes (`:967`, `:1011`, `:1024`, `:1049`, `:1098`, `:1161`, `:1205`) | — |

**Total mutation paths inventoried: 87** (41 MCP tools + 4 agent paths + 8 background/webhook/system + 3 terminals + 34 UI handler groups covering ~70 individual handlers).

## 2. Paths that mutate WITHOUT the full auth + authz + policy + audit chain

| Path class | AuthN | AuthZ | Guardrail/policy | Approval | AuditLog |
|---|---|---|---|---|---|
| MCP direct (Agent module on) | ✔ | ✔ | ✔ | ✔ (retry) | partial (M-15, M-21/22, M-26..30, M-32..40 missing) |
| MCP direct (Agent module off) | ✔ | ✔ | ✘ | ✘ | partial |
| Agent (web / instruct_agent) | ✔ | ✔ | ✔ | ✔ | per tool (same gaps) |
| AI triggers | system | trigger ceiling | ✔ but wrong policy (B-05) | ✘ (auto-deny) | summary + per tool |
| UI handlers (U-*) | ✔ | ✔ (role) | **✘** | **✘** | mostly ✔, gaps listed |
| Terminals (T-*) | ✔ | ✔ (role) | **✘** | **✘** | **✘** |
| Scheduler cron (S-01) | ✘ (system) | ✘ (not re-checked) | **✘** | **✘** | **✘** |
| AutoUpdate / AutoPatch (S-02/S-03) | system | config | **✘** | **✘** | **✘** |
| Webhooks / git-deploy (S-04/S-05) | HMAC | config | **✘** | **✘** | **✘** |

## 3. Gap findings

| ID | Severity | Category | v1-blocker | Finding | Evidence |
|---|---|---|---|---|---|
| **B-01** | **Critical** | AuthZ / privilege escalation | **Yes** | **Operator → host root via UI deploy.** MCP `deploy_app`/`deploy_compose` are Admin precisely because a compose file can bind-mount `/` or run privileged ("de-facto root on the host"), but the UI equivalents only require Operator: raw compose YAML in Deploy, ComposeEditor, AppStore registry/template deploy. AppStore additionally string-replaces user-supplied template variables into YAML (`yaml.Replace`) — newline injection can add `volumes: - /:/host` or `privileged: true` even to a "safe" template. | `Models/McpPermission.cs:107-110` vs `Components/Pages/Deploy.razor:228,287`; `ComposeEditor.razor:251,265`; `AppStore.razor:500,529-536,615,647` |
| **B-02** | **High** | Policy / automation | **Yes** | **Unattended automation bypasses guardrails, kill switch, approvals and AuditLog.** Scheduler cron execution (incl. CustomCommand root shell, `docker system prune`, retention `rm -f`), AutoUpdate, AutoPatch, webhooks and webhook-triggered git deploy call Docker/host executors directly. The ReadOnly kill switch (`ReadOnlyModeRule`) and `ProtectedResources` (`whiskers*`) never apply; only side tables (`TaskRunHistory`, `UpdateHistory`, `WebhookLogs`) record what happened, outside the AuditLog/Agent History the operator reviews. | `Services/Scheduler/SchedulerService.cs:110-111,136`; `TaskExecutor.cs:125-147,180-181`; `AutoUpdate/AutoUpdateService.cs:141`; `AutoPatch/AutoPatchService.cs:83`, `ScriptAutoPatchRunner.cs:49`; `Webhooks/WebhookService.cs:121-130,182-195`; `GuardrailEngine.cs` used only by agent/MCP |
| **B-03** | **High** | Audit / terminals | **Yes** | **Interactive root shells are unaudited and unguarded.** Host shell (`nsenter` / SSH root) and container terminals (SSH as **root** to the host, then `docker exec`) write no AuditLog entry for session open/close, keep no transcript, and are outside guardrails/kill switch. Container terminal is Operator-level: exec into a container holding `docker.sock` or running privileged is host root for an Operator. | `ServerTerminalView.razor:48-78`; `TerminalView.razor:51-63`; `ContainerDetail.razor:1109-1120`; `TerminalSessionManager.cs:44-74,95-140` (only `_logger.LogInformation`); `TerminalSession.cs:28-34` |
| **B-04** | **High** | Audit | **Yes** | **MCP mutations missing AuditLog** (only Agent History, whose `Success` is HTTP transport status): `execute_query`, all `cloud_*` power/snapshot tools, `hetzner_disable_rescue/enable_backups/disable_backups/change_server_type/delete_snapshot` (irreversible / deletes backups), network connect/disconnect, `create_log_alert`, `pause/resume_server_checks`, `preview_auto_patch`. | `DatabaseTools.cs:114-150`; `CloudTools.cs:78-110,123-128`; `HetznerTools.cs:47-150` (only :38 audits); `NetworkTools.cs:100-131`; `LogTools.cs:38-63`; `LoopSuspensionTools.cs:56-120`; `AutoPatchTools.cs:33-60`; `McpCallLogMiddleware.cs:57` |
| **B-05** | **High** | Policy / AI triggers | **Yes** | **AI trigger runs ignore the trigger's pinned guardrail preset.** The dispatcher builds `ctx.Policy` from `t.GuardrailPreset`, but `AgentService` always passes the live `IGuardrailStore`, and `AgentSession.LivePolicy` prefers `_guardrailStore.Current` (global active preset) over `_context.Policy`. A trigger configured "Observe only" runs under whatever preset is globally active (possibly autonomous-write); the code comment claims the opposite. | `Services/Agent/Triggers/AiTriggerDispatcher.cs:101-113`; `Services/Agent/AgentService.cs:76`; `Services/Agent/AgentSession.cs:192-195` |
| **B-06** | **High** | AI safety / injection | **Yes** | **Untrusted log text flows into autonomous agent prompts.** Log-alert events carry the matched third-party log line in `ImageInfo`; the trigger prompt embeds it verbatim ("Info: …"). With any policy whose `MaxAutonomousLevel ≥ write` (or B-05), an attacker who can get a line into a container log can steer write actions with no human approval. | `Services/LogMonitor/LogMonitorService.cs:206`; `AiTriggerDispatcher.cs:151-171` (esp. :165) |
| **B-07** | **High** | Audit / policy changes | **Yes** | **Policy-relevant UI changes are not audited:** UFW enable/**disable**, nginx reload, server onboarding (root installs), DB query/migrations/seed/backup, GitDeploy app save/delete (changes what a webhook will deploy as root), log-alert rules (feed AI triggers), AI trigger save, guardrail policy/preset edits incl. ReadOnly kill switch, approval decisions. | `Firewall.razor:144`; `NginxManager.razor:214`; `Servers.razor:734`; `ContainerDetail.razor:1655-1840`; `GitDeployView.razor:205,225`; `LogSearchView.razor:212,233,243`; `AiTriggersView.razor:185`; `GuardrailsView.razor:319`; `AiOperationsView.razor:169`; `ApprovalsView.razor:173` |
| **B-08** | **High** | AuthN / config | **Yes** (as release gate) | **`Auth:Disabled` gives every network client Admin, including `/mcp` (`execute_command` = root) and every UI action.** No loopback/IP restriction; the synthetic principal is injected for every request. | `Startup/WhiskersPipelineExtensions.cs:136-148`; `Mcp/McpPermissionCheck.cs:59-61`; `CurrentUserService.cs:35` |
| **B-09** | **Medium** | Policy (UI) | No (decide & document) | **UI actions never consult the guardrail engine**: the ReadOnly kill switch, `ProtectedResources` (`whiskers*`) and forbidden-arg patterns (`DROP DATABASE`, `rm -rf /`) apply to AI/MCP only; an Operator can stop/remove Whiskers itself or run `DROP DATABASE` in the UI query box. If intentional, the kill switch must be documented as "AI/MCP only". | no `IAgentGuardrailEngine` reference in `Components/Pages/*.razor` except AI pages; `Configuration/GuardrailPolicy.cs` SafeDefault |
| **B-10** | **Medium** | AuthZ / scheduler | **Yes** (simple fix) | **Operator can execute Admin-created CustomCommand (root) tasks via UI "Run now"**; MCP `run_scheduled_task` correctly requires `execute_command` for that type. Cron runs also never re-check the creator (identity not stored), so a demoted/removed admin's root tasks keep running. | `ScheduledTasksView.razor:350-358` vs `Mcp/Tools/SchedulerTools.cs:123-127`; `SchedulerService.cs:126-160` |
| **B-11** | **Medium** | Approval | Partial (audit = Yes) | **Approval weaknesses:** (a) a `create_scheduled_task` CustomCommand approval carries level *write* (tool level), so an Operator can approve a root command; (b) `run_scheduled_task` approvals show only `taskId`; (c) approvals and direct-MCP grants are in-memory (lost on restart) and decisions are not written to AuditLog; (d) no four-eyes: a requester may approve their own direct-MCP request; (e) one approved create → unlimited future unattended executions (escapes `MaxActionsPerSession`). | `DirectMcpCallGuard.cs:71,108-137`; `ApprovalStore.cs:113-134`; `SchedulerTools.cs:57-61` |
| **B-12** | **Medium** | Policy | No (document) | **Direct MCP calls have no guardrails when the Agent module is disabled** — `McpGuardrailFilter` passes through when `IDirectMcpCallGuard` is not registered; Write keys are then bounded by scope only (no kill switch, no protected resources). | `Mcp/McpGuardrailFilter.cs:100-102`; `Modules/Agent/AgentModule.cs:82-83` |
| **B-13** | **Medium** | AuthZ consistency | No | **MCP Write level is broader than UI Operator**: firewall add/remove, nginx config, systemd, cloud shutdown/hard-reset, Hetzner disable-backups/delete-snapshot/change-type are Write over MCP but Admin-only in the UI; a Write ("operator") key can open ports or delete backups that an Operator web user cannot. | `Models/McpPermission.cs:111-115,176-190` vs `Firewall.razor:131,159`, `NginxManager.razor:146`, `SystemdManager.razor:150`, `CloudView.razor:128` |
| **B-14** | **Medium** | Webhooks | Partial | **Webhook trigger hardening:** HMAC covers body only — no timestamp/nonce/delivery-ID dedupe → captured requests replay indefinitely; no rate limit; executes root `docker compose pull && up` / git deploy; not in AuditLog; Operator "Test" fires the real action. | `WebhookService.cs:87-153,182-195`; `WebhooksView.razor:264-272`; `WhiskersPipelineExtensions.cs:293-302` |
| **B-15** | **Medium** | Verification | No | **AutoUpdate has no rollback despite `AutoRollback` policy flag** (TODO) — a failed update leaves the container broken; also no AuditLog and no UI to set policies (dormant but running hosted service). | `AutoUpdateService.cs:141-171,206` |
| **B-16** | **Medium** | Verification | No | **Post-execution verification is largely absent.** Start/stop/restart/update/deploy/env/firewall/systemd/cloud actions return after the API call; `update_container` (MCP) and Dashboard update do no health check. Only AutoUpdate (15 s state), AutoPatch (script verify+rollback) and nginx `-t` pre-check verify. | `ContainerTools.cs:240-253`; `Dashboard.razor:904`; `AutoUpdateService.cs:144-152` |
| **B-17** | **Low** | AuthZ (session) | No | Role is read once per Blazor circuit; demotion / whitelist removal does not affect open circuits or running terminal sessions until reconnect. Servers pause/resume handlers rely on markup hiding only (no `Require`). | `Dashboard.razor:591`; `Servers.razor:35,124,359-400` |
| **B-18** | **Low** | Defense in depth | No | `AgentToolInvoker` blocks only `Deny`; `Confirm` is enforced solely by `AgentSession`. Any future caller of the invoker would execute Confirm-level actions without approval. | `AgentToolInvoker.cs:61-67` |
| **B-19** | **Low** | Audit fidelity | No | Agent History for direct MCP records `Success` from HTTP status (tool errors and permission denials show as success) and records verdict "allow" for cookie users denied by `McpPermissionCheck`. | `McpCallLogMiddleware.cs:57,151-158` |
| **B-20** | **Low** | Audit retention | No | AuditLog and McpToolCalls are hard-deleted after 90 days automatically (not configurable, no export). | `MetricsCollectorService.cs:277-280` |

## 4. Bypass summary (paths that mutate without auth+authz+policy+audit)

1. Scheduler cron execution — `SchedulerService.cs:110-160` → `TaskExecutor.cs:62-182` (no authz re-check, no GR, no approval, no AL).
2. AutoUpdate — `AutoUpdateService.cs:141` (no GR/approval/AL).
3. AutoPatch — `AutoPatchService.cs:83` → `ScriptAutoPatchRunner.cs:49` (no GR/approval/AL; has verify+rollback).
4. Webhooks + git deploy — `WhiskersPipelineExtensions.cs:293` → `WebhookService.cs:121-195` (HMAC only, no GR/approval/AL, replayable).
5. Terminals — `ServerTerminalView.razor:48-78`, `TerminalView.razor:51-63`, `ContainerDetail.razor:1109` (no GR, no AL).
6. AI triggers — `AiTriggerDispatcher.cs:89-149` (autonomous; runs under global not pinned policy, `AgentSession.cs:194`; untrusted log text in prompt, `:165`).
7. All UI handlers (U-01..U-34) — no GR/kill switch; AL missing for U-07/08/09/14/17/21/29(save/delete)/30/31/32/33.
8. UI deploy at Operator = host root — `Deploy.razor:228,287`, `ComposeEditor.razor:251`, `AppStore.razor:500,615`.
9. MCP tools without AL — see B-04.
10. MCP direct with Agent module off — `McpGuardrailFilter.cs:100-102`.
