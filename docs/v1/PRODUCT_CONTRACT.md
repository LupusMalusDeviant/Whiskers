# Whiskers v1.0 — Product Contract

| | |
|---|---|
| Status | **Accepted** (WP11a). Decisions PC-D1…PC-D8 accepted by the maintainer on 2026-10-26. |
| Baseline | `main` @ `498e70d` (0.15.0) + PR #41 (WP1) |
| Date | 2026-10-26 |
| Companion | [`SECURITY_INVARIANTS.md`](SECURITY_INVARIANTS.md) (INV-xx), [`TECHNICAL_AUDIT.md`](TECHNICAL_AUDIT.md) (TA-xx), [`audit/B-mutations.md`](audit/B-mutations.md) (path inventory) |

## 1. The promise

> **Give AI agents production access without giving them unrestricted production credentials.**

Whiskers is a self-hosted control plane between people, AI agents and infrastructure (Docker hosts, Kubernetes
workloads, host services, cloud servers, DNS, databases). The agent never holds a host credential. It asks
Whiskers, and Whiskers decides, executes, verifies and records.

**The contract.** *No infrastructure mutation that Whiskers performs bypasses authentication, authorization,
policy, a required approval, or audit.*

The original wording limited this to mutations "through the agent/MCP control plane". Decision PC-D1 widens it
to **every mutation Whiskers performs**, whatever the entry point. Reason: the audit
shows that the UI, the scheduler, AutoUpdate, AutoPatch, webhooks and terminals reach the same executors. If they
stay outside the contract, an agent that can create a scheduled task, a webhook or a log alert can launder a
mutation through them (B-02, B-11e, B-06). A contract with that hole cannot be defended to a security team.

## 2. Hero workflow

```
Observe → Propose → Policy check → Human approval → Execute → Verify → Audit
```

| Step | What happens | Owner after WP2 |
|---|---|---|
| Observe | Read-only tools and pages gather state. Reads are authorized but not policy-gated. | MCP read tools, UI |
| Propose | A principal (human, agent, automation) requests a capability on a target with arguments. | Entry point |
| Policy check | The policy engine decides Allow / Confirm / Deny for exactly that call; the decision is persisted. | Mutation Gateway |
| Human approval | On Confirm, a durable, hash-bound approval is raised; an eligible second person decides. | Mutation Gateway + approval store |
| Execute | The gateway executes the call that was decided and approved, under a deadline and an operation id. | Mutation Gateway |
| Verify | The capability's verifier checks the result; the outcome is recorded honestly. | Mutation Gateway |
| Audit | One tamper-evident event per attempt links all of the above. | Mutation Gateway |

## 3. Definitions

**Infrastructure mutation.** Any action that changes state outside Whiskers' own read models: containers,
images, networks, volumes, K8s workloads, compose projects, host packages and services, firewall, nginx,
certificates, cloud server power and snapshots, DNS records, database contents, backups and restores, files on
managed hosts. Also included, because they **cause** future mutations: creating or changing scheduled tasks,
webhooks, git-deploy apps, log alerts, AI triggers, guardrail policy, keys, roles and the whitelist.

**Principal.** Who acts (INV-01). Kinds:

| Kind | Example | Authenticated by |
|---|---|---|
| Human | an operator in the web UI | OIDC / Google / local login |
| ApiKey | an external MCP client | Bearer key (hashed) |
| Agent | an in-process agent session | on behalf of a Human or ApiKey principal (ceiling, INV-02) |
| Automation | scheduled task, AutoUpdate, AutoPatch, AI trigger | on behalf of a recorded owner (INV-04) |
| Webhook | an inbound CI call | HMAC + freshness + delivery id (INV-42) |
| System | boot-time housekeeping that touches no managed infrastructure | — |

**Capability.** A named mutating action with one required level (INV-13), for example `container.restart`,
`host.exec`, `compose.deploy`. MCP tools, UI handlers and automation map onto capabilities.

**Surface.** The entry point: `mcp`, `agent`, `ui`, `automation`, `webhook`, `terminal`.

## 4. Requirements per chain stage

Each stage lists the target behaviour and the invariants that make it testable.

| Stage | Required behaviour | Invariants |
|---|---|---|
| Actor | A structured principal, never a string; on-behalf-of chains are explicit. | INV-01, 02, 04, 05, 06 |
| AuthN | Every request is authenticated; synthetic principals cannot be presented; sessions and keys can be revoked immediately. | INV-03, 07–12 |
| AuthZ | One capability table for all surfaces; root-equivalent capabilities are Admin-only; handler-side checks only. | INV-13–19 |
| Policy | Every mutation gets a persisted decision; kill switch everywhere; protected resources on resolved targets; fail closed. | INV-20–29 |
| Approval | Durable, hash-bound, single-use, expiring, separation of duties. | INV-30–37 |
| Execution | Only through the gateway; operation id; deadline; injection-safe arguments; replay-safe webhooks. | INV-38–42 |
| Verification | A verifier per capability; honest outcome. | INV-43, 44 |
| Audit | One complete, redacted, tamper-evident event per attempt, written before execution. | INV-45–51 |
| Secrets | Vault for all infrastructure credentials; honest self-reports; authentic restores. | INV-51–56 |
| Target isolation | One resolved target per mutation; per-host credentials; Whiskers protects itself. | INV-57–59 |

## 5. Scope and stated limits

These are deliberate. They must appear in the product documentation (WP11b) so nobody reads more into the
contract than it says.

1. **Whiskers is root-equivalent by design.** The host-control plane runs commands as host root. Docker create
   rights are root on the host. The contract governs *who may reach* that power and *what is recorded*; it
   does not make the power smaller.
2. **An Admin with shell access to the Whiskers host is outside the contract.** They can edit the database,
   the JSON stores and the binaries. The audit hash chain (INV-49) makes database-only edits *detectable*;
   a host that holds `VAULT_KEY`, the checkpoint file and the database can rewrite all three consistently
   (ADR-0006).
3. **Commands typed inside an interactive terminal are not policy-checked one by one.** The contract covers
   opening the session (authorization, kill switch, audit) and, depending on PC-D2, a transcript.
4. **Single replica.** Whiskers 1.0 is a single-instance appliance. Availability and RPO/RTO follow from that
   and are documented in the backup guide (WP11b).
5. **No per-server or tenant scoping in 1.0.** A principal's level applies to every managed server (A-17).
6. **Out-of-band changes are invisible.** Changes made directly on a host, in a cloud console or via another
   tool are not governed. Whiskers may observe their effects; it does not attribute them.

## 6. Decisions

All eight decisions were accepted on 2026-10-26 as recommended. The alternatives stay listed so the reasoning remains
traceable.

| ID | Question | Decision | Rejected alternative | Affects |
|---|---|---|---|---|
| PC-D1 | Does the contract cover every mutation, or only agent/MCP? | **Every mutation.** Human UI actions go through the gateway: authorization, kill switch, protected resources, audit. They need an approval only where policy says so for humans (default: never). | Agent/MCP only, documenting UI, automation and terminals as out of scope. Leaves the laundering paths open (§1). | INV-20, 21, 38; TA-26 |
| PC-D2 | Terminals: what is recorded? | Session open/close audited (INV-47); host shell and container shell both Admin; kill switch blocks opening; **no keystroke transcript** in 1.0, stated as a limit. | Full transcript with redaction. Higher value for auditors, but stores secrets typed into shells. | INV-19, 47 |
| PC-D3 | Level of `execute_query` (arbitrary SQL as DB superuser, incl. `COPY … TO PROGRAM`). | **Admin** on every surface. | Keep Write and add SQL classification. Classification is a denylist and fails like INV-25. | INV-19; TA-43 |
| PC-D4 | Separation of duties for approvals. | Non-interactive requests (direct MCP, AI trigger, scheduled task) cannot be approved by the requesting principal. Interactive chat may self-confirm unless a policy flag `requireSecondApprover` is set. A single-admin install must be able to work: the flag defaults off, and self-approval is audited as such. | Strict four-eyes everywhere. Unusable for single-operator installs. | INV-33 |
| PC-D5 | What happens when the audit store cannot be written? | **Fail closed:** no intent record, no mutation. Break-glass is host access, which is outside the contract (§5.2). | Fail open with ILogger fallback, as today. | INV-48 |
| PC-D6 | Owner of unattended automation. | Every scheduled task, AI trigger, webhook and git-deploy app stores its owner. Each run re-checks the owner's authority; if it is gone, the item is suspended and an Admin is notified. | Re-check only at creation. | INV-04 |
| PC-D7 | Autonomy of AI triggers. | Read-only by default. Write requires an explicit per-trigger allowlist of capabilities and targets; anything else is Confirm, which for an unattended run means Deny. | Keep `MaxLevel=write` default. | INV-23, 27 |
| PC-D8 | Policy engine and modules. | The policy engine and approvals are **core**, independent of the Agent module. Disabling the agent removes the agent, not the guardrails. | Keep them in the Agent module and document that MCP is unguarded without it. | INV-26 |

## 7. Path matrix (current state)

One row per mutation path from [`audit/B-mutations.md`](audit/B-mutations.md), updated for WP1 and WP2 step 2.1. Each row shows
the chain **Actor → AuthN → AuthZ → Policy → Approval → Execution → Verification → Audit** as it is today,
and the invariants the path violates.

**Every gap is a v1 blocker.** A row is done when its gap column is empty.

Legend: ✅ holds · ⚠️ partial · ❌ missing · n/a not applicable · **GR?** guardrails only when the Agent module is on
(INV-26; fixed for every path in WP2 step 2.1 — guardrails are core) · **AL** AuditLog row · **AH** Agent History row only.

**Gaps shared by every path today** (not repeated per row): INV-01 (string actor), INV-20 for UI/automation/terminal,
INV-22 (no decision record — since R1-12 every gateway decision is recorded, so it remains only for paths not yet on the gateway), INV-38 (no gateway — MCP tools and agent go through it since WP2 step 2.1), INV-45 (no complete audit event — since R1-19 every gateway attempt is one complete event with typed fields; it remains for paths not yet on the gateway and for the rows UI pages still write themselves, R1-21; MCP tools write none of their own since R1-20),
INV-48 (audit after the fact, fail-open — since R1-19 the gateway refuses without a durable intent and keeps a failed outcome or refusal recoverable and visible; it remains for `AuditLogService` callers outside the gateway, whose lost rows are now at least visible). INV-49 holds since R1-23 for every audit row (hash chain with signed checkpoints, ADR-0006). Rows below list only the path-specific gaps.

### 7.1 MCP tools (direct `/mcp` calls; the in-process agent invokes the same methods)

Common to all M rows since WP2 step 2.1: Actor = API key or web user · AuthN ✅ (Bearer / cookie) · AuthZ ✅ in the
Mutation Gateway: catalog level, tool switch and stricter stored level for every principal (INV-15), `AllowedTools`
cannot widen (INV-16); the scope check inside the tool still runs · Target: containers resolved before policy
(INV-57 ⚠️, other kinds pass through) · Policy ✅ core guardrails (INV-26); protected resources and the built-in
control plane are matched against the resolved target and the words of free text (INV-24/59 ✅ since R1-16) · Approval ✅ approve-then-retry bound to principal, credential, capability, normalized
arguments, resolved target and policy version, single use, persisted (INV-30/31/35 ✅ since R1-14); never decided by
its requester, expiry and approver level checked at decision time (INV-32–34/37 ✅ since R1-15) · Execution = inside the gateway scope with a deadline · Audit: exactly one gateway AuditLog event (decision,
approval, intent, outcome) per attempt; what the tool reports about its work (`IToolAuditLog`) is added to that event's
`results` instead of a row of its own, and a failure it reports makes the event `Failed` (R1-20). A tool's error
answer is recorded `Failed` too (`MutationScope.Fail`, or an MCP result with `isError`). The Agent History of a
direct call records the filter's per-call outcome and the event's operation id, not the HTTP status (R1-20). In the
Audit column, `GW ✅` = the gateway event; `+ details` = the tool also reports domain details into it.

| ID | Tool | Level | Verification | Audit | Path-specific gaps |
|---|---|---|---|---|---|
| M-01 | `start_container` | write | ❌ | GW ✅ + details | INV-43 |
| M-02 | `stop_container` | write | ❌ | GW ✅ + details | INV-43 |
| M-03 | `restart_container` | write | ❌ | GW ✅ + details | INV-43 |
| M-04 | `update_container` | write | ❌ no health check; rollback snapshot only | GW ✅ + details | INV-43, 44, 53 |
| M-05 | `deploy_app` | admin | ❌ | GW ✅ + details | INV-43 (volumes checked since R1-16) |
| M-06 | `set_container_env` | **admin** (R1-05, root-equivalent like the page's .env save; was write) | ❌ | GW ✅ + details | INV-43, 51 |
| M-07 | `deploy_compose` | admin | ❌ | GW ✅ + details | INV-43 (plan incl. mounts decided since R1-16) |
| M-08 | `execute_command` | admin | ❌ | GW ✅ + details (redacted) | INV-24 (free text ✅ R1-16), 43, 59 |
| M-09 | `add_firewall_rule` | **admin** (R1-05; was write) | ❌ | GW ✅ + details | 43 |
| M-10 | `remove_firewall_rule` | **admin** (R1-05; was write) | ❌ | GW ✅ + details | 43 |
| M-11 | `update_nginx_config` | **admin** (R1-05; was write) | ⚠️ `nginx -t` pre-check | GW ✅ + details | 41 (E-03) |
| M-12 | `manage_systemd_service` | **admin** (R1-05; was write) | ❌ | GW ✅ + details | 43 |
| M-13 | `renew_ssl_certificate` | admin (R1-05) | ❌ | GW ✅ + details | INV-43 |
| M-14 | `renew_mtls_certificate` | admin | ⚠️ validator in service | GW ✅ + details | INV-58 |
| M-15 | `execute_query` | **admin** (PC-D3) | ❌ | GW ✅ | INV-19 (PC-D3), 24, 43 |
| M-16 | `backup_database` | admin (R1-05) | ❌ | GW ✅ + details | INV-43 |
| M-17 | `set_dns_record` | write | ❌ | GW ✅ + details | INV-43, 52 (DNS token) |
| M-18 | `delete_dns_record` | write | ❌ | GW ✅ + details | INV-43, 52 |
| M-19 | `create_network` | **admin** (R1-05; was write) | ❌ | GW ✅ + details | — |
| M-20 | `remove_network` | **admin** (R1-05; was write) | ❌ | GW ✅ + details | — |
| M-21 | `connect_container_to_network` | **admin** (R1-05; was write) | ❌ | GW ✅ | — |
| M-22 | `disconnect_container_from_network` | **admin** (R1-05; was write) | ❌ | GW ✅ | — |
| M-23 | `create_scheduled_task` | write; **effective admin** for CustomCommand, SelfBackup, every DB backup, and volume backups with retention (`maxBackups` > 0, default 7) — resolved before the caller check and the approval (R1-05) | n/a | GW ✅ + details | INV-04 |
| M-24 | `delete_scheduled_task` | write | n/a | GW ✅ + details | — |
| M-25 | `run_scheduled_task` | write; **effective admin** for the stored task's CustomCommand, SelfBackup, DB backup or retention, resolved before the caller check and the approval (R1-05) | ❌ | GW ✅ + details | INV-24 (policy sees only `taskId`), 30, 43 |
| M-26 | `cloud_power_on` | **admin** (R1-05; was write) | ❌ | GW ✅ | INV-43, 52 |
| M-27 | `cloud_shutdown` | **admin** (R1-05; was write) | ❌ | GW ✅ | 43, 52 |
| M-28 | `cloud_reboot` | **admin** (R1-05; was write) | ❌ | GW ✅ | INV-43, 52 |
| M-29 | `cloud_hard_reset` | **admin** (R1-05; was write) | ❌ | GW ✅ | 43, 52 |
| M-30 | `cloud_create_snapshot` | **admin** (R1-05; was write) | ❌ | GW ✅ | INV-43, 52 |
| M-31 | `hetzner_enable_rescue` | admin | ❌ | GW ✅ + details | INV-51 (returns root password), 52 |
| M-32 | `hetzner_disable_rescue` | **admin** (R1-05; was write) | ❌ | GW ✅ | INV-52 |
| M-33 | `hetzner_enable_backups` | **admin** (R1-05; was write) | ❌ | GW ✅ | INV-52 |
| M-34 | `hetzner_disable_backups` (deletes backups) | **admin** (R1-05; was write) | ❌ | GW ✅ | 52 |
| M-35 | `hetzner_change_server_type` | **admin** (R1-05; was write) | ❌ | GW ✅ | 43, 52 |
| M-36 | `hetzner_delete_snapshot` (irreversible) | **admin** (R1-05; was write) | ❌ | GW ✅ | 52 |
| M-37 | `create_log_alert` (feeds AI triggers) | write | n/a | GW ✅ | INV-27, 46 |
| M-38 | `pause_server_checks` | write | n/a | GW ✅ | — |
| M-39 | `resume_server_checks` | write | n/a | GW ✅ | — |
| M-40 | `preview_auto_patch` (pulls images) | write | n/a | GW ✅ | — |
| M-41 | `instruct_agent` → agent run | read | per tool | per tool | INV-08 (in-flight run survives key revocation) |

### 7.2 Agent paths

| ID | Path | Actor | AuthN | AuthZ | Policy | Approval | Execution | Verification | Audit | Path-specific gaps |
|---|---|---|---|---|---|---|---|---|---|---|
| A-01 | Web agent chat | Human → Agent | ✅ cookie | ✅ ceiling + tool switch (INV-15) | ✅ | ✅ self-confirm allowed and recorded unless `RequireSecondApprover` (PC-D4); ends with the chat (R1-15); persisted, and the gateway runs only an approval bound to the operation, arguments, target and policy version (R1-14); the chat's buttons decide only through the recorded approval, under the same rules as the Approvals page | invoker → gateway → tool | ❌ | gateway AL + per tool (as in 7.1) | — (INV-32, 36 closed by R1-15) |
| A-02 | `instruct_agent` | ApiKey/Human → Agent | ✅ | ✅ ceiling + tool switch | ✅ | Confirm → auto-deny ✅ | invoker → gateway → tool | ❌ | gateway AL + per tool | INV-08 |
| A-03 | AI trigger | Automation (`trigger:<id>`, own principal per trigger since R1-03) | n/a | ✅ read-only unless an Admin granted capabilities + targets; every trigger has an owner, re-checked before each run and per mutation (nested included), lost authority or no owner → suspended + Admin notified (R1-13); tool switch ✅ | ✅ pinned to the trigger's preset with the global kill switch; missing preset → no run (R1-12) | Confirm → Deny in the gateway, nested mutations included, no approval raised ✅ (R1-13); Allow runs unattended within the grant | invoker → gateway → tool | ❌ | gateway AL + per tool + `agent.run` | INV-04, 06, 27, 40 |
| A-04 | Claude Code CLI runtime (dead code) | fixed key | ✅ key | ⚠️ fail-open rights check | GR? | approve-then-retry | MCP | ❌ | per tool | remove or fix (C-20); INV-02 |

### 7.3 Background services, scheduler, webhooks

| ID | Path | Actor | AuthN | AuthZ at run | Policy | Approval | Execution | Verification | Audit | Path-specific gaps |
|---|---|---|---|---|---|---|---|---|---|---|
| S-01 | Scheduler cron (incl. custom root command, prune, retention `rm -f`) | Automation on behalf of `CreatedBy` (since WP2 step 2.3; typed owner id and `CreatedVia` provenance since R1-03); ownerless tasks refused by default (`RequireOwner`, R1-04), grace period only on explicit opt-out; authority loss suspends + notifies; agent-created tasks keep agent rules | n/a | ✅ owner re-checked per run; ⚠️ grace period | ✅ PC-D1 rules (kill switch, protected) | none (owner approved by scheduling); a key-owned `Confirm` → Deny, no approval raised | automation runner → gateway → `TaskExecutor` (incl. self-backup and retention deletes); key-owned tasks keep the key's rules | ❌ | ✅ gateway AL + `TaskRunHistory` | INV-04 (grace), 43, 45 |
| S-02 | AutoUpdate | Automation on behalf of the feature owner (Settings); ownerless refused by default (`RequireOwner`, R1-04), grace period only on explicit opt-out; authority loss suspends + notifies | n/a | ✅ re-checked per update; ⚠️ grace period (opt-in) | ✅ PC-D1 rules | none; a key-owned `Confirm` → Deny, no approval raised | automation runner → gateway → `RecreateContainerAsync` | ⚠️ 15 s state check, no rollback | ✅ gateway AL + `UpdateHistory` | INV-04 (grace), 44, 45, 59 |
| S-03 | AutoPatch nightly | Automation on behalf of the Admin who took it over; **no owner → no run**; authority loss (also mid-cycle) suspends + notifies (R1-04) | n/a | ✅ config opt-in + owner re-checked per cycle | ✅ PC-D1 rules for the cycle; own exclude list per container | none; a key-owned `Confirm` → Deny, no approval raised | automation runner → gateway (`autopatch.run` per night + `container.update` per container) → `img-update.sh` as root | ✅ script verify + rollback | ✅ gateway AL + notification | INV-45 |
| S-04 | Webhook `POST /api/webhooks/{id}` | Automation on behalf of the webhook owner (R1-09); ownerless or authority lost → refused, switched off + notified | ✅ HMAC + provider freshness rule + durable delivery dedup, serialized per target (R1-10) | ✅ owner re-checked per delivery | ✅ PC-D1 rules (kill switch, protected, resolved target) | none; a key-owned `Confirm` → Deny, no approval raised | automation runner → gateway → restart / recreate / compose up (each planned container decided) / Git deploy (then on the app owner) as root; server = the webhook's, or `local` (R1-06, same as the Test gate) | ❌ | ✅ gateway AL (every delivery) + `WebhookLogs` | INV-42, 45, 57 |
| S-05 | Git deploy (via S-04 or U-29) | U-29: the person; S-04: the app owner (R1-08) | owner re-checked each run | ✅ gateway (stored app, revision; S-04 via automation runner) | ❌ | ❌ | clone + compose up of the decided snapshot (planned, explicit `-p` / `-f`; every planned container decided — R1-07/08) | ❌ | ✅ gateway | INV-04, 20, 24, 41 (branch), 45 |
| S-06 | Health / log / CVE / cert monitors | system | — | — | — | — | no direct mutation; emits events → A-03 | — | — | INV-27 (event text → prompt) |
| S-07 | VPN bootstrap at startup | system | — | — | — | — | `EnsureUpAsync` local | ❌ | ❌ log only | INV-41 (E-08), 45 |
| S-08 | Retention purge of AuditLog + Agent History | system | — | — | — | — | after `Audit:RetentionDays` (default 365, at least 30), daily; both through the chain: the trail is verified first (nothing removed otherwise), an `audit.retention` event naming both counts, then the signed new start (R1-23) | — | ✅ (`audit.retention`) | — |

### 7.4 Interactive terminals

| ID | Path | AuthZ | Policy | Approval | Execution | Audit | Path-specific gaps |
|---|---|---|---|---|---|---|---|
| T-01 | Container terminal `/terminal/{srv}/{ctr}` | **Admin** (PC-D2, WP2) | ✅ GW `terminal.container_open` (R1-17) | Control plane only (INV-59) | `docker exec` (remote: SSH as root first) | ✅ open + one close event | — (unknown server refused, no local fallback) |
| T-02 | Embedded terminal in container detail | **Admin** (PC-D2, WP2) | ✅ GW `terminal.container_open` (R1-17) | Control plane only (INV-59) | same | ✅ open + one close event | — |
| T-03 | Host shell `/server-terminal/{srv}` | Admin | ✅ GW `terminal.host_open` (R1-17) | n/a | `nsenter` / SSH root | ✅ open + one close event | Session ends ≤ 15 s after demotion or kill switch (R1-17) |

### 7.5 Web UI handlers

Common to all U rows: Actor = Human (cookie) · AuthN ✅ · AuthZ = `Require(role)` in the handler, role cached per
circuit (INV-09), **and** since WP2 step 2.2 the Mutation Gateway for every marked capability (`IUiMutationRunner`):
catalog level, tool switch and stricter stored level (INV-15), container target resolved (INV-57) · Policy ✅ for
humans under PC-D1: kill switch and protected resources (INV-21, 24) — tool lists, argument patterns and
confirmation are agent rules and do not apply · Approval n/a for humans under PC-D1, except an Admin's action on a
protected resource or Whiskers' control plane, which needs an Admin approval (INV-59, R1-16) · Execution = inside the gateway
scope with a deadline · Audit (R1-21): for the infrastructure pages exactly one event per attempt, the gateway's —
the page writes no row of its own; a role check in front of a confirmation dialog records its refusal as the
gateway's Denied event (`RequireAsync` → `IUiMutationRunner.RefuseAsync`); a failure the service reports as a value
(`false`, a failed tuple, a result object with `Success = false`, an output the handler recognises) is `Failed`, not
`Executed`; what the page knows about the result (backup file, deployment output, template or registry, previous
owner) is added to the event (`MutationScope.Note`, or as an argument). `GW ✅` below = migrated so. Migrated handlers:
`Dashboard` Start/Stop/RestartContainer (container and K8s workload), RemoveContainer, UpdateContainer,
RollbackContainer; `ContainerDetail` Start, Stop, Restart, Remove, SaveEnvAndRestart, RunMigration, RollbackMigration,
ExecuteMigrationFile, ExecuteSeedFile, ExecuteQuery, BackupDatabase; `Deploy` DeployFromForm, DeployFromCompose;
`AppStore` DeployTemplate, DeployRegistryImage; `ComposeEditor` Deploy; `Networks` CreateNetwork, DeleteNetwork;
`Firewall` ToggleFirewall, DeleteRule, OpenAddDialog and the dialog `AddFirewallRuleDialog` Submit; `NginxManager`
SaveConfig, ToggleSite, ReloadNginx; `SystemdManager` ManageService; `SslCerts` RenewCert, RenewAll; `CloudView` Act;
`GitDeployView` SaveApp, DeleteApp, StartDeploy, AdoptApp; `ScheduledTasksView` CreateTask, RunNow, ToggleEnabled,
AdoptTask, AdoptAll, DeleteTask; `VolumeBackupsView` CreateBackup, RestoreBackup, DeleteBackup. Since R1-22 the configuration
pages (U-20–U-22, U-25, U-28, U-30–U-34) write no rows of their own either: their role checks record a Denied event and
their writes carry a versioned, redacted diff; server onboarding (U-21) runs as `server.onboard` and its switch to
mTLS carries the server's diff.

| ID | Handler | Role | Verification | Audit | Path-specific gaps |
|---|---|---|---|---|---|
| U-01 | Dashboard start/stop/restart (Docker and K8s) | Operator | ❌ | GW ✅ (R1-21) | INV-43 |
| U-02 | Dashboard remove (force) | Admin | ❌ | GW ✅ (R1-21) | INV-43 |
| U-03 | Dashboard update | Operator | ❌ | GW ✅ (R1-21) | INV-43, 53 |
| U-04 | Dashboard rollback | Operator | ❌ | GW ✅ (R1-21) | INV-43, 53 |
| U-05 | Container detail start/stop/restart/remove | Operator / Admin | ❌ | GW ✅ (R1-21) | INV-43 |
| U-06 | Container detail save `.env` + restart | Admin | ⚠️ exit code | GW ✅ (R1-21) | INV-51 |
| U-07 | Container detail DB query (arbitrary SQL) | **Admin** (PC-D3, WP2) | ❌ | GW ✅ (R1-21) | — |
| U-08 | Artisan migrate / rollback / SQL file / seed | Admin | ❌ | GW ✅ (R1-21) | — |
| U-09 | Container detail DB backup | Admin | ❌ | GW ✅ (R1-21) | — |
| U-10 | Deploy form / raw compose | **Admin** (WP1) | ❌ | GW ✅ (R1-21) | INV-43 |
| U-11 | App store template / registry image | **Admin** (WP1) | ❌ | GW ✅ (R1-21) | INV-43 |
| U-12 | Compose editor | **Admin** (WP1) | ❌ | GW ✅ (R1-21) | INV-43 |
| U-13 | Networks create/remove | Admin | ❌ | GW ✅ (R1-21) | — (MCP admin since R1-05) |
| U-14 | Firewall enable/disable UFW | Admin | ❌ | GW ✅ (R1-21) | — |
| U-15 | Firewall rule add/remove (dialog submit has no page check; the gateway requires Admin for `firewall.rule_add` since R1-05) | Admin | ❌ | GW ✅ (R1-21) | INV-17 |
| U-16 | Nginx save/enable/disable site | Admin | ⚠️ `nginx -t` | GW ✅ (R1-21) | INV-41 (E-03) |
| U-17 | Nginx reload | Admin | ❌ | GW ✅ (R1-21) | — |
| U-18 | Systemd start/stop/restart | Admin | ❌ | GW ✅ (R1-21) | INV-43 |
| U-19 | SSL certificate renew | Admin | ❌ | GW ✅ (R1-21) | INV-43 |
| U-20 | Servers add/update/delete (+ vault secrets, SSH keys) | Admin | — | GW ✅ (versioned diff, R1-22) | INV-41 (TA-42 SSH user/host), 52 |
| U-21 | Server onboarding (root installs) | Admin | ⚠️ result object | GW ✅ (`server.onboard`; the switchover carries the server diff, R1-22) | INV-45 |
| U-22 | Servers pause/resume checks | **markup only** (the gateway decides `checks.pause`/`checks.resume`) | — | GW ✅ (no page row since R1-22) | INV-17 |
| U-23 | Cloud power / snapshot | Admin | ❌ | GW ✅ (R1-21) | INV-43, 52 |
| U-24 | Volume backup / restore / delete | Operator / Admin / Admin | ❌ | GW ✅ (R1-21) | INV-43 |
| U-25 | Self-backup create / delete / restore / download | Admin | ⚠️ archive validation | GW ✅ (download: audited before the first byte, refused without its row — R1-22) | INV-55 |
| U-26 | Scheduled tasks create / toggle / delete | create + delete: catalog level (Operator), then the gateway's effective level per task type (R1-05); toggle / take over: Admin | — | GW ✅ (R1-21) | INV-04 |
| U-27 | Scheduled task "Run now" | Operator; custom command, self-backup, DB backup and retention **Admin** (WP2; gateway resolves the same per task since R1-05) | ❌ | GW ✅ (R1-21) | INV-43 |
| U-28 | Webhooks create / take over / regenerate / toggle / delete / test | Admin; test = Operator | — | ✅ gateway (`webhook.*` on the stored webhook; create records the owner; test decided as the person, then runs as the owner — R1-09); since R1-22 the one gateway event per action, with a versioned diff, no page row | INV-04, 19 (test fires the real action at Operator) |
| U-29 | Git-deploy app save / delete; deploy | Admin; deploy **Admin** (R1-05, root-equivalent; was Operator) | ❌ | GW ✅ (R1-21) | INV-41 (branch), 46 |
| U-30 | Log-alert rules (Log search, AI triggers page) | Operator / Admin | — | GW ✅ (`logalert.*`; create/toggle/delete carry a versioned diff, the pattern as text only — R1-22) | INV-27 |
| U-31 | AI triggers save (prompt, preset, write grant) | Admin | — | GW ✅ (`aitrigger.save`; versioned diff, the prompt as text only — R1-22) | INV-23, 27, 28, 46 |
| U-32 | Guardrail policy edit / preset switch / kill switch (`guardrails.save`, `guardrails.kill_switch_off` since R1-11; the kill switch refuses every save but switching it off) | Admin | — | GW ✅ | INV-28 |
| U-33 | Approvals approve / reject | approve **and** reject (same rules since R1-15, INV-37): level ≥ the operation's **effective** level (the tool level, raised per call by R1-05 — e.g. Admin for a scheduled custom command or DB backup); a requester never decides their own non-interactive request, and decides an interactive one only while `RequireSecondApprover` is off (PC-D4); not after expiry | — | ✅ (R1-15: `ApprovalSeparationOfDutyTests`, `ApprovalStoreTests`) | — (INV-31 closed by R1-14; INV-32, 33, 34, 37 by R1-15) |
| U-34 | Settings: keys, tool levels, roles, whitelist, vault, registries, terminal (`config.*`, `mcp.key_*`, `vault.secret_*`, `registry.*` since R1-11) | Admin | — | GW ✅ (versioned, redacted diff; no page row since R1-22) | INV-15, 16 |

Reading the trail is not a mutation and not counted above, but it is governed (R1-23, INV-50): the Audit log and Agent
History pages and the audit rows of the Self status timeline are Admin only; `GET /audit/export` is Admin only, records
`audit.export` before it reads anything (503 without that row) and records a signed-in user's refusal.

### 7.6 Totals

*Correction:* the inventory summary in `audit/B-mutations.md` and `TECHNICAL_AUDIT.md` §6 said 87 paths; its rows add up
to 41 + 4 + 8 + 3 + 34 = **90**. Both documents are corrected in this PR.

| Class | Paths | Paths with path-specific gaps |
|---|---|---|
| MCP tools | 41 | 40 |
| Agent | 4 | 4 |
| Background / webhook / system | 8 | 7 |
| Terminals | 3 | 3 |
| UI handler groups | 34 | 34 |
| **Total** | **90** | **88** (M-24 and S-08 have no path-specific gap) |

All 90 paths were blocked by the shared gaps (INV-01, 22, 38, 39, 45, 48, 49; INV-49 closed by R1-23). WP2 (gateway) closes most of
them structurally. The path-specific gaps then close in WP3–WP7.

## 8. How this document is maintained

- A new mutating MCP tool, UI handler or background job gets a row in §7 in the same PR.
- When a WP closes a gap, the PR removes the invariant from the row's gap column and updates the invariant's
  status in `SECURITY_INVARIANTS.md`.
- When the gateway exists, §7 will be generated from the capability table (WP2) and checked in CI, so that the
  document cannot drift from the code.
