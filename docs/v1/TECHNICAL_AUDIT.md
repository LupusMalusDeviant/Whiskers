# Whiskers v1.0 — Technical Audit (Phase 0)

| | |
|---|---|
| Baseline | `main` @ `498e70d` (release commit for 0.15.0) |
| Date | 2026-10-26 |
| Scope | Whole repository: application, data layer, migrations, MCP server, agent, deployment artefacts, CI/CD, tests |
| Mode | Read-only. **Nothing was fixed during this audit.** |
| Detail reports | [`audit/A-auth.md`](audit/A-auth.md) · [`audit/B-mutations.md`](audit/B-mutations.md) · [`audit/C-agent-approvals-audit.md`](audit/C-agent-approvals-audit.md) · [`audit/D-secrets-data-backup.md`](audit/D-secrets-data-backup.md) · [`audit/E-exec-injection.md`](audit/E-exec-injection.md) · [`audit/F-ops-supplychain-tests.md`](audit/F-ops-supplychain-tests.md) |

## 1. Method

The repository (≈500 source files, 150 test files) was split into six review areas:

- **A:** authentication, authorization, the HTTP/SignalR surface and sessions.
- **B:** the mutation-surface inventory.
- **C:** agent, guardrails, approvals and audit.
- **D:** secrets, persistence, migrations, backup and restore.
- **E:** command execution, injection and remote transports.
- **F:** operations, supply chain, CI/CD, tests and background services.

Each area was reviewed against the code with `file:line` evidence. The detail reports hold the evidence, attack paths and the controls that hold up.

Several areas found the same issue independently. Those duplicates are merged below into one consolidated finding (`TA-xx`) that lists its source IDs.

**Verification status.** Findings marked **✔** were independently re-checked against the code while consolidating (TA-01, TA-02, TA-03, TA-04, TA-05, TA-10). The rest rest on the evidence in the area reports. Items the area reports flag as "needs verification" keep that flag.

**Threat-model premise.** Whiskers is a root-level control plane by design: the host-control plane runs commands as host root, and Docker create rights are root-equivalent. The question is therefore not *whether* privileged execution is possible. It is whether every path to it is authenticated, authorized at the right level, policy-checked, approval-gated where required, and audited.

## 2. Summary

| Severity | Count (consolidated) | v1 blockers |
|---|---|---|
| Critical | 1 | 1 |
| High | 17 | 17 |
| Medium | 35 | 3 |
| Low | 26 | 0 |

The area reports contain 111 findings: A 18, B 20, C 24, D 17, E 9, F 23. After merging duplicates they come to the 79 above.

**Overall assessment.** The cryptographic and transport foundations are sound:
- Vault: AES-256-GCM with PBKDF2 at 600k iterations.
- mTLS: chain and hostname validation, fail-closed.
- Hashed MCP keys.
- HMAC on webhooks.
- A fail-closed default authorization policy.
- No unsafe deserialization.
- Single-argv command passing.

**The product contract does not hold yet.** The contract is: *no infrastructure mutation through the agent/MCP control plane bypasses authentication, authorization, policy, approval and audit*. It fails in three ways:

1. **Policy and audit are attached to the MCP/agent path, not to the mutation itself.**
   - UI handlers, scheduler, AutoUpdate, AutoPatch, webhooks and terminals mutate infrastructure without the guardrail engine.
   - Several of them also write no audit record.
2. **Privilege levels are inconsistent between surfaces.** Examples:
   - UI deploy needs Operator, while the equivalent MCP tool needs Admin.
   - Several MCP Write tools are Admin-only in the UI.
   - The tool "Enabled" switch is honoured for API keys only.
3. **Approvals and policy decisions are not durable security objects.**
   - Approvals live in memory.
   - Approver decisions are not audited.
   - There is no persisted policy decision record.
   - There is no tamper evidence.

## 3. v1 blockers (Critical + High)

| ID | ✔ | Severity | Category | Finding | Sources | Evidence (src/Whiskers/…) |
|---|---|---|---|---|---|---|
| **TA-01** | ✔ | **Critical** | Security | **Operator → host root via UI deploy.** The MCP tools `deploy_app`/`deploy_compose` require Admin because a compose file can mount `/` or run privileged. The UI equivalents need only Operator. AppStore pastes template variables into the YAML verbatim. | B-01 (E-05) | `Components/Pages/Deploy.razor:228,287`, `ComposeEditor.razor:251`, `AppStore.razor:500,529-536,615`; `Models/McpPermission.cs:110-111` |
| **TA-02** | ✔ | High | Security | **First-run setup can be claimed by any visitor.** `/setup` is anonymous and only checks "no Admin role exists". The installer (`--bind 0.0.0.0`) and the Helm ingress expose it. Removing the last Admin re-opens the wizard after a restart. | A-01, A-02, F-02 | `Services/Setup/SetupStateService.cs:38-66`, `Components/Pages/Settings.razor:1155-1159`, `deploy/install.sh:37` |
| **TA-03** | ✔ | High | Security | **No brute-force protection on local login.** `CheckPasswordAsync` bypasses Identity lockout, there is no rate limiter, and timing differs between unknown users and wrong passwords. | A-03 | `Startup/WhiskersPipelineExtensions.cs:220`, `Startup/WhiskersAuthenticationExtensions.cs:167` |
| **TA-04** | ✔ | High | Security | **Unattended automation bypasses the guardrails, the read-only kill switch, approvals and the AuditLog.** Affected: scheduler cron (custom root commands, prune), AutoUpdate, AutoPatch, and webhook/git deploys. Cron runs never re-check the creator's authority. | B-02, B-10 | `Services/Scheduler/SchedulerService.cs:110-160`, `TaskExecutor.cs:62-182`, `AutoUpdate/AutoUpdateService.cs:141`, `AutoPatch/AutoPatchService.cs:83`, `Webhooks/WebhookService.cs:121-195` |
| **TA-05** | ✔ | High | Security / Operations | **Interactive root shells are unaudited and unguarded.** The host shell (nsenter/SSH root) and container terminals (root SSH, then `docker exec`) record no session open/close and no transcript. | B-03 | `Components/Pages/ServerTerminalView.razor:48-78`, `TerminalView.razor:51-63`, `ContainerDetail.razor:1109`, `Services/Terminal/TerminalSessionManager.cs:44-74` |
| **TA-06** | | High | Commercial readiness / Security | **The audit trail is incomplete.** (a) MCP mutations write only Agent History, where "success" means the HTTP status: `execute_query`, all `cloud_*`, Hetzner backups/snapshots/type, networks, log alerts, loop suspension, AutoPatch. (b) Policy-relevant UI changes are not audited: UFW disable, nginx reload, onboarding, DB query/migrate/seed, GitDeploy, log alerts, AI triggers, guardrail edits. (c) Approval decisions and approver identity are recorded nowhere. Approvals are in memory only and capped at 200. | B-04, B-07, C-04, A-10, B-19, C-13 | `Mcp/Tools/DatabaseTools.cs:114`, `CloudTools.cs:78-110`, `HetznerTools.cs:47-150`, `Services/Agent/Approvals/ApprovalStore.cs:41,182-191`, `Whiskers.Data/Entities/McpToolCall.cs` |
| **TA-07** | | High | Security | **AI triggers ignore their pinned guardrail preset.** Sessions read the global live store, so an autonomous run gets whatever preset is currently active. | B-05, C-01 | `Services/Agent/AgentService.cs:76`, `AgentSession.cs:194`, `Triggers/AiTriggerDispatcher.cs:101-111` |
| **TA-08** | | High | Security | **Prompt injection into autonomous runs.** A matched third-party log line is embedded verbatim in the trigger task. Unattended triggers default to Write level. | B-06, C-03 | `Services/LogMonitor/LogMonitorService.cs:206`, `Triggers/AiTriggerDispatcher.cs:165`, `Models/AiTrigger.cs:27` |
| **TA-09** | | High | Security | **ProtectedResources is trivially bypassed.** It does a whole-value glob match only. Container IDs and unique prefixes, or names embedded in commands (`execute_command "docker stop whiskers"`), pass. | C-02 | `Services/Agent/Guardrails/BuiltInGuardrailRules.cs:114-138`, `Mcp/Tools/McpInputValidation.cs:25-31` |
| **TA-10** | ✔ | High | Security | **The tool "Enabled" switch and stricter stored levels apply to API keys only.** Web users and the in-process agent use the code defaults. Disabling `execute_command` in Settings does not stop the agent or its triggers. | C-05 | `Mcp/McpPermissionCheck.cs:28-35`, `Services/Mcp/McpPermissionService.cs:163-185`, `Services/Agent/AgentToolRegistry.cs:60` |
| **TA-11** | | High | Security | **`AUTH_DISABLED` makes every network client an Admin**, including anonymous `/mcp` `execute_command`. There is no loopback restriction, and DNS rebinding works via `AllowedHosts:"*"`. The synthetic Admin principal is also trusted when auth is on, so a stolen DataProtection key ring lets an attacker forge an Admin cookie. | B-08, C-06, A-08, A-04 | `Startup/WhiskersPipelineExtensions.cs:136-148,285`, `Mcp/McpPermissionCheck.cs:60`, `Services/Auth/CurrentUserService.cs:35`, `deploy/install.sh:190` |
| **TA-12** | | High | Security | **Most high-value secrets are stored as plaintext JSON outside the vault:** Hetzner/Hostinger API keys, DNS token, SMTP password, chat tokens and webhook URLs, outbound webhook secret, AI provider keys. The production-readiness check wrongly reports them as encrypted. | D-01 | `Models/ServerConfig.cs:87`, `Services/Persistence/AppSettingsStore.cs:37-41`, `Services/Agent/AgentSettingsStore.cs:12,40`, `Services/Setup/ProductionReadinessService.cs:50-55` |
| **TA-13** | ✔ | High | Security / Reliability | **Restore accepts arbitrary archive entries.** A crafted archive can wipe every self-backup, including the pre-restore safety copy. Unencrypted archives are accepted even when `VAULT_KEY` is set. A staged DB has no integrity check, so a bad one crash-loops the app. **Fixed in R1-32:** one list of restore targets (`SelfBackupLayout`) with forbidden, duplicate and conflicting entries refused and size/count bounds; plaintext refused with `VAULT_KEY`; staged database, JSON and key files checked before the commit; the swap never touches `backups/`, makes each target equal to the archive and restricts key permissions; encrypted archives are published by rename. | D-02, D-14 | `Services/Backup/BackupArchiver.cs:90-111`, `RestoreBootHandler.cs:70-111`, `BackupService.cs:296,358-369` |
| **TA-14** | ✔ | High → corrected to Medium | Security (supply chain) | **The code default for the Trivy image is the unpinned `aquasec/trivy:latest`, and Trivy runs with `docker.sock` mounted on every managed host.** *Correction while consolidating:* `appsettings.json` already pins `0.72.0@sha256:…`, so only the code fallback and operator-entered values are unpinned. | F-01 | `Configuration/CveMonitorSettings.cs:23`, `Services/Cve/TrivyScanner.cs:79-85` |
| **TA-15** | | High | Security (supply chain) | **GitHub Actions are pinned by mutable tags, not SHAs**, including the release job that holds `packages: write` and the OIDC signing identity. | F-03 | `.github/workflows/release.yml:16-19,30-140`, `ci.yml`, `chart-ci.yml` |
| **TA-16** | | High | Operations (supply chain) | **Release pipeline integrity.** A `workflow_dispatch` run builds the dispatched ref, not the tag, yet pushes `X.Y.Z`/`X.Y` and the Helm chart, signed. Tag releases are not gated on CI. | F-04 | `.github/workflows/release.yml:10-14,29-30,91-93,156-182` |
| **TA-17** | | High | Reliability (DR) | **On PostgreSQL the self-backup contains no relational data**, so a restore splits config from the DB. Backups also live on the same volume as the data. | F-05, D-06, D-05 | `Services/Backup/BackupService.cs:76-96,122-153`, `Configuration/DataPathOptions.cs:101` |
| **TA-18** | | High | Security / Operations | **Approval integrity gaps:** an expired approval can still be approved. The requester can approve their own request, and Viewers can reject any. A scheduled custom-command approval carries Write level, so an Operator can approve a root command. A session whose chat was closed executes a later approval unattended. | C-07, C-08, C-10, B-11, A-09 | `Services/Agent/Approvals/ApprovalStore.cs:113-129`, `Services/Agent/Guardrails/DirectMcpCallGuard.cs:71`, `Components/Pages/AgentView.razor:377` |

> TA-18 combines four Medium findings into one High blocker: together they mean approvals are not yet security objects (Phase 3 of the program).

## 4. Medium findings

| ID | Category | Finding | Sources |
|---|---|---|---|
| TA-19 | Security | No session revocation: sliding 24h persistent cookies, no security stamp; Blazor circuits and terminals never re-check role or whitelist | A-05, B-17 |
| TA-20 | Security | Identity is keyed on the email claim only; OIDC email claim is configurable and `email_verified` is unchecked | A-06 |
| TA-21 | Security | Webhooks: no replay protection (no timestamp/delivery ID), ID-enumeration oracle, no rate limit, long work inside the HTTP request, no per-target serialization | A-07, B-14, F-06, E-07 |
| TA-22 | Security | Viewer can delete all notifications, start fleet-wide CVE scans, reject any approval | A-09 |
| TA-23 | Security | Fail-open whitelist before setup: any Google/OIDC account gets Viewer | A-12 |
| TA-24 | Commercial readiness | No local user management (password change/reset, extra users, MFA) | A-11 |
| TA-25 | Testing | No integration tests asserting the auth requirement of every endpoint | A-18 |
| TA-26 | Security | UI actions never consult the guardrail engine (kill switch / protected resources apply to AI+MCP only) — needs an explicit product decision | B-09 |
| TA-27 | Security | Direct MCP calls get no guardrails when the Agent module is disabled | B-12, C-14 |
| TA-28 | Security | MCP Write is broader than UI Operator (firewall, nginx, systemd, cloud reset, Hetzner destructive ops). **Fixed in R1-05:** `CapabilityCatalog` is the one level table and `DefaultToolLevels` takes its mutating entries from it; the firewall, nginx, systemd, network, cloud and Hetzner tools are Admin on MCP as in the UI; `gitdeploy.deploy` is Admin on the page and in the gateway; scheduler create/run carry the task's effective level into the caller check and the approval (B-11a). **Remaining:** the webhook path into Git deploy (S-04/S-05, R1-08/09) and the UI actions not yet through the gateway (U-06, U-08, U-14, U-20, U-21, U-25, U-34). | B-13 |
| TA-29 | Reliability / Commercial readiness | `AutoRollback` setting is persisted (default ON) but never executed — feature claim ≠ behaviour (**v1 blocker**). **Fixed in R1-36:** a failed automatic update is rolled back through the gateway and the rollback is verified; with rollback on, a failed snapshot stops the update. | D-10, B-15 |
| TA-30 | Reliability | Post-execution verification largely absent (start/stop/update/deploy/firewall/cloud). **Container part fixed in R1-36:** start/stop/restart/remove/update/create/rollback are read back, a failed read-back is a failed outcome; the other capabilities are R1-37. | B-16 |
| TA-31 | Operations | Actor identity lossy/inconsistent across stores; all triggers log as one "ai-trigger" identity | C-12 |
| TA-32 | Security | Guardrail page: unsaved edits alias the live policy after the first save | C-09 |
| TA-33 | Security | ForbiddenArgPatterns is a bypassable denylist and fails open on regex error/timeout | C-15 |
| TA-34 | Security | A key's `AllowedTools` list bypasses its level check | C-16 |
| TA-35 | Commercial readiness | Audit data: no tamper evidence, fixed 90-day hard delete, no export, visible to all Viewers | C-17, B-20 |
| TA-36 | Security | A wrong/changed `VAULT_KEY` is not detected; legacy CBC migration with a wrong key can overwrite entries | D-03 |
| TA-37 | Security | DataProtection key ring unencrypted at rest and included in backups (cookie-forgery credential). **Fixed in R1-31:** each key's secret is encrypted with a vault-derived key (`VaultKeyRing`), plaintext key files are encrypted in place at start; without `VAULT_KEY` the keys stay as before (reported). | D-04, A-04 |
| TA-38 | Reliability | No pre-migration backup or downgrade path; a failed migration crash-loops; Helm startup probe ≈60 s | D-07, F-12 |
| TA-39 | Reliability | JSON stores: corruption blocks boot, no fsync before rename, some non-atomic writers. **Fixed in R1-35:** `DurableFile` (flushed own temp file, one rename) for the shared store and the CVE, image-update, agent/app-settings and chat writers; the previous generation is kept as `.bak` and used when the file is unreadable (original kept as `.corrupt-<time>`, shown on the readiness card); without a readable generation the start stops instead of defaulting; guardrails stay fail-closed without recovery. | D-09 |
| TA-40 | Security | Rollback snapshots store full container env (secrets) in plaintext and replay it. **Fixed in R1-31:** snapshots are stored encrypted with a vault-derived key bound to container and server; without a usable vault none is kept. | D-11 |
| TA-41 | Security | git branch not validated → option/argument injection; deploying an untrusted repo = code execution on host (by design, Admin) | E-01 |
| TA-42 | Security | SSH user/host not charset-validated → `ssh` option injection (`-oProxyCommand`) on the Whiskers host (Admin-set) | E-02 |
| TA-43 | Security | `execute_query` is Write level but runs arbitrary SQL as superuser (DROP, Postgres `COPY … TO PROGRAM`) — level decision needed | E-04 |
| TA-44 | Security | Single shared mTLS client key and fixed `--allow-cn` across the whole fleet | E-06 |
| TA-45 | Reliability | Unattended AI-trigger runs have no deadline; LLM streams have no body timeout; slots can leak | F-07 |
| TA-46 | Reliability | Notification retry mostly dead code; SMTP has no effective timeout; no durable outbox | F-08 |
| TA-47 | Security / Testing | CI: no SAST/CodeQL, dependency-vuln or secret scanning, no image build/scan on PRs, no job timeouts | F-09 |
| TA-48 | Security | Runtime image: base images not digest-pinned; ships full `docker.io` engine + sshpass; root by default | F-10 |
| TA-49 | Operations / UX | Installer defaults: `:latest`, privileged profile, no `VAULT_KEY`, no signature check, stale guidance recommending `Auth__Disabled` (**v1 blocker for the text**) | F-11 |
| TA-50 | Testing | No migration-drift test; Postgres tests skip silently in CI; 3 UI tests; no E2E / image / upgrade / hardened-boot tests | F-13, D-08, D-16, C-23 |
| TA-51 | Security | Hardened profile: socket proxy with `POST=1` on unauthenticated host loopback `127.0.0.1:2375` | F-14 |
| TA-52 | Reliability | Background loops: non-shutdown `OperationCanceledException` escapes; host stops on background exceptions; unbounded intervals | F-15 |
| TA-53 | Documentation | No upgrade/rollback guide, troubleshooting, production checklist, Postgres backup guidance (**v1 blocker: upgrade + backup docs**) | F-18, D-17 |

## 5. Low findings (summary)

A-13 missing security headers / HSTS · A-14 cookie hardening at defaults · A-15 forwarded headers trust all RFC1918 ·
A-16 minor HTTP robustness · A-17 no per-server/tenant scoping (by design today) · B-18 `AgentToolInvoker` enforces only
Deny (Confirm only in `AgentSession`) · C-11 approval level source differs from the gate · C-18 resolver keyed by
non-unique provider ToolCallId · C-19 per-session rate limits only, unbounded pending approvals · C-20 ClaudeCodeRuntime
fail-open rights check / fixed-key deputy · C-21 transcripts not fully redacted · C-22 approval notifications lack
context · C-24 `SaveMcpPermissions` has no in-method Admin check · D-12 SecretRedactor coverage, not applied at logging
layer · D-13 `VAULT_KEY` env-only, no `_FILE`, no strength check · D-15 SQLite→Postgres migrator not transactional ·
E-03 nginx site-name regex allows `..`/leading `-` · E-08 VPN args concatenated into one `Arguments` string ·
E-09 kubeconfig `exec` plugins may run on host (needs verification) · F-16 shutdown semantics of mutating background
work · F-17 no structured logs / correlation IDs / tracing · F-19 Dependabot misses 3 of 5 projects and compose/Helm
images, no lock files, no SDK pin · F-20 Helm tailscale `:latest`, emptyDir state · F-21 Tailscale auth key in argv
under `pid: host` · F-22 legacy AiChat client without cancellation · F-23 release Trivy gate scans amd64/CRITICAL only.

## 6. Mutation surface (summary)

The B report inventories **90 mutation paths** (corrected from 87 in WP11a; the rows add up to 90): 41 MCP tools, 4 agent paths, 8 background/webhook/system paths, 3 terminals and 34 groups of UI handlers (≈70 handlers). It records each one as *Actor → AuthN → AuthZ → Guardrail → Approval → Execution → Verification → Audit*.

| Path class | AuthN | AuthZ | Guardrail | Approval | Verification | Audit |
|---|---|---|---|---|---|---|
| In-process agent (UI / `instruct_agent`) | ✅ | ✅ ceiling | ✅ | ✅ (in memory) | ❌ mostly | Agent History only |
| Direct MCP tool call (agent module on) | ✅ | ✅ key scope | ✅ | ✅ approve-then-retry | ❌ mostly | Agent History; AuditLog partial |
| AI triggers (autonomous) | n/a (system) | ✅ trigger level | ⚠️ wrong preset (TA-07) | ⚠️ | ❌ | Agent History |
| Web UI handlers | ✅ | ✅ role (⚠️ inconsistent, TA-01/TA-28) | ❌ | ❌ | ❌ mostly | ⚠️ partial (TA-06) |
| Scheduler / AutoUpdate / AutoPatch | n/a (system) | ❌ no re-check | ❌ | ❌ | AutoPatch only | ❌ side tables only |
| Webhooks / git deploy | HMAC | — | ❌ | ❌ | ❌ | ❌ webhook log only |
| Terminals (host / container) | ✅ | ✅ Admin / Operator | ❌ | ❌ | n/a | ❌ |

Paths that mutate without the full chain are listed in [`audit/B-mutations.md` §4](audit/B-mutations.md). This inventory is the input for `PRODUCT_CONTRACT.md` (Phase 1).

## 7. Architecture concerns

1. **Enforcement lives at the entry point, not at the mutation.**
   - Guardrails sit in `AgentToolInvoker` and `McpGuardrailFilter`, and audit calls are scattered across UI pages, tools and services.
   - Every new entry point (UI, scheduler, webhook, terminal) must re-implement the chain, and today most do not.
   - A single *mutation gateway* is needed. Every infrastructure mutation would pass through one service that owns authorization, policy, approval binding, execution, verification and audit. That is the structural fix behind TA-01, 04, 05, 06, 10, 26, 28.
2. **Two permission models.**
   - UI roles (`AppRole`) and MCP levels (`DefaultToolLevels` + stored overrides) are mapped ad hoc and disagree (TA-01, TA-10, TA-28).
   - v1 needs one capability table per action, consulted by every surface.
3. **Approvals are UI workflow state.**
   - They live in memory, are keyed by session and provider ToolCallId, are not persisted and have no decision record.
   - Phase 3 requires persisted, hash-bound approval objects.
4. **Identity is an email string.**
   - Human, credential, agent and session identities collapse into `Actor` strings (`local@serverwatch.local`, `ai-trigger`, `key:<name>`).
   - Phase 6 needs a structured principal that propagates into policy decisions, approvals and audit.
5. **State is split across JSON stores, SQLite/Postgres and memory.**
   - Security-relevant state lives outside the DB: roles, whitelist, guardrails, MCP permissions, settings (incl. secrets), in-memory approvals.
   - This complicates backup consistency (TA-17), atomicity (TA-39) and auditability.
6. **The single-replica appliance is a stated constraint, not a defect.**
   - It is correct for 1.0, but must be documented with its availability and RPO/RTO consequences.

## 8. Positive controls (verified in the area reports)

- **Authentication and authorization:** fail-closed fallback authorization policy with default `[Authorize]` on pages, and role checks inside page handlers rather than markup only.
- **MCP:**
  - Keys are SHA-256 hashed and compared in constant time.
  - Per-call scope checks run on all 86 tools (checked by script).
  - Direct MCP calls are guardrailed with argument-hash-bound, single-use approvals.
- **Agent:** the principal ceiling blocks agent escalation, and an approved agent call executes the exact stored call object.
- **Vault and backup cryptography:** AES-256-GCM, PBKDF2 at 600k iterations, correct nonce handling and frame AAD. Backup restore blocks zip-slip and symlinks, and its swap is crash-safe.
- **Transport:** mTLS server validation checks the chain and the hostname and fails closed. SSH uses a shared TOFU host-key policy (ADR-0002).
- **Command execution:** commands pass as a single argv element, the process tree is killed on timeout, and output is bounded.
- **Inputs and endpoints:**
  - Git URLs are restricted to `https://`.
  - Webhooks require HMAC and compare it in constant time.
  - `/metrics` is gated by a token.
  - Backup download is Admin-only with path containment.
- **Supply chain:** release images are Trivy-gated, SBOM-attested and keyless cosign-signed.
- **Tests:** CI fails if no tests run, and boots the app to check dependency injection.
