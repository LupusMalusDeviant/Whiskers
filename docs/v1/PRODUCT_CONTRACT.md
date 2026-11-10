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
contract than it says. Since R1-51 they do: README "Accepted limits (1.0)", SECURITY.md (threat model) and
`docs/operations/production-checklist.md`.

1. **Whiskers is root-equivalent by design.** The host-control plane runs commands as host root. Docker create
   rights are root on the host. The contract governs *who may reach* that power and *what is recorded*; it
   does not make the power smaller.
2. **An Admin with shell access to the Whiskers host is outside the contract.** They can edit the database,
   the JSON stores and the binaries. With `VAULT_KEY` set, the audit hash chain (INV-49) makes database-only
   edits *detectable*, except that the events after the newest checkpoint (at most a minute) can be cut off
   unnoticed. Without `VAULT_KEY` nothing is signed and a database writer can rewrite the trail consistently.
   A host that holds `VAULT_KEY`, the checkpoint file and the database can rewrite all three consistently
   (ADR-0006).
3. **Commands typed inside an interactive terminal are not policy-checked one by one.** The contract covers
   opening the session (authorization, kill switch, audit). There is **no keystroke or output transcript** in 1.0
   (PC-D2): what was typed inside a session is not recorded.
4. **Single replica.** Whiskers 1.0 is a single-instance appliance. Availability and RPO/RTO follow from that
   and are documented in the backup guide (`docs/operations/backup-and-recovery.md`, R1-33).
5. **No per-server or tenant scoping in 1.0.** A principal's level applies to every managed server (A-17): roles
   (Viewer, Operator, Admin) and key levels are global, so an Operator on one server is an Operator on all.
6. **Out-of-band changes are invisible.** Changes made directly on a host, in a cloud console or via another
   tool are not governed. Whiskers may observe their effects; it does not attribute them.
7. **Local accounts have no second factor (R1-49).** A local password alone signs in (with lockout and rate limit).
   Production deployments sign in through their identity provider (OIDC) with MFA; local accounts are for the first
   setup and break-glass access (`docs/operations/local-accounts.md`).

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

### 6.1 Release decisions

Every decision the release gate relies on (contract 0008, D5): an **exception** that lets a ⚠️ invariant ship
(`RELEASE_GATE.md` *Accepted exceptions*, with a limitation in the product documentation that names the invariant), the
**retirement** of an invariant ID (*Retired invariants*), and a **known violation** that lets exactly one patch release
ship with a ❌/❓ (*Known violations*, from 1.0.1 on; never at X.Y.0). *Release* is that exact version for a known
violation and — otherwise. *Status* is `Proposed` or `Accepted`; *Date* is the ISO date of acceptance; *Decided by*
names the maintainer who accepted it. **An `Accepted` row is immutable**: it is never edited or deleted, and an ID is
never reused; a changed decision is a new row with a new ID (R18, checked against the base on every pull request and
against the previous release tag in every release run). A retirement that a PC-D decision above already settled is
recorded here as a `retirement` that cites it.

| ID | Kind | Invariant | Decision | Release | Status | Date | Decided by |
|---|---|---|---|---|---|---|---|

No release decision is recorded yet.

## 7. Path matrix (current state)

One row per mutation path from [`audit/B-mutations.md`](audit/B-mutations.md), updated for WP1 and WP2 step 2.1. Each row shows
the chain **Actor → AuthN → AuthZ → Policy → Approval → Execution → Verification → Audit** as it is today,
and the invariants the path violates.

**Every gap is a v1 blocker.** A row is done when its gap column is empty.

Legend: ✅ holds · ⚠️ partial · ❌ missing · n/a not applicable · **GR?** guardrails only when the Agent module is on
(INV-26; fixed for every path in WP2 step 2.1 — guardrails are core) · **AL** AuditLog row · **AH** Agent History row only.
In §7.2–7.4 an "Approval" cell that begins with ✅ marks a path on which a person gives an approval (a call can wait
for it and run after it; `AuditPathMatrixTests` then requires both events); any other cell — none, n/a, a terminal
`Confirm → Deny` — marks a path that never waits for one.

**When a mutation attempt begins (user decision 2026-11-07, INV-45).** An attempt begins when the handler requests the
capability from the gateway (`RunAsync`/`RefuseAsync` or the surface's equivalent: the MCP filter's gateway call, the
agent invoker's, the automation runner's); returning before that — invalid input, a cancelled confirmation, an object
not found — changes nothing and is not an attempt, so it has no audit event. It changes nothing because no page writes
state itself and every guarded service refuses outside a gateway decision (the tests are named in INV-45's evidence).
Likewise no attempt: an AI trigger run refused before its session starts (too many runs in parallel, its preset
missing, its owner without authority — A-03) and a volume-backup retention whose backup list cannot be read (nothing
was requested to delete — S-01). A DB retention lists inside its `db.backup_delete` decision, so an unreadable or
truncated listing is that event's Failed outcome; a DB-retention deletion a safety check skips (an unsafe database name
or container key) is the gateway's Denied event of `db.backup_delete`. A scheduled run is one event even when a call
it makes before its decision fails: the scheduler records it as the gateway's Denied event of the task's capability
(S-01). An attempt that has begun and is cancelled before its decision — the caller gone, a run's deadline passed —
is still one Denied event.

**Gaps shared by every path today** (not repeated per row): INV-01 (string actor), INV-20 for UI/automation/terminal,
INV-22 (no decision record — since R1-12 every gateway decision is recorded, so it remains only for paths not yet on the gateway), INV-38 (no gateway — MCP tools and agent go through it since WP2 step 2.1), INV-45 (closed: since R1-19 every gateway attempt is one complete event with typed fields, MCP tools and UI pages write none of their own since R1-20/R1-21/R1-22, and every row below is proved by `AuditPathMatrixTests` — each attempt one complete event for every outcome the path can have),
INV-48 (audit after the fact, fail-open — since R1-19 the gateway refuses without a durable intent and keeps a failed outcome or refusal recoverable and visible; it remains for `AuditLogService` callers outside the gateway, whose lost rows are now at least visible). INV-49 holds since R1-23 for every audit row (hash chain with signed checkpoints, ADR-0006). Rows below list only the path-specific gaps.

### 7.1 MCP tools (direct `/mcp` calls; the in-process agent invokes the same methods)

Common to all M rows since WP2 step 2.1: Actor = API key or web user · AuthN ✅ (Bearer / cookie) · AuthZ ✅ in the
Mutation Gateway: catalog level, tool switch and stricter stored level for every principal (INV-15), `AllowedTools`
cannot widen (INV-16); the scope check inside the tool still runs · Target: every kind resolved before policy
(INV-57 ✅; the tool runs on the resolved identity, contract 0012) · Policy ✅ core guardrails (INV-26); protected resources and the built-in
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
| M-01 | `start_container` | write | ✅ read back (R1-36) | GW ✅ + details | INV-43 |
| M-02 | `stop_container` | write | ✅ read back (R1-36) | GW ✅ + details | INV-43 |
| M-03 | `restart_container` | write | ✅ read back (R1-36) | GW ✅ + details | INV-43 |
| M-04 | `update_container` | write | ✅ the new container (same name) read back (R1-36); no health check; a failed snapshot is a warning, not a stop (that rule is AutoUpdate's) | GW ✅ + details | INV-43, 44, 53 |
| M-05 | `deploy_app` | admin | ✅ the returned container read back (R1-36); a deployment that names none: `NotVerifiable` with its reason | GW ✅ + details | INV-43 (volumes checked since R1-16), 62 (out of scope of contract 0006: a `DeploymentRequest` is not decided on its exposures) |
| M-06 | `set_container_env` | **admin** (R1-05, root-equivalent like the page's .env save; was write) | `NotVerifiable` + reason (the process reads the .env, R1-37) | GW ✅ + details | INV-43, 51, 62 (the exposures of the planned project decided, run and re-checked through `ComposeRunner`, contract 0006) |
| M-07 | `deploy_compose` | admin | ✅ every container of the project the plan resolved (a top-level `name:` included) read back (R1-37); an interpolated project name: `NotVerifiable` + reason | GW ✅ + details | INV-43 (plan incl. mounts decided since R1-16), 62 (exposures decided and re-checked after `up`, contract 0006) |
| M-08 | `execute_command` | admin | `NotVerifiable` + reason (arbitrary command; its exit code decides, R1-37) | GW ✅ + details (redacted) | INV-24 (free text ✅ R1-16), 43, 59 |
| M-09 | `add_firewall_rule` | **admin** (R1-05; was write) | ✅ `ufw show added` read back (R1-37) | GW ✅ + details | 43 |
| M-10 | `remove_firewall_rule` | **admin** (R1-05; was write) | `NotVerifiable` + reason (ufw renumbers after a delete, R1-37) | GW ✅ + details | 43 |
| M-11 | `update_nginx_config` | **admin** (R1-05; was write) | ✅ `nginx -t` pre-check; the site file read back (R1-37) | GW ✅ + details | 41 (E-03) |
| M-12 | `manage_systemd_service` | **admin** (R1-05; was write) | ✅ `systemctl is-active` / `is-enabled` read back (R1-37) | GW ✅ + details | 43 |
| M-13 | `renew_ssl_certificate` | admin (R1-05) | ✅ certificate valid for at least 29 more days (certbot's 30-day renewal window, one day of slack) read back (R1-37) | GW ✅ + details | INV-43 |
| M-14 | `renew_mtls_certificate` | admin | ⚠️ validator in service; `NotVerifiable` + reason (R1-37) | GW ✅ + details | INV-58 |
| M-15 | `execute_query` | **admin** (PC-D3) | `NotVerifiable` + reason (arbitrary SQL, R1-37) | GW ✅ | INV-19 (PC-D3), 24, 43 |
| M-16 | `backup_database` | admin (R1-05) | ✅ dump file on the host, not empty (R1-37) | GW ✅ + details | INV-43 |
| M-17 | `set_dns_record` | write | ✅ provider lists the record with the value (R1-37) | GW ✅ + details | INV-43, 52 (DNS token) |
| M-18 | `delete_dns_record` | write | ✅ provider no longer lists the record (R1-37) | GW ✅ + details | INV-43, 52 |
| M-19 | `create_network` | **admin** (R1-05; was write) | ✅ listed with its driver (R1-37) | GW ✅ + details | — |
| M-20 | `remove_network` | **admin** (R1-05; was write) | ✅ no longer listed (R1-37) | GW ✅ + details | — |
| M-21 | `connect_container_to_network` | **admin** (R1-05; was write) | ✅ container attached (R1-37) | GW ✅ | — |
| M-22 | `disconnect_container_from_network` | **admin** (R1-05; was write) | ✅ container no longer attached (R1-37) | GW ✅ | — |
| M-23 | `create_scheduled_task` | write; **effective admin** for CustomCommand, SelfBackup, every DB backup, and volume backups with retention (`maxBackups` > 0, default 7) — resolved before the caller check and the approval (R1-05) | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ + details | INV-04 |
| M-24 | `delete_scheduled_task` | write | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ + details | — |
| M-25 | `run_scheduled_task` | write; **effective admin** for the stored task's CustomCommand, SelfBackup, DB backup or retention, resolved before the caller check and the approval (R1-05) | `NotVerifiable` + reason (the task's own capability is verified, R1-37) | GW ✅ + details | INV-24 (policy sees only `taskId`), 30, 43 |
| M-26 | `cloud_power_on` | **admin** (R1-05; was write) | ✅ provider reports running, polled (R1-37) | GW ✅ | INV-43, 52 |
| M-27 | `cloud_shutdown` | **admin** (R1-05; was write) | ✅ provider reports off, polled (R1-37) | GW ✅ | 43, 52 |
| M-28 | `cloud_reboot` | **admin** (R1-05; was write) | `NotVerifiable` + reason (same end state, R1-37) | GW ✅ | INV-43, 52 |
| M-29 | `cloud_hard_reset` | **admin** (R1-05; was write) | `NotVerifiable` + reason (same end state, R1-37) | GW ✅ | 43, 52 |
| M-30 | `cloud_create_snapshot` | **admin** (R1-05; was write) | `NotVerifiable` + reason (created asynchronously, R1-37) | GW ✅ | INV-43, 52 |
| M-31 | `hetzner_enable_rescue` | admin | ✅ Hetzner reports rescue on, polled (R1-37) | GW ✅ + details | INV-51 (returns root password), 52 |
| M-32 | `hetzner_disable_rescue` | **admin** (R1-05; was write) | ✅ Hetzner reports rescue off, polled (R1-37) | GW ✅ | INV-52 |
| M-33 | `hetzner_enable_backups` | **admin** (R1-05; was write) | ✅ Hetzner reports a backup window, polled (R1-37) | GW ✅ | INV-52 |
| M-34 | `hetzner_disable_backups` (deletes backups) | **admin** (R1-05; was write) | ✅ Hetzner reports no backup window, polled (R1-37) | GW ✅ | 52 |
| M-35 | `hetzner_change_server_type` | **admin** (R1-05; was write) | ⚠️ the action's error status fails the call; the resize itself `NotVerifiable` + reason (R1-37) | GW ✅ | 43, 52 |
| M-36 | `hetzner_delete_snapshot` (irreversible) | **admin** (R1-05; was write) | ✅ Hetzner no longer finds the image (R1-37) | GW ✅ | 52 |
| M-37 | `create_log_alert` (feeds AI triggers) | write | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ | INV-27, 46 |
| M-38 | `pause_server_checks` | write | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ | — |
| M-39 | `resume_server_checks` | write | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ | — |
| M-40 | `preview_auto_patch` (pulls images) | write | `NotVerifiable` + reason (a preview changes no container, R1-37) | GW ✅ | — |
| M-41 | `instruct_agent` → agent run | read | per tool | per tool | INV-08 (since R1-26 the key or user is re-read before every tool call of the run) |
| M-42 | `create_git_deploy_app` (`gitdeploy.save`, contract 0001) | admin | `NotVerifiable` + reason (Whiskers' own record); the tool reads the stored app back and fails on a mismatch | GW ✅ + details | the approving person becomes the owner, never the key (no person behind the call → `denied`); the token is never echoed or logged; a repository URL with credentials is refused; a name is unique (case-insensitive, checked under the store lock) |
| M-43 | `set_git_deploy_token` (`gitdeploy.set_token`, contract 0001) | admin | `NotVerifiable` + reason (Whiskers' own record); the tool reads `credentials` back | GW ✅ + details | stale `revision` refused (`conflict`); the approving person becomes the owner (R1-08) |
| M-44 | `get_git_deploy_app` (contract 0001) | read | — | — | answers `credentials: stored/none`, never the token; a read a policy holds for approval answers `pending_approval` (the approval named in the message; reads have no operation id) |
| M-45 | `create_git_deploy_webhook` (`webhook.save`, contract 0001) | admin | `NotVerifiable` + reason (Whiskers' own record); the tool reads the webhook back | GW ✅ + details | github only; the approving person becomes the owner; the target resolves through the app (its server); a webhook for an app moved or renamed after the decision is refused (`conflict`); the secret appears once, in the answer only; a second webhook for the same app is not refused (`get_git_deploy_app` lists them) |
| M-46 | `rotate_git_deploy_webhook_secret` (`webhook.rotate_secret`, contract 0001) | admin | `NotVerifiable` + reason (Whiskers' own record); the tool reads the webhook back | GW ✅ + details | git-deploy webhooks only; the new secret appears once, in the answer only |
| M-47 | `assess_update_risk` (`image.assess_update`, pulls the candidate image) | **write** (R1-48; was read — a pull changes the host) | `NotVerifiable` + reason (an assessment changes no container) | GW ✅ + details; an assessment that could not be made is `Failed` | INV-20/38: the pull is a composed `image.pull` (`MutationCompositions`) |
| M-48 | `migrate_mtls_client_credential` (`mtls.client_migrate`, R1-44, contract 0007; row added by R1-51) | admin | `NotVerifiable` + reason: every host step is confirmed inside the guarded rollout (the running ghostunnel's allow-list read back, the new credential admitted and, before narrowing, the shared one refused) | GW ✅ + details | INV-58 (holds per host only after the migration) |
| M-49 | `renew_mtls_client_credential` (`mtls.client_renew`, R1-44, contract 0007; row added by R1-51) | admin | `NotVerifiable` + reason: the rollout confirms the new certificate through a rebuilt connection (a rotation reads the allow-list back) before it reports success | GW ✅ + details | INV-58 |

The four writes M-42, M-43, M-45 and M-46 are not offered to the AI agent (`AgentToolRegistry.NonAgentTools`): credentials never
reach a model. Every error of the five, the gateway's refusals and a pending approval included, is the coded envelope of
`McpErrorEnvelope` (contract 0001; a pending write carries the operation and approval ids, a pending read neither — see M-44).

### 7.2 Agent paths

| ID | Path | Actor | AuthN | AuthZ | Policy | Approval | Execution | Verification | Audit | Path-specific gaps |
|---|---|---|---|---|---|---|---|---|---|---|
| A-01 | Web agent chat | Human → Agent | ✅ cookie | ✅ ceiling + tool switch (INV-15) | ✅ | ✅ self-confirm allowed and recorded unless `RequireSecondApprover` (PC-D4); ends with the chat (R1-15); persisted, and the gateway runs only an approval bound to the operation, arguments, target and policy version (R1-14); the chat's buttons decide only through the recorded approval, under the same rules as the Approvals page | invoker → gateway → tool | ❌ | gateway AL + per tool (as in 7.1) | — (INV-32, 36 closed by R1-15) |
| A-02 | `instruct_agent` | ApiKey/Human → Agent | ✅ | ✅ ceiling + tool switch, re-read before every tool call (R1-26) | ✅ | Confirm → auto-deny ✅ | invoker → gateway → tool | ❌ | gateway AL + per tool | INV-08 ✅ (R1-26) |
| A-03 | AI trigger | Automation (`trigger:<id>`, own principal per trigger since R1-03) | n/a | ✅ read-only unless an Admin granted capabilities + targets; every trigger has an owner, re-checked before each run and per mutation (nested included), lost authority or no owner → suspended + Admin notified (R1-13); tool switch ✅ | ✅ pinned to the trigger's preset with the global kill switch; missing preset → no run (R1-12) | Confirm → Deny in the gateway, nested mutations included, no approval raised ✅ (R1-13); Allow runs unattended within the grant | invoker → gateway → tool | ❌ | gateway AL + per tool + `agent.run` | INV-04, 06, 27, 40 |
| A-04 | Claude Code CLI runtime (dead code) | fixed key | ✅ key | ⚠️ fail-open rights check | GR? | approve-then-retry | MCP | ❌ | per tool | remove or fix (C-20); INV-02 |

### 7.3 Background services, scheduler, webhooks

| ID | Path | Actor | AuthN | AuthZ at run | Policy | Approval | Execution | Verification | Audit | Path-specific gaps |
|---|---|---|---|---|---|---|---|---|---|---|
| S-01 | Scheduler cron (incl. custom root command, prune, retention `rm -f`) | Automation on behalf of `CreatedBy` (since WP2 step 2.3; typed owner id and `CreatedVia` provenance since R1-03); ownerless tasks refused by default (`RequireOwner`, R1-04), grace period only on explicit opt-out; authority loss suspends + notifies; agent-created tasks keep agent rules | n/a | ✅ owner re-checked per run; ⚠️ grace period | ✅ PC-D1 rules (kill switch, protected) | none (owner approved by scheduling); a key-owned `Confirm` → Deny, no approval raised | automation runner → gateway → `TaskExecutor` (incl. self-backup and retention deletes); key-owned tasks keep the key's rules | ✅ container restart (R1-36), DB / volume / self-backups and their retention read back (R1-37); custom command and prune `NotVerifiable` + reason | ✅ gateway AL + `TaskRunHistory` | INV-04 (grace), 43 |
| S-02 | AutoUpdate | Automation on behalf of the feature owner (Settings); ownerless refused by default (`RequireOwner`, R1-04), grace period only on explicit opt-out; authority loss suspends + notifies | n/a | ✅ re-checked per update; ⚠️ grace period (opt-in) | ✅ PC-D1 rules | none; a key-owned `Confirm` → Deny, no approval raised | automation runner → gateway → `RecreateContainerAsync` | ✅ recreate read back + 15 s state check; a failed one rolled back through the gateway, verified (R1-36) | ✅ gateway AL + `UpdateHistory` | INV-04 (grace), 44, 59 |
| S-03 | AutoPatch nightly | Automation on behalf of the Admin who took it over; **no owner → no run**; authority loss (also mid-cycle) suspends + notifies (R1-04) | n/a | ✅ config opt-in + owner re-checked per cycle | ✅ PC-D1 rules for the cycle; own exclude list per container | none; a key-owned `Confirm` → Deny, no approval raised | automation runner → gateway (`autopatch.run` per night + `container.update` per container) → `img-update.sh` as root | ✅ script verify + rollback | ✅ gateway AL (also a night refused for no owner or an unreadable owner entry) + notification | — |
| S-04 | Webhook `POST /api/webhooks/{id}` | Automation on behalf of the webhook owner (R1-09); ownerless or authority lost → refused, switched off + notified | ✅ HMAC + provider freshness rule + durable delivery dedup, serialized per target (R1-10) | ✅ owner re-checked per delivery | ✅ PC-D1 rules (kill switch, protected, resolved target) | none; a key-owned `Confirm` → Deny, no approval raised | automation runner → gateway → restart / recreate / compose up (each planned container decided) / Git deploy (then on the app owner) as root; server = the webhook's, or `local` (R1-06, same as the Test gate) | ✅ restart / recreate (R1-36) and compose up (R1-37) read back; Git deploy `NotVerifiable` + reason (R1-37) | ✅ gateway AL (every accepted delivery, also one that ends before its decision) + `WebhookLogs` | INV-42, 57, 62 (a compose webhook runs through `ComposeRunner`; a key-owned run that needs an exposure confirmation is refused) |
| S-05 | Git deploy (via S-04 or U-29) | U-29: the person; S-04: the app owner (R1-08) | owner re-checked each run | ✅ gateway (stored app, revision; S-04 via automation runner) | ✅ PC-D1 rules in the gateway (`gitdeploy.deploy`, Admin: kill switch, protected resources; `GitDeployView.razor:313`, `GitDeployOwners.cs:49`) | none for the person (PC-D1); a key-owned `Confirm` → Deny, no approval raised | clone + compose up of the decided snapshot (planned, explicit `-p` / `-f`; every planned container decided — R1-07/08) | `NotVerifiable` + reason (R1-37) | ✅ gateway | INV-04, 20, 24, 41 (branch), 62 (pull, build, image inspection and re-check, contract 0006) |
| S-06 | Health / log / CVE / cert monitors | system | — | — | — | — | no mutation of workloads, files or settings; emits events → A-03. Designed exemption (R1-48): the CVE scan changes host caches — `apt-get update` (package index) and Trivy (pulls its scanner image, runs it as a throw-away `--rm` container with a cache volume); pinned to the CVE monitor (and the update-risk assessment, M-47, inside its own decision). A scan a person starts is U-40 (`cve.scan`) | — | — | INV-27 (event text → prompt) |
| S-07 | VPN bootstrap at startup (in-app provider `tailscale`/`netbird`) | Automation on behalf of the admin who took "VPN bring-up" over (Settings → Automation owners, R1-43); **no owner → no run**, never a grace period; authority loss suspends | n/a | ✅ owner re-checked at every start | ✅ PC-D1 rules (kill switch, protected) | none (a recorded owner, no implied confirmation); a key-owned `Confirm` → Deny | automation runner → gateway (`vpn.bootstrap`, Admin) → `EnsureUpAsync`; argv elements, validated hostname/URL, key as a 0600 file (`--auth-key=file:` / `--setup-key-file=`) | `NotVerifiable` + reason (the CLI's exit code decides; a failure is `Failed`) | ✅ gateway AL (executed, failed, or Denied also for no owner/blocked); result in Settings; `Vpn:FailStartupOnError` stops the start | The legacy `entrypoint.sh` bring-up (`VPN_PROVIDER` unset and Whiskers' provider not `tailscale`/`netbird` — never next to the in-app one) is the host operator's start, outside the gateway; its key is a 0600 file too (F-21). No child process inherits a key (`SecretEnvironment`; the entrypoint unsets them before it starts `tailscaled` or anything else); an env-delivered key stays visible in `docker inspect` — prefer `…AuthKeyFile` / `…SetupKeyFile`. The Helm chart sets `VPN_PROVIDER=none` and `Vpn__Provider=none` on every render (R1-46): neither bring-up runs in the pod, mesh access is the pinned Tailscale sidecar |
| S-08 | Retention purge of AuditLog + Agent History | system | — | — | — | — | after `Audit:RetentionDays` (default 365, at least 30), daily; both through the chain: the trail is verified first (nothing removed otherwise), an `audit.retention` event naming both counts, then the signed new start (R1-23) | — | ✅ (`audit.retention`) | — |
| S-09 | Startup upgrade (R1-34) | system | — | — | — | — | a release other than the last one started, or pending migrations, on an existing database: a `pre-upgrade-…` self-backup right after the vault key check and before any data or schema is rewritten; it failing stops the start with nothing changed (opt-out `WHISKERS_UPGRADE_SKIP_BACKUP`, logged); an unfinished upgrade keeps its backup across restarts (`.upgrade-pending`), and neither the scheduled retention nor the upgrade pruning deletes it | ✅ migration history read back by EF; a failed migration stops the start and logs the backup to restore | log only (no audit row before the schema is current) | outside the gateway by design (R1-48): `CreatePreUpgradeBackupAsync` / `DeletePreUpgradeBackupAsync` (the latter refuses any non-pre-upgrade backup), called only by `UpgradeGuard` (pinned) |
| S-10 | CLI restore `--restore-backup <id>` or `latest-pre-upgrade`, optionally `--for-previous-release` (R1-34) | host operator (shell on the container or host, the data directory) | outside the app: host access | — | — | — | the same checked path as U-25 (`BeginRestoreAsync`: archive validation, pre-restore safety backup, PostgreSQL dry run; for the previous release without running this release's migrations on the archive), only on-box ids from `backups/self`; applied at the next start | as U-25 | ✅ `selfbackup.restored` written by the next start of this release or any from R1-23 on; a previous release from before R1-23 records none | INV-55; outside the gateway by design (R1-48): `IBackupService.BeginHostRestoreAsync`, called only by `RestoreCli` from `Program` (pinned by `MutationGatewayCoverageTests`) |
| S-11 | CLI account recovery `--recover-local-account <email>` (R1-49) | host operator (shell in the running container) | outside the app: host access | existing local account only; never creates an account or grants a role | — | — | issues a one-time recovery code (password removed, new security stamp = every session ends), printed to the terminal only; refuses without `--force` while a code could not be redeemed (guardrails fail-closed, or `recovery_code` blocked under the kill switch; exit code 4, contract 0009 A1.2) | — (own account store) | ✅ `auth.recovery_issued` written **before** the change; without it nothing changes | INV-61 |
| S-12 | Recovery-code redemption `/account/recover` (R1-49) | whoever holds the code (anonymous page, like the sign-in) | the code: one use, 24 h, hashed; a wrong code counts towards the lockout; 10 attempts per client (IPv4 address or IPv6 /64, from the circuit's hub connection) per minute and 10 per account per 15 minutes (service-side limiter, the circuit is outside the HTTP limiter); refused while `AUTH_DISABLED` | sets only that account's own password | — | — | Identity: code used up, password set, stamp changed in one save | — (own account store) | ✅ `auth.recovery_redeemed` / `auth.recovery_refused` (reason only, never the code) | INV-61 |
| S-13 | First-run setup wizard: the first administrator (W1, R1-48) | the person holding the one-time setup token (host-level: it is read from the data directory or `WHISKERS_SETUP_TOKEN`) | the setup token | only while no Admin exists — `IWhitelistService.AddFirstAdminAsync` and `IRoleService.SetFirstAdminAsync` refuse once any Admin exists (the wizard holds `AccessControlWriteGate`, the one write boundary every role and whitelist writer shares, across the check and both writes, and the Admin is published only after it was saved: two submits at once create at most one, no other writer runs in between) | — (no person exists the gateway could decide for) | — | the address is added to the whitelist (its enforcement unchanged), then the Admin role; pinned to `SetupStateService` | `NotVerifiable` (Whiskers' own record; the role store is the setup state's truth) | ✅ `setup.first_admin` written **before** anything; without it nothing is written (fail closed) | designed exemption from the gateway (INV-38); afterwards whitelist and roles change only through it |
| S-14 | Federated identity binding at sign-in (R1-27, contract 0003) | the person signing in through Google/OIDC whose verified address is an entry an Admin configured (role or whitelist) | the provider (issuer + subject); `email_verified=true` from a trusted provider (`GoogleAuth:TrustEmailVerified` default true, `Oidc:TrustEmailVerified` default false) for the standard `email` claim | only while an explicit Admin exists, only for an entry, only when the account has no binding for any issuer yet; everything else → a pending request, the sign-in refused | — (no person exists the gateway could decide for) | — | `IIdentityBindingStore.BindAtSignInAsync` under `AccessControlWriteGate` and the file lock shared with the host CLI, on the file as re-read there; under them it decides the admission again — an explicit Admin exists, the address is an entry the whitelist admits, the standard `email` claim is verified by a trusted provider, the subject is unbound and the account has no binding — then persists before it publishes; pinned to `FederatedAdmission` | `NotVerifiable` (Whiskers' own record) | ✅ `auth.identity_bound` (method `verified_email`) written **before** the binding; without it nothing is bound and the sign-in is refused | designed exemption from the gateway (INV-38), like S-13; confirming or removing bindings afterwards goes through it (U-36) |
| S-15 | CLI binding confirmation `--confirm-identity-binding <email> [--issuer <issuer>]` (R1-27) | host operator (shell in the running container) | outside the app: host access | the newest pending request of that configured address only (several issuers pending → exit 4, nothing changed); never creates an entry or grants a role | — | — | binds the pending subject (replacing an older binding of the same account and issuer) in the file the running instance uses, under the exclusive lock both share and on the file as re-read there; the running instance follows it at once (no restart); never starts the web host | — (own record) | ✅ `auth.identity_bound` (actor `cli (--confirm-identity-binding)`) written **before** the change; without it nothing changes | INV-05 |
| S-16 | Endpoint-check monitor (R1-57; row added by R1-51) | Automation on behalf of the check's owner (the Admin who saved it); **no owner → no run**, never a grace period; authority loss suspends the check | n/a | ✅ owner re-checked before every run | ✅ PC-D1 rules in the gateway (`endpointcheck.run`, Admin) | none (a recorded owner, no implied confirmation) | automation runner → gateway → the fixed probe commands of the check (resolve, curl, nc, openssl) on its server | `NotVerifiable` + reason (a probe changes nothing; its result is the check's answer) | ✅ gateway AL; the run is recorded with the check | — |

### 7.4 Interactive terminals

| ID | Path | AuthZ | Policy | Approval | Execution | Audit | Path-specific gaps |
|---|---|---|---|---|---|---|---|
| T-01 | Container terminal `/terminal/{srv}/{ctr}` | **Admin** (PC-D2, WP2) | ✅ GW `terminal.container_open` (R1-17) | ✅ an Admin's approval, control plane only (INV-59) | `docker exec` (remote: SSH as root first) | ✅ open + one close event | — (unknown server refused, no local fallback) |
| T-02 | Embedded terminal in container detail | **Admin** (PC-D2, WP2) | ✅ GW `terminal.container_open` (R1-17) | ✅ an Admin's approval, control plane only (INV-59) | same | ✅ open + one close event | — |
| T-03 | Host shell `/server-terminal/{srv}` | Admin | ✅ GW `terminal.host_open` (R1-17) | n/a | `nsenter` / SSH root | ✅ open + one close event | Session ends ≤ 15 s after demotion or kill switch (R1-17) |

### 7.5 Web UI handlers

Common to all U rows: Actor = Human (cookie) · AuthN ✅ · AuthZ = `Require(role)` in the handler, role cached per
circuit (INV-09; since R1-26 the circuit re-checks its session every 30 s and every UI mutation re-checks it, so a
cached role never authorizes a mutation), **and** since WP2 step 2.2 the Mutation Gateway for every marked capability (`IUiMutationRunner`):
catalog level, tool switch and stricter stored level (INV-15), every target kind resolved before policy (INV-57 ✅: the delegate receives the resolved object, contract 0012) · Policy ✅ for
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
RollbackContainer; `ContainerDetail` Start, Stop, Restart, Remove, SaveEnvAndRestart; its database tab
`ContainerDatabasePanel` RunMigration, RollbackMigration, ExecuteMigrationFile, ExecuteSeedFile, ExecuteQuery,
BackupDatabase; `Deploy` DeployFromForm, DeployFromCompose;
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
| U-01 | Dashboard start/stop/restart (Docker and K8s) | Operator | ✅ Docker read back (R1-36); K8s `NotVerifiable` + reason (pods are replaced, R1-37) | GW ✅ (R1-21) | INV-43 |
| U-02 | Dashboard remove (force) | Admin | ✅ read back (R1-36) | GW ✅ (R1-21) | INV-43 |
| U-03 | Dashboard update | Operator | ✅ read back (R1-36) | GW ✅ (R1-21) | INV-43, 53 |
| U-04 | Dashboard rollback | Operator | ✅ read back (R1-36) | GW ✅ (R1-21) | INV-43, 53 |
| U-05 | Container detail start/stop/restart/remove | Operator / Admin | ✅ read back (R1-36) | GW ✅ (R1-21) | INV-43 |
| U-06 | Container detail save `.env` + restart | Admin | ⚠️ exit code; `NotVerifiable` + reason (the process reads the .env, R1-37) | GW ✅ (R1-21) | INV-51, 62 (contract 0006) |
| U-07 | Container detail DB query (arbitrary SQL) | **Admin** (PC-D3, WP2) | `NotVerifiable` + reason (arbitrary SQL, R1-37) | GW ✅ (R1-21) | — |
| U-08 | Artisan migrate / rollback / SQL file / seed | Admin | `NotVerifiable` + reason (arbitrary command, R1-37) | GW ✅ (R1-21) | — |
| U-09 | Container detail DB backup | Admin | ✅ dump file on the host, not empty (R1-37) | GW ✅ (R1-21) | — |
| U-10 | Deploy form / raw compose | **Admin** (WP1) | ✅ the form's container (R1-36); raw compose `NotVerifiable` + reason (no project name to read back, R1-37) | GW ✅ (R1-21) | INV-43, 62 (out of scope of contract 0006: the compose mode, `ComposeFileParser`) |
| U-11 | App store template / registry image | **Admin** (WP1) | `NotVerifiable` + reason (the deployment answers with the compose output, not a container, R1-37) | GW ✅ (R1-21) | INV-43, 62 (contract 0006) |
| U-12 | Compose editor | **Admin** (WP1) | ✅ every container of the project the plan resolved read back (R1-37); an interpolated project name `NotVerifiable` + reason | GW ✅ (R1-21) | INV-43, 62 (contract 0006) |
| U-13 | Networks create/remove | Admin | ✅ listed with its driver / no longer listed (R1-37) | GW ✅ (R1-21) | — (MCP admin since R1-05) |
| U-14 | Firewall enable/disable UFW | Admin | ✅ `ufw status` read back (R1-37) | GW ✅ (R1-21) | — |
| U-15 | Firewall rule add/remove (the dialog's submit has no page role check of its own: it runs `firewall.rule_add` through `IUiMutationRunner`, `AddFirewallRuleDialog.razor:55`, and the gateway refuses a caller below Admin at the click; `FirewallService.AddRuleAsync` runs only inside that decision — `MutationGatewayCoverageTests`) | Admin (gateway, catalog level) | ✅ add: `ufw show added` read back; remove: `NotVerifiable` + reason (R1-37) | GW ✅ (R1-21) | INV-17 (pinned on the real handlers: `FirewallRuleRoleTests` — the dialog's submit below Admin, also after a demotion, and the page's rule removal below Admin are refused and recorded as Denied, `IFirewallService` is never called; an Admin's submit runs after its audit intent) |
| U-16 | Nginx save/enable/disable site | Admin | ✅ `nginx -t`; the site file and the sites-enabled link read back (R1-37) | GW ✅ (R1-21) | INV-41 (E-03; site names validated since R1-42) |
| U-17 | Nginx reload | Admin | ✅ nginx active after the reload (R1-37) | GW ✅ (R1-21) | — |
| U-18 | Systemd start/stop/restart | Admin | ✅ `systemctl is-active` read back (R1-37) | GW ✅ (R1-21) | INV-43 |
| U-19 | SSL certificate renew | Admin | ✅ certificate valid for at least 29 more days (certbot's 30-day renewal window, one day of slack) (R1-37) | GW ✅ (R1-21) | INV-43 |
| U-20 | Servers add/update/delete (+ vault secrets: kubeconfig, cloud API key; SSH keys) | Admin | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ (versioned diff, R1-22) | INV-41 (TA-42 SSH user/host/port validated since R1-42; kubeconfig exec/auth-provider/file refused, E-09), 52 |
| U-21 | Server onboarding (root installs) | Admin | ⚠️ result object; `NotVerifiable` + reason (the next health check shows the host, R1-37) | GW ✅ (`server.onboard`; the switchover carries the server diff, R1-22) | — |
| U-22 | Servers pause/resume checks (one server and the fleet-wide pause: `Servers.razor:390,410,442` run `checks.pause` / `checks.resume` through `IUiMutationRunner`; `ILoopSuspensionService.Suspend`/`Resume` run only inside that decision — `MutationGatewayCoverageTests`) | Operator (gateway, catalog level `write`, decided at the click; the page's button visibility is not the control) | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ (no page row since R1-22) | INV-17 (pinned on the real handlers: `ServersPauseResumeRoleTests` — a Viewer, also an Operator demoted while the page is open, is refused for one server and the fleet, each attempt recorded as Denied, `ILoopSuspensionService` is never called; an Operator's pause/resume and fleet-wide pause run after their audit intent) |
| U-23 | Cloud power / snapshot | Admin | ✅ power on / shut down read back, polled; reboot, reset, snapshot `NotVerifiable` + reason (R1-37) | GW ✅ (R1-21) | INV-43, 52 |
| U-24 | Volume backup / restore / delete | Operator / Admin / Admin | ✅ backup and delete read back; restore `NotVerifiable` + reason (R1-37) | GW ✅ (R1-21) | INV-43 |
| U-25 | Self-backup create / delete / restore / download | Admin | ✅ archive validation (R1-32); create and delete read back, restore staged `NotVerifiable` + reason (R1-37) | GW ✅ (download: audited before the first byte, refused without its row — R1-22) | INV-55 |
| U-26 | Scheduled tasks create / toggle / delete | create + delete: catalog level (Operator), then the gateway's effective level per task type (R1-05); toggle / take over: Admin | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ (R1-21) | INV-04 |
| U-27 | Scheduled task "Run now" | Operator; custom command, self-backup, DB backup and retention **Admin** (WP2; gateway resolves the same per task since R1-05) | `NotVerifiable` + reason for the outer call: "run now" answers with the run summary (R1-37) | GW ✅ (R1-21) | INV-43 |
| U-28 | Webhooks create / take over / regenerate / toggle / delete / test | Admin; test = Operator | `NotVerifiable` + reason (Whiskers' own record, R1-37) | ✅ gateway (`webhook.*` on the stored webhook; create records the owner; test decided as the person, then runs as the owner — R1-09); since R1-22 the one gateway event per action, with a versioned diff, no page row | INV-04, 19 (test fires the real action at Operator) |
| U-29 | Git-deploy app save / delete; deploy | Admin; deploy **Admin** (R1-05, root-equivalent; was Operator) | `NotVerifiable` + reason (the repository decides what is built, R1-37) | GW ✅ (R1-21) | INV-41 (branch validated since R1-42), 46, 62 (contract 0006) |
| U-30 | Log-alert rules (Log search, AI triggers page) | Operator / Admin | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ (`logalert.*`; create/toggle/delete carry a versioned diff, the pattern as text only — R1-22) | INV-27 |
| U-31 | AI triggers save (prompt, preset, write grant) | Admin | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ (`aitrigger.save`; versioned diff, the prompt as text only — R1-22) | INV-23, 27, 28, 46 |
| U-32 | Guardrail policy edit / preset switch / kill switch (`guardrails.save`, `guardrails.kill_switch_off` since R1-11; the kill switch refuses every save but switching it off) | Admin | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ | INV-28 |
| U-33 | Approvals approve / reject | approve **and** reject (same rules since R1-15, INV-37): level ≥ the operation's **effective** level (the tool level, raised per call by R1-05 — e.g. Admin for a scheduled custom command or DB backup); a requester never decides their own non-interactive request, and decides an interactive one only while `RequireSecondApprover` is off (PC-D4); not after expiry | — | ✅ (R1-15: `ApprovalSeparationOfDutyTests`, `ApprovalStoreTests`) | — (INV-31 closed by R1-14; INV-32, 33, 34, 37 by R1-15) |
| U-34 | Settings: keys, tool levels, roles, whitelist, vault, registries, terminal, metric retention, quiet hours (`config.*`, `mcp.key_*`, `vault.secret_*`, `registry.*` since R1-11; metric retention `config.save` since R1-55, values from the environment read-only; quiet-hours rules `config.save` since R1-56, an unknown or non-IANA time zone refused before anything is written) | Admin | `NotVerifiable` + reason (Whiskers' own record, R1-37) | GW ✅ (versioned, redacted diff; no page row since R1-22) | INV-15, 16 |
| U-35 | Local accounts (R1-49): own password change on *My account* (`account.password_change`, read level, own account only, current password required); add / reset / delete under *Settings → Local accounts* (`account.create`, `account.password_reset`, `account.delete`) | own account / Admin | `NotVerifiable` + reason (Whiskers' own account store, R1-37) | GW ✅ (the event names the account and, for an added one, the role — never a password or recovery code; the service re-checks the session and role itself) | INV-09, 61 |
| U-36 | Sign-in identities (R1-27): confirm a pending request / remove a binding or pending request under *Settings → Sign-in identities* (`identity.binding_confirm`, `identity.binding_remove`) | Admin | `NotVerifiable` + reason (Whiskers' own record) | GW ✅ (target kind `identity`, the id as the only argument; refuses `last_admin` — the last way the last Admin can sign in, checked one removal at a time together with the local-account deletion — and `auth_disabled`) | INV-05, 61 |
| U-37 | Per-host mTLS client credentials on *Servers* (R1-44, contract 0007; row added by R1-51): migrate a host to its own credential (`mtls.client_migrate`), retire the shared credential (`mtls.shared_client_retire`, refused while any considered host is not proven narrowed live) | Admin | `NotVerifiable` + reason (migration: every host step confirmed inside the rollout; retirement: Whiskers' own record) | GW ✅ (R1-44) | INV-58 |
| U-38 | Endpoint checks (R1-57; row added by R1-51): save / delete a check, run it now (`endpointcheck.save`, `endpointcheck.delete`, `endpointcheck.run`; a run is fixed probe commands on the check's server) | Admin | `NotVerifiable` + reason (Whiskers' own record; a probe changes nothing) | GW ✅ | — |
| U-39 | In-app notifications (user decision 2026-11-07, INV-18): mark all read and clear all on the *Notifications* page and in the bell (`notifications.mark_read`, `notifications.clear`; the log is shared by everyone). The buttons are shown from Operator on and the bell marks read on open only for an Operator and only when something is unread — presentation; the gateway decides at the click. A refusal of the bell's automatic mark is recorded but not shown. Like every write, both are stopped by the read-only kill switch | Operator (gateway, catalog level `write`) | `NotVerifiable` + reason (Whiskers' own record) | GW ✅ | INV-17, 18 (`NotificationsRoleTests`) |
| U-40 | Manual CVE scan on the *CVEs* page and in *Settings* (user decision 2026-11-07, INV-18): `cve.scan`, one cycle over every enabled host (apt index, Trivy), decided at the click and lasting the whole cycle; the monitor's own unattended cycle stays S-06. A click while a cycle is already running does nothing and is recorded as a reported failure, with a page message saying so. The read-only kill switch stops it. Stopping the host during a manual scan waits the shutdown drain's budget, then records the scan as interrupted, like other long operations. "Started" is shown only once the cycle holds the scan gate; with the CVE module off nothing is scanned and the click is recorded as not run. The findings' notifications start AI triggers outside the person's decision (the run does not inherit the open `cve.scan` operation) but on the event's own flow: lane and correlation id as before, so a trigger fired from a background loop stays in the background lane and a server paused with `checks.pause` (U-22) refuses its calls; one fired from the manual scan runs in the lane the scan ran in | Operator (gateway, catalog level `write`) | `NotVerifiable` + reason (findings are Whiskers' own record) | GW ✅ | INV-17, 18 (`CveScanRoleTests`, `SettingsHandlerRoleTests`, `TriggerPolicyPinningTests.A_trigger_started_from_a_manual_scan_has_no_parent_operation`, `TriggerPolicyPinningTests.A_trigger_fired_by_a_background_loops_event_runs_in_the_background_lane_and_stops_at_a_paused_server`) |

Reading the trail is not a mutation and not counted above, but it is governed (R1-23, INV-50): the Audit log and Agent
History pages and the audit rows of the Self status timeline are Admin only; `GET /audit/export` is Admin only, records
`audit.export` before it reads anything (503 without that row) and records a signed-in user's refusal.

### 7.6 Totals

*Correction:* the inventory summary in `audit/B-mutations.md` and `TECHNICAL_AUDIT.md` §6 said 87 paths; its rows add up
to 41 + 4 + 8 + 3 + 34 = **90**. Both documents are corrected in this PR.

The 90 paths of the WP11a baseline (2026-10-26):

| Class | Paths | Paths with path-specific gaps |
|---|---|---|
| MCP tools | 41 | 40 |
| Agent | 4 | 4 |
| Background / webhook / system | 8 | 7 |
| Terminals | 3 | 3 |
| UI handler groups | 34 | 34 |
| **Total** | **90** | **88** (M-24 and S-08 have no path-specific gap) |

All 90 paths were blocked by the shared gaps (INV-01, 22, 38, 39, 45, 48, 49; INV-49 closed by R1-23, INV-45 by the WP5 path matrix). WP2 (gateway) closes most of
them structurally. The path-specific gaps then close in WP3–WP7.

**Rows today (R1-51, counted from the tables above).** Later packages added rows: M-42…M-47 (contract 0001, R1-48),
S-09…S-15 (R1-34, R1-48, R1-49, R1-27), U-35 and U-36 (R1-49, R1-27). The release gate found five paths that had
no row and R1-51 added them: M-48 and M-49 (R1-44), S-16 and U-38 (R1-57), U-37 (R1-44). U-39 and U-40 came with the INV-18 user decision of 2026-11-07.

| Class | Rows |
|---|---|
| MCP tools (M) | 49 |
| Agent (A) | 4 |
| Background / webhook / system (S) | 16 |
| Terminals (T) | 3 |
| UI handler groups (U) | 40 |
| **Total** | **112** |

The gap column per row is not recounted here, and the release gate (contract 0008) does not count gaps either: it
counts the rows per class and fails when the table above disagrees (R15); the 90-row baseline stays as history.

## 8. How this document is maintained

- A new mutating MCP tool, UI handler or background job gets a row in §7 in the same PR, and a row that is not an M
  row names its capabilities in [`PATH_CAPABILITIES.md`](PATH_CAPABILITIES.md).
- When a WP closes a gap, the PR removes the invariant from the row's gap column and updates the invariant's
  status in `SECURITY_INVARIANTS.md` (and its line in [`RELEASE_GATE.md`](RELEASE_GATE.md)).
- §7 stays hand-written: its chain cells are judgments the capability table does not hold (contract 0008, D7). What is
  generated is [`CAPABILITY_INVENTORY.md`](CAPABILITY_INVENTORY.md), from the capability table, and the release gate
  (`ReleaseGateTests`) joins the two in CI: every capability is named by a §7 row or composed by another (R13), every
  tool and capability a row names exists (R14), and §7.6 matches the rows (R15). So the document cannot drift from the
  code unnoticed.
