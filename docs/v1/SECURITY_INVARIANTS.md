# Whiskers v1.0 — Security Invariants

| | |
|---|---|
| Status | **Accepted** (WP11a, 2026-10-26) |
| Baseline | `main` @ `498e70d` (0.15.0) + PR #41 (WP1) |
| Date | 2026-10-26 |
| Companion | [`PRODUCT_CONTRACT.md`](PRODUCT_CONTRACT.md) (what the product promises), [`TECHNICAL_AUDIT.md`](TECHNICAL_AUDIT.md) (evidence) |

An invariant is a property that must hold on **every** path, for **every** principal, at **every** point in time.
Each one has a stable ID, a statement that a test can decide, its current status, and the test that will pin it.
The [`PRODUCT_CONTRACT.md`](PRODUCT_CONTRACT.md) path matrix refers to these IDs.

**Release rule.** v1.0 ships only when every invariant below is ✅ and its named test exists and passes. A
deliberate exception needs a recorded decision in [`PRODUCT_CONTRACT.md` §6](PRODUCT_CONTRACT.md#6-decisions)
and a matching limitation in the product documentation. `RELEASE_GATE.md` (WP11b) will check this table
mechanically, so keep its format: one row per invariant, the ID in the first column, the status symbol at the start
of the third.

**Status legend.** ✅ holds on every path and is pinned by a test · ⚠️ holds on some paths, or holds but is untested ·
❌ violated · ❓ not verified yet.

**Test column.** An existing test is named as `Class.Method`. A planned test is named as `Class` *(planned, WPx)*.
"Wire test" means a test through `WebApplicationFactory<Program>` in `[Collection("WebAppBoot")]`.

## 1. Actors and identity

| ID | Invariant | Status | Evidence / gap | Test | WP |
|---|---|---|---|---|---|
| INV-01 | Every mutation is attributed to exactly one structured principal `{kind ∈ Human, ApiKey, Agent, Automation, Webhook, System; stable id; credential id; on-behalf-of principal; session id}`. Display strings are derived from it, never the other way round. | ⚠️ | R1-03: gateway principals carry typed ids (`user:` / `key:<id>` / `trigger:<id>` / `agent:<session>`), credential, session and on-behalf-of, built by `MutationPrincipals` from typed fields only; owner ids follow the scheme, legacy bare emails are migrated explicitly and anything ambiguous stays suspended; the MCP call log and key rows of the audit log use the key id. Open: humans are identified by email, not a stable subject (INV-05); webhook and system principals come with their packages. TA-31, C-12. | `PrincipalPropagationTests` | R1-03 |
| INV-02 | An agent run never holds more rights than the principal that started it, including runs started through `instruct_agent` and AI triggers. | ⚠️ | `PrincipalCeilingRule` holds. The trigger ceiling is the trigger's `MaxLevel` with no human owner behind it (see INV-04). | `GuardrailEngineTests.Write_tool_by_read_principal_is_denied`, `GuardrailEngineTests.Tool_outside_principal_allowlist_is_denied` | WP6 |
| INV-03 | No client can present a synthetic principal. With authentication enabled, a request carrying an `AuthDisabled` or `AgentSynthetic` identity is rejected. | ✅ | Fixed in WP1 (TA-11). | `LoginHardeningTests.A_cookie_carrying_a_synthetic_identity_is_rejected` | WP1 |
| INV-04 | Every unattended mutation (scheduled task, AutoUpdate, AutoPatch, AI trigger, webhook, git deploy) runs under a recorded owner principal. The owner's authority for that capability is re-checked at **each** execution. If the owner no longer holds it, the run is refused and audited. | ⚠️ | Since WP2 step 2.3 scheduled tasks, AutoUpdate and AutoPatch run on behalf of a recorded owner whose authority is re-read on every run (PC-D6). An unattended `Confirm` is a terminal refusal that raises no approval (R1-01). R1-04: owners are required by default (`Automation:RequireOwner=true`; the grace period only when set to `false`); an owner whose local account was deleted has no authority; a run refused for lost authority suspends the item and notifies an Admin; agent-created tasks keep the agent's rules. Open: AI triggers have no owner yet, webhooks and Git deploy (step 2.4). TA-04, B-10. | `AutomationMutationRunnerTests`, `KillSwitchTests.Unattended_runs_never_auto_confirm`, `KillSwitchTests.AutoUpdate_and_AutoPatch_are_suspended_when_their_owner_lost_authority` | WP2 |
| INV-05 | A human is identified by a stable subject (OIDC `sub` + issuer, or local user id), not by the email claim alone. An OIDC login without `email_verified=true` cannot match the whitelist. | ❌ | Identity is the email claim, `email_verified` is unchecked. TA-20. | `OidcIdentityBindingTests` *(planned, WP6)* | WP6 |
| INV-06 | Each AI trigger acts under its own principal (`trigger:<id>`), distinguishable in policy, approval and audit. | ✅ | R1-03: a trigger runs as `AgentPrincipalKind.Trigger` with its own `trigger:<id>`; renaming it keeps the identity and two triggers with one name stay apart in policy, approvals (actor id) and audit. TA-31, C-12. | `PrincipalPropagationTests` | R1-03 |

## 2. Credentials and sessions

| ID | Invariant | Status | Evidence / gap | Test | WP |
|---|---|---|---|---|---|
| INV-07 | MCP key material is persisted only as a hash. The plaintext key is returned once, at creation, and cannot be planted or changed through a settings save. | ✅ | Since 0.15.0. | `McpPermissionServiceTests.A_new_key_is_returned_once_in_plaintext_but_only_its_hash_reaches_the_disk`, `McpPermissionServiceTests.Saving_from_the_UI_can_neither_plant_a_key_nor_change_key_material` | — |
| INV-08 | Disabling or deleting a key takes effect on the next tool call. This includes agent sessions that were started with that key. | ⚠️ | New requests are refused immediately. A running `instruct_agent` session keeps its principal snapshot. audit C §4 (identity). | `KillSwitchTests` *(planned, WP6)* | WP6 |
| INV-09 | Removing a user, removing them from the whitelist, or lowering their role ends their cookie sessions, Blazor circuits and terminal sessions within a bounded time (target ≤ 60 s). | ❌ | Sliding 24 h cookies, no security stamp; circuits cache the role. TA-19, B-17. | `SessionRevocationTests` *(planned, WP6, wire test)* | WP6 |
| INV-10 | Local login is protected against brute force: account lockout, per-client rate limit, and equal timing for unknown users and wrong passwords. | ✅ | Fixed in WP1 (TA-03). | `LoginHardeningTests.Five_wrong_passwords_lock_the_account_and_the_client_is_then_rate_limited` | WP1 |
| INV-11 | First-run setup can only be completed with the one-time setup token, and the last Admin can never be removed or demoted. | ✅ | Fixed in WP1 (TA-02). | `SetupStateServiceTests`, `RoleServiceLastAdminTests` | WP1 |
| INV-12 | With `AUTH_DISABLED`, only clients from the trusted network with an allowed `Host` and no cross-site `Origin` are served. | ✅ | Fixed in WP1 (TA-11). | `AuthDisabledAccessPolicyTests` | WP1 |

## 3. Authorization and scopes

| ID | Invariant | Status | Evidence / gap | Test | WP |
|---|---|---|---|---|---|
| INV-13 | Every mutating capability has exactly one required level in one capability table. UI roles and MCP levels map onto that table by a single fixed rule (Viewer↔read, Operator↔write, Admin↔admin), and every surface consults it. | ❌ | Two models disagree: firewall, nginx, systemd, cloud and Hetzner tools are Write over MCP but Admin in the UI. TA-28, B-13. | `CapabilityTableConsistencyTests` *(planned, WP2)* | WP2 |
| INV-14 | Every MCP tool checks the caller's scope before acting. A tool without a declared level resolves to Admin. | ✅ | Checked per tool for all 86 tools. There is no central filter yet that would catch a future omission (moves to the gateway, INV-38). | `McpToolLevelTests`, `McpToolRegistrationTests` | — |
| INV-15 | A tool's Enabled switch and a stored level stricter than the code default apply to every principal kind: API key, web user, in-process agent and AI trigger. | ✅ | Since WP2 step 2.1: mutations in the Mutation Gateway (`ICapabilityOverrides`), reads in `McpGuardrailFilter` and `AgentToolInvoker`. A stored level can only raise the code level. TA-10, C-05 fixed. | `ToolAuthorizationTests` | WP2 |
| INV-16 | A key's `AllowedTools` list can only narrow what its level permits, never widen it. | ✅ | Since WP2 step 2.1 the level is checked before the tool runs, for every tool: in the Mutation Gateway (every write and admin tool has a capability) and in `McpGuardrailFilter` for reads. `McpPermissionService.IsToolAllowed` still lets the list override the level, but it is no longer the only check. TA-34, C-16 fixed. | `ToolAuthorizationWireTests` | WP2 |
| INV-17 | Every server-side handler that mutates state enforces its required role inside the handler. Hiding a button is never the control. | ⚠️ | Servers pause/resume (U-22) and the firewall dialog submit (U-15) rely on markup; `SaveMcpPermissions` has no in-method check (C-24). TA-25. | `EndpointAuthMatrixTests` *(planned, WP10)* | WP10 |
| INV-18 | A Viewer cannot change shared state: no approval decisions, no notification deletion, no scan or job start. | ❌ | TA-22, A-09, C-10. | `EndpointAuthMatrixTests` *(planned, WP10)* | WP10 |
| INV-19 | Root-equivalent capabilities require Admin on every surface: host shell, container shell, `execute_command`, compose/app deploy, scheduled custom commands (create **and** run), arbitrary SQL. | ⚠️ | UI deploy fixed in WP1 (TA-01). WP2 step 2.2: container terminal (T-01/T-02) and "Run now" of a custom command (B-10) need Admin in the UI, `execute_query` / `db.query` is Admin on every surface (TA-43, PC-D3). Holds; only `db.query` is pinned by a test so far. | `UiMutationRunnerTests.Arbitrary_sql_needs_admin_on_the_ui_too`, `CapabilityTableConsistencyTests` *(planned, WP2)* | WP2 |

## 4. Policy and guardrails

| ID | Invariant | Status | Evidence / gap | Test | WP |
|---|---|---|---|---|---|
| INV-20 | No infrastructure mutation executes without a policy decision taken for exactly that call, on every surface (agent, MCP, UI, automation, webhook, terminal open). | ⚠️ | Agent and direct MCP calls are decided in the Mutation Gateway (WP2 step 2.1), UI handlers of marked capabilities since step 2.2 (PC-D1: authorization, kill switch, protected resources; no approval for a human), scheduled tasks, AutoUpdate and AutoPatch since step 2.3. Webhooks, terminals, Kubernetes workload actions and the unmarked UI actions (container `.env`, Artisan, server records, onboarding, self-backup) are not. TA-04, TA-26. | `UiMutationRunnerTests`, `AutomationMutationRunnerTests`, `ToolAuthorizationTests`, `MutationGatewayCoverageTests` *(planned, WP2)* | WP2 |
| INV-21 | While the read-only kill switch is on, every mutation on every path is denied, and no new terminal session opens. The only exception is switching the kill switch off (Admin, audited). | ⚠️ | Applies to agent, direct MCP, (since WP2 step 2.2) the UI handlers that go through the gateway and (since step 2.3) scheduled tasks, AutoUpdate and AutoPatch. Not yet: webhooks, terminals, the unmarked UI actions. B-02, B-09. | `UiMutationRunnerTests.The_kill_switch_stops_a_human_too`, `KillSwitchTests` *(planned, WP6, wire test)* | WP6 |
| INV-22 | Every policy decision is persisted as a decision record (id, time, principal, capability, resolved target, input hash, policy version hash, preset, matched rules, verdict) **before** the mutation runs. Approvals and audit events reference it by id. | ❌ | Decisions are transient. Phase 4. | `PolicyDecisionRecordTests` *(planned, WP4)* | WP4 |
| INV-23 | The effective policy of a run is fixed when the run starts. For an AI trigger it is the trigger's pinned preset, combined most-restrictively with the global kill switch. | ❌ | Trigger runs read the global live preset. TA-07, C-01. | `TriggerPolicyPinningTests` *(planned, WP4)* | WP4 |
| INV-24 | Protected resources are matched against the **resolved** target (canonical container id, name, unique id prefixes, compose project, K8s workload). Free-text arguments (commands, SQL, compose YAML) that reference a protected name are denied or require approval. | ❌ | Whole-value glob match on argument values. Since WP2 step 2.1 MCP and agent mutations on containers also match the resolved full id and name; free text (commands, SQL, compose YAML) and UI paths are not covered. TA-09, C-02. | `ProtectedResourceResolutionTests` *(planned, WP4)* | WP4 |
| INV-25 | Any error while evaluating policy (invalid regex, regex timeout, unreadable or missing policy) yields Deny. | ⚠️ | R1-02: an invalid forbidden-argument pattern denies every call, a pattern or glob timeout denies. An unreadable or invalid `guardrails.json` (broken JSON, no presets unless it is a legacy single-policy file, a missing field or list entry, an active preset that does not exist) puts the store into a fail-closed policy (kill switch, empty allowlist, everything protected) without overwriting the file, until an Admin saves a valid one. **Open:** a missing file is still created with the restrictive default, so a deleted file cannot be told apart from a first start. TA-33. | `GuardrailEngineTests.Invalid_policy_regex_does_not_throw` (inverted to Deny), `PolicyFailClosedTests` | R1-02 |
| INV-26 | Policy enforcement does not depend on an optional module. Disabling the Agent module leaves direct MCP calls guarded. | ✅ | Since WP2 step 2.1 the guardrail engine, store, approvals and the direct-MCP guard are core (PC-D8); `McpGuardrailFilter` refuses every call if they are missing. The Guardrails and Approvals pages are core routes. TA-27, C-14 fixed. | `GuardrailModuleIndependenceTests` | WP2 |
| INV-27 | Untrusted text (log lines, container and image names, event payloads, tool output) that enters an LLM prompt is delimited as data. It can never raise a run's autonomy. AI trigger runs are read-only unless the trigger has an explicit write allowlist of tools and targets. | ❌ | The matched log line is embedded verbatim; triggers default to Write. TA-08, C-03. | `TriggerPromptIsolationTests` *(planned, WP4)* | WP4 |
| INV-28 | Policy changes (guardrail config, preset switch, kill switch, triggers) are Admin-only, versioned and audited with a diff. The live policy is an immutable snapshot that UI edits cannot alias. | ⚠️ | Admin-only holds. Snapshot holds since R1-02: `GuardrailStore` hands out and stores copies only, validates before saving and swaps the live policy only after the file is written (TA-32, C-09). Open: versioning and audit with a diff (ILogger only). | `PolicyChangeAuditTests` (snapshot part) | R1-02 / audit WP |
| INV-29 | The policy verdict is deterministic and independent of any LLM output except the tool name and arguments it evaluates. | ✅ | `GuardrailEngine` is stateless, most-restrictive-wins. | `GuardrailEngineTests.Deny_beats_confirm` and the rest of `GuardrailEngineTests` | — |

## 5. Approvals

| ID | Invariant | Status | Evidence / gap | Test | WP |
|---|---|---|---|---|---|
| INV-30 | An approval binds a hash over (principal, credential, capability, normalized arguments, resolved target, policy decision id, expiry). A call executes on an approval only if its own hash is identical. Changed arguments or a changed target need a new approval. | ⚠️ | Direct MCP binds actor + tool + raw args (no target, no policy). The agent path binds the in-memory call object. audit C §1 (approval integrity). | `ApprovalBindingTests` *(planned, WP3)* | WP3 |
| INV-31 | Approvals and their decisions are durable: they survive a restart, and their history is never truncated by a count cap. | ❌ | In memory, 200 resolved entries kept. TA-06c, C-04. | `ApprovalPersistenceTests` *(planned, WP3)* | WP3 |
| INV-32 | An expired approval can be neither approved nor executed. Expiry is checked at resolve time **and** at execution time. | ❌ | `ResolveAsync` does not check `ExpiresAt`. TA-18, C-07. | `ApprovalStoreTests` (new cases, WP3) | WP3 |
| INV-33 | The principal that requested an approval cannot approve it when the request is non-interactive (direct MCP, AI trigger, scheduled task). Interactive agent chat may self-confirm only if policy allows it. | ❌ | Self-approval is not prevented anywhere. TA-18, C-10. Rule per PC-D4. | `ApprovalSeparationOfDutyTests` *(planned, WP3)* | WP3 |
| INV-34 | The approver's level is at least the **effective** level of the action (for example Admin for a scheduled custom command, not the tool's Write level). | ⚠️ | The level check exists; the level used is the tool level. TA-18, B-11a, C-11. | `ApprovalSeparationOfDutyTests` *(planned, WP3)* | WP3 |
| INV-35 | An approval is single-use. Concurrent attempts to execute on one approval result in exactly one execution. | ⚠️ | Holds in memory (CAS / atomic `TryRemove`). Must hold again once approvals are persisted. | `ApprovalStoreTests.Resolving_twice_returns_false_and_runs_resolver_once`, `DirectMcpCallGuardTests.Confirm_raises_one_approval_and_the_identical_call_runs_once_after_a_human_approves`; `ApprovalConcurrencyTests` *(planned, WP3)* | WP3 |
| INV-36 | An approval whose requesting session has ended does not execute unattended. It expires with the session. | ❌ | An orphaned session still executes a later approval. C-08. | `ApprovalStoreTests.CancelForSession_denies_only_that_sessions_pending_approvals` (partial); `OrphanedApprovalTests` *(planned, WP3)* | WP3 |
| INV-37 | Rejecting an approval requires the same level as approving it. | ❌ | Viewers can reject any approval. TA-22, C-10. | `ApprovalSeparationOfDutyTests` *(planned, WP3)* | WP3 |

## 6. Execution

| ID | Invariant | Status | Evidence / gap | Test | WP |
|---|---|---|---|---|---|
| INV-38 | Every infrastructure mutation executes through the Mutation Gateway. No page, MCP tool, hosted service or webhook handler calls a mutating infrastructure service directly. | ❌ | Gateway, capability catalog and report-mode scope guard exist (WP2 step 2.0). Since step 2.1 every MCP tool and agent call that maps to a capability runs inside the gateway, since step 2.2 every UI page call to a marked capability (`IUiMutationRunner`), since step 2.3 scheduled tasks, AutoUpdate and AutoPatch (`IAutomationMutationRunner`); 23 bypasses remain in `docs/v1/mutation-bypass-baseline.txt` (remaining background services, webhooks, provider internals, and three calls from MCP tools to a capability other than their own; 104 after step 2.0, 75 after 2.1, 29 after 2.2). Execution primitives (`IHostCommandExecutor`, `RunHostShellAsync`) are not marked yet. | `MutationBypassArchitectureTests` (baseline may only shrink), `MutationScopeGuardTests` | WP2 |
| INV-39 | Every mutation has a unique operation id that links decision, approval, execution, verification and audit. A retry with the same operation id does not execute twice. | ❌ | Only the agent path has a CorrelationId; AuditLog has no correlation column. TA-52, audit C §3 (correlation). | `OperationIdempotencyTests` *(planned, WP9)* | WP9 |
| INV-40 | Every execution has a deadline. Unattended runs (triggers, LLM streams, webhooks) cannot hold a slot indefinitely. | ❌ | TA-45. | `ExecutionDeadlineTests` *(planned, WP9)* | WP9 |
| INV-41 | Commands are passed as a single argv element, and every user-controlled value placed into a command line is validated against option injection (git branch, SSH user/host, nginx site name, VPN arguments). | ⚠️ | argv passing holds. Validation missing: TA-41, TA-42, E-03, E-08. | `McpInputValidationTests` (extended), `OptionInjectionTests` *(planned, WP2)* | WP2 |
| INV-42 | A webhook executes only if its HMAC is valid, its timestamp is fresh and its delivery id has not been seen. Executions for the same target are serialized. | ⚠️ | HMAC holds. No replay protection, no serialization. TA-21. | `WebhookSignatureTests` (extended), `WebhookReplayTests` *(planned, WP9)* | WP9 |

## 7. Verification

| ID | Invariant | Status | Evidence / gap | Test | WP |
|---|---|---|---|---|---|
| INV-43 | Every mutating capability declares a verifier. The recorded outcome is `Verified`, `Failed` or `NotVerifiable` and is never inferred from transport status. | ❌ | Verification is absent on most paths; Agent History "success" is the HTTP status. TA-30, B-19, C-13. | `VerificationOutcomeTests` *(planned, WP2)* | WP2 |
| INV-44 | Every recovery feature the UI or docs offer actually runs. A setting that is not implemented is not shown. | ❌ | `AutoRollback` is persisted (default on) and never executed. TA-29. | `AutoRollbackTests` *(planned, WP9)* | WP9 |

## 8. Audit

| ID | Invariant | Status | Evidence / gap | Test | WP |
|---|---|---|---|---|---|
| INV-45 | Every mutation attempt — allowed, denied, waiting for approval, failed — produces exactly one audit event with: principal (INV-01), surface, capability, resolved target, redacted arguments, decision id, approval id and approver, outcome, verification result, duration, operation id. | ❌ | Many paths write nothing to the AuditLog (B-04, B-07); none writes the full field set. TA-06. | `AuditCompletenessTests` *(planned, WP5)* | WP5 |
| INV-46 | Every security-relevant configuration change is audited: roles, whitelist, keys, tool levels, guardrails, presets, triggers, webhooks, git-deploy apps, log alerts, authentication events, backup downloads. | ⚠️ | Settings writes are audited. Guardrails, triggers, log alerts, git-deploy save/delete, auth events and backup downloads are not. TA-06b, A-10. | `AuditLoggingTests` (extended) *(WP5)* | WP5 |
| INV-47 | Opening and closing every terminal session is audited with principal, target and duration. No keystroke transcript in 1.0 (PC-D2). | ❌ | Terminals log to ILogger only. TA-05. | `TerminalAuditTests` *(planned, WP2)* | WP2 |
| INV-48 | An audit *intent* record is durably written before a mutation runs. If it cannot be written, the mutation does not run. | ❌ | `AuditLogService` writes after the fact and, on failure, logs to ILogger and lets the action stand. Fail-closed per PC-D5. | `AuditFailClosedTests` *(planned, WP5)* | WP5 |
| INV-49 | The audit trail is tamper-evident: `eventHash = SHA256(prevHash ‖ canonicalEvent)`. A verification routine detects any modified, inserted or deleted event. | ❌ | Plain rows, no chain. TA-35. ADR required first. | `AuditHashChainTests` *(planned, WP5)* | WP5 |
| INV-50 | Audit retention is configurable, deletion is itself audited, audit data can be exported, and only Admin (or a dedicated auditor role) can read it. | ❌ | Fixed 90-day hard delete, no export, visible to all Viewers. TA-35, B-20. | `AuditRetentionTests` *(planned, WP5)* | WP5 |

## 9. Secrets and redaction

| ID | Invariant | Status | Evidence / gap | Test | WP |
|---|---|---|---|---|---|
| INV-51 | Secrets never reach a persistent or outbound sink in plaintext: logs, AuditLog, Agent History, approvals, transcripts, notifications. Redaction runs before persistence on every sink. | ⚠️ | Redaction runs before AuditLog / Agent History / approvals. Transcripts keep user and assistant text unredacted (C-21); the logging layer has no redaction (D-12). | `SecretRedactorTests` (extended), `SinkRedactionTests` *(planned, WP5)* | WP5 |
| INV-52 | Every credential for managed infrastructure (cloud, DNS, SMTP, chat, AI provider, git tokens, webhook secrets) is encrypted at rest by the vault. | ❌ | Stored as plaintext JSON. TA-12, D-01. | `SecretsAtRestTests` *(planned, WP7)* | WP7 |
| INV-53 | Rollback snapshots and other stored container configs contain no plaintext secrets. | ❌ | Full env stored and replayed. TA-40, D-11. | `SecretsAtRestTests` *(planned, WP7)* | WP7 |
| INV-54 | A wrong or changed `VAULT_KEY` is detected at startup and the app refuses to write to the vault with it. | ❌ | TA-36, D-03. | `VaultKeyCheckTests` *(planned, WP7)* | WP7 |
| INV-55 | Restore accepts only allowlisted archive entries from an archive whose authenticity is proven (AEAD with the instance key). An unencrypted archive is refused when `VAULT_KEY` is set. | ❌ | TA-13, D-02. | `RestoreBootHandlerTests` (extended), `BackupArchiverTests` (extended) *(WP7)* | WP7 |
| INV-56 | Every self-report (production-readiness check, README, SECURITY.md, UI hints) matches the code's actual behaviour. | ❌ | Readiness reports plaintext secrets as encrypted. TA-12. | `ProductionReadinessServiceTests` *(planned, WP7)* | WP7 |

## 10. Target isolation

| ID | Invariant | Status | Evidence / gap | Test | WP |
|---|---|---|---|---|---|
| INV-57 | Every mutation acts on exactly one resolved target (server + resource) that exists in the inventory. An ambiguous or unknown target is refused before policy evaluation. | ⚠️ | Since WP2 step 2.1 the gateway resolves **container** targets (id, name or unique prefix) and refuses unknown or ambiguous ones before policy. Other target kinds (host, network, DNS zone, cloud server, task) pass through and are resolved by the service. | `ToolAuthorizationTests` (containers), `TargetResolutionTests` *(planned, WP2)* | WP2 |
| INV-58 | Compromising the credential of one managed host grants no access to any other host. | ❌ | One mTLS client key and one fixed `--allow-cn` across the fleet. TA-44, E-06. | `PerHostCredentialTests` *(planned, WP7)* | WP7 |
| INV-59 | Whiskers' own control plane (its container, data volume, database, proxy and mTLS material) is protected by default on every path except an interactive Admin action that is approved and audited. | ❌ | `whiskers*` protection is bypassable (INV-24) and absent outside agent/MCP (INV-20). TA-09, B-09. | `SelfProtectionTests` *(planned, WP4)* | WP4 |

## 11. Count

| Status | Count |
|---|---|
| ✅ | 10 (INV-03, 07, 10, 11, 12, 14, 15, 16, 26, 29) |
| ⚠️ | 16 |
| ❌ | 33 |
| ❓ | 0 |
| **Total** | **59** |
