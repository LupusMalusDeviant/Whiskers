# Audit C: Agent, MCP permissions, guardrails, approvals, audit, identity

Scope: read-only review of `src/Whiskers` (Services/Agent/**, Mcp/**, Services/Mcp/McpPermissionService.cs, Services/AuditLog/**, Services/Observability/McpCallLogStore.cs, Models/Agent/**, Configuration/Guardrail*.cs, Modules/Agent/AgentModule.cs, Utils/SecretRedactor.cs, Approvals/Guardrails/AgentHistory pages).
All paths below are relative to `src/Whiskers/` unless prefixed otherwise. Date: 2026-10-26.

---

## Answers to the audit questions

### 1. Approval integrity

**What an approval binds.**
- Agent path (`ApprovalCoordinator.RaiseAsync`, Services/Agent/Approvals/ApprovalCoordinator.cs:40-70): the approval stores SessionId, ToolCallId, Actor, ActorType, ToolName, Level, redacted ParamsJson, Reason, CorrelationId, RiskLevel, TargetServer/Workload, preset, rule (Models/Agent/Approval.cs:37-83). Execution is bound only through the resolver closure `session.ResolveConfirmationAsync(call.Id, approved)` (ApprovalCoordinator.cs:67). Every other field is shown for display only, as the comments say (Approval.cs:25-26, 59-60).
- **What runs after approval:** the same in-memory `AgentToolCall` object the provider produced. `AgentSession.RunAsync` waits on a TCS keyed by `call.Id` (AgentSession.cs:143-150) and then calls `_invoker.InvokeAsync(call, LiveContext, ct)` on that same local `call` (AgentSession.cs:175). Nothing is supplied again, so the arguments cannot change between approval and execution. The invoker re-checks the guardrails against the live policy and blocks only on Deny (AgentToolInvoker.cs:61-66). A policy that has become stricter still blocks the call. A Confirm verdict at invoke time is not re-checked, which is correct because the human already approved.
- Direct MCP path (`DirectMcpCallGuard`, Services/Agent/Guardrails/DirectMcpCallGuard.cs): approve-then-retry. The grant is bound to SHA-256(actor `key:<id>`/email, tool, sorted raw args) (:164-171). It is single-use: `_grants.TryRemove` is atomic (:89). It lasts 10 minutes (:36). If the arguments change, a new approval is needed. This is a good design.
- **Replay / reuse / duplicates:** On the agent path a TCS is completed once (`TrySetResult`, AgentSession.cs:62). `ApprovalStore.ResolveAsync` uses an Interlocked CAS so the resolver runs exactly once (ApprovalStore.cs:129), which makes a double-click on Approve safe. On the direct path the grant is consumed once. Residual risk: the resolver is keyed by the provider's ToolCallId, which is not globally unique. Gemini produces `call_{n}` again on every turn (Providers/GeminiWire.cs:158). See C-18.
- **Expiry:** the TTL is 15 minutes (ApprovalStore.cs:50). Expiry is lazy: it is swept only inside `GetPending()`/`GetAll()` (ApprovalStore.cs:154-166). `ResolveAsync` checks `Status != Pending` but does **not** check `ExpiresAt` (ApprovalStore.cs:115). The session waits without a timeout (AgentSession.cs:150; the UI passes no CancellationToken: AgentView.razor:377, AiChat.razor:261). See C-07 and C-08.
- **Persistence:** approvals are kept in memory only, in a `ConcurrentDictionary` capped at 200 resolved entries (ApprovalStore.cs:41, 182-191). A restart loses all pending and historical approvals, and nothing is written to the AuditLog (C-04).
- **Who may approve:** on the Approvals page the approver needs a level ≥ the approval's Level (`ResolveAsync(..., level)`, ApprovalsView.razor:171-173 → ApprovalStore.cs:120-126). Rejecting is always allowed, even for Viewers (ApprovalStore.cs:118-121). The inline chat buttons pass **no** resolver level (AgentView.razor:445, AiChat.razor:308), which is intended because the requester confirms their own action. **Self-approval is not prevented anywhere**: the actor can approve their own `mcp-direct` request on the Approvals page. There is no four-eyes option (C-10).
- **Notification content:** `"Agent \"{actor}\" wants to run \"{tool}\" — approval required."` plus the CorrelationId (ApprovalCoordinator.cs:72-77). The direct path sends no correlation ID (DirectMcpCallGuard.cs:142-146). There are no arguments, no approval ID and no link. External channels (Mattermost/Matrix) receive the actor's email.

### 2. Guardrail engine
- **Deterministic, stateless, LLM-independent.** It aggregates on a most-restrictive-wins basis: any Deny beats any Confirm, which beats Allow (Guardrails/GuardrailEngine.cs:28-56). Rule order affects only the reason text. Rules: PrincipalCeiling, ReadOnlyMode, ToolDenyList, ToolAllowList, ProtectedResource, ForbiddenArgument, Confirmation (GuardrailEngine.cs:17-26; BuiltInGuardrailRules.cs).
- **LLM influence:** the LLM cannot change the verdict logic. It controls only tool names and arguments, and it can therefore steer around the argument-matching rules (ProtectedResource via container ID, ForbiddenArg via different spellings) (C-02, C-15). The tool catalog hides tools that are statically denied (AgentToolCatalog.cs:25-35).
- **Agent module disabled:** `IDirectMcpCallGuard` is not registered, so `McpGuardrailFilter` passes every call through (Mcp/McpGuardrailFilter.cs:29). Direct MCP calls are then bounded by key scope alone. guardrails.json remains on disk but is not enforced, and the UI gives no warning (C-14).
- **Policy storage:** guardrails.json is stored through `JsonFileStore` (GuardrailStore.cs:25-27). Writes require `editor.PermissionLevel == admin` (GuardrailStore.cs:135-140), and the editor principal is built from the caller's AppRole (GuardrailsView.razor:327-337). There is no integrity protection or version history, and changes go to ILogger only, never to the AuditLog (GuardrailStore.cs:86). `SaveConfigAsync` stores the caller's object by reference (`_config = config`, :83), and the getter returns the live object (`Config => _config`, :30) (C-09).
- **Preset switching:** done in GuardrailsView (`MakeActive`, then save) and AiOperationsView (:166-169). Both go through admin-only `SaveConfigAsync`.
- **AI triggers:** the principal is `McpKey "ai-trigger:{name}"` at `t.MaxLevel` (default write) with `McpKeyId: "ai-trigger"` (Triggers/AiTriggerDispatcher.cs:107-111). Confirm verdicts are auto-denied (:122-127). **The trigger's pinned `GuardrailPreset` is ignored**: `AgentService` always passes the global `IGuardrailStore` into the session (AgentService.cs:76), and `AgentSession.LivePolicy => _guardrailStore?.Current ?? _context.Policy` (AgentSession.cs:194) prefers the global active preset (C-01). A trigger can make changes without a human whenever the effective preset's MaxAutonomousLevel ≥ write or RequireConfirmationForWrites is false. The trigger prompt includes untrusted log-line text (C-03).
- **ClaudeCodeRuntime:** registered (AgentModule.cs:90-91) but never called anywhere, so it is dead code. Its rights check is fail-open when the configured key is invalid (ClaudeCodeRuntime.cs:54). The CLI acts under the configured key, not under the caller (C-20).

### 3. Audit
- **AuditLog** (`AuditLogEntity`: Timestamp, Actor, ActorType, Action, TargetType/Id/Name, Details, ServerId, Success; src/Whiskers.Data/Entities/AuditLogEntry.cs). Individual tools write it with an actor from `IAuditLogService.GetActorFromHttpContext` (Services/AuditLog/IAuditLogService.cs:21-48). Agent-executed tools appear as `agent:<email|keyId|"ai-trigger">` with ActorType `"web"`, because the synthetic context carries an email claim (AgentToolInvoker.cs:223-229). Trigger runs add one row, `agent.run` (AiTriggerDispatcher.cs:174-183).
- **Agent History** (`McpToolCallEntity`: Actor, ActorType, ToolName, Level, CorrelationId, ParamsJson (redacted), Verdict, Success, DurationMs, ResultSummary (redacted), ServerId, Error; src/Whiskers.Data/Entities/McpToolCall.cs). Rows are written by `AgentToolInvoker.RecordAsync` (:209-235) and, for direct calls, by `McpCallLogMiddleware` (Mcp/McpCallLogMiddleware.cs:107-138).
- **Not captured anywhere:** approver identity, approval ID, approval decision and time, policy and preset version at decision time, credential ID for direct calls (the key *name* is used), post-execution verification, guardrail/trigger/preset changes (C-04).
- **Redaction:** `SecretRedactor.Redact` runs **before persistence** in both stores (AgentToolInvoker.cs:225-229 region; McpCallLogMiddleware.cs:126; ApprovalCoordinator.cs:43). It fails closed on regex timeout (Utils/SecretRedactor.cs:80-84). Transcripts redact only Tool-role text (AgentTranscriptStore.cs:80-81) (C-21).
- **Retention:** a hard-coded 90-day `ExecuteDelete` for both tables (Services/Metrics/MetricsCollectorService.cs:277-280). It is not configurable and has no archive or export step.
- **Modification/deletion:** no UI or API path to edit or delete audit rows was found; the pages are read-only. There is **no tamper evidence**: plain rows sit in the metrics DB, with no hash chain and no append-only enforcement. Anyone with DB or file access can edit them (C-17).
- **Correlation:** the agent path has `AgentToolCall.CorrelationId` (Models/Agent/AgentDtos.cs:29), which ties together approval, notification and call-log row. Direct calls have none (McpToolCall.cs:20-24 notes this). The AuditLog has no correlation column, so AuditLog rows cannot be joined to Agent History rows.

### 4. Identity
- Human (cookie): email plus a role from `RoleService`, with unknown users falling back to `DefaultRole` (Services/Auth/RoleService.cs:70-81). In the agent this becomes `AgentPrincipal(WebUser, email, level)`.
- MCP key: `McpBearerAuthMiddleware` sets the claim `mcp-api-key=true`, with no key ID claim (Mcp/McpBearerAuthMiddleware.cs:32-38). Each consumer looks the key up again from the header (AgentPrincipalResolver.cs:307-316; IAuditLogService.cs:34-41; McpCallLogMiddleware.cs:144-154). Different places use different identity strings: key Name (AuditLog), `key:<Name>` (call log), `key:<Id>` (approvals and fingerprint), and the Id GUID (agent synthetic actor).
- Agent: `AgentSynthetic` scheme with the email claim `agent:<…>` and the claim `sw:mcp-level` (AgentToolInvoker.cs:221-235). `McpPermissionCheck.WebUserLevel` honours the claim level, so the synthetic principal is not admin (Mcp/McpPermissionCheck.cs:56-57). Positive.
- AuthDisabled: the synthetic `local@serverwatch.local` is admin everywhere (Startup/WhiskersPipelineExtensions.cs:136-148; McpPermissionCheck.cs:60-61; AgentPrincipalResolver.cs:319-321), including on `/mcp` without a key (C-06).
- **Confused-deputy risks:**
  - The in-process agent uses the web-user/claim branch of CheckAccess, which ignores the per-tool `Enabled` flag and stored stricter levels (C-05).
  - Trigger runs share one identity, `ai-trigger` (C-12).
  - ClaudeCodeRuntime acts under a fixed key (C-20).
  - An instruct_agent session keeps its principal snapshot after the key is revoked; the in-flight run continues (Low).

### 5. MCP
- **Registration:** the tools are the enabled modules' `McpToolTypes` (Startup/WhiskersHostingExtensions.cs:124-155), together with `OperationsPrompts` and `WhiskersResources` (metadata only: tool catalog and my-permissions).
- **Scope enforcement is per tool, not central.** A scripted check of all 86 `[McpServerTool]` methods found that each one calls `McpPermissionCheck.CheckAccess` (or `Guarded`) with a matching snake_case name. No tool lacks a check today, but there is no central filter to catch a future omission. Undeclared levels default to admin (McpPermissionCheck.cs:31; DirectMcpCallGuard.cs:71), which fails closed.
- **Scope bypasses:**
  - The `AllowedTools` list on a key overrides the level check (McpPermissionService.cs:177-178) (C-16).
  - Cookie and agent callers ignore the `Enabled` toggle (C-05).
  - AuthDisabled gives anonymous callers admin (C-06).
- **Key revocation:** immediate. `ValidateKey` runs on every request against an atomically swapped snapshot (McpPermissionService.cs:137-144, 346-364). Only agent sessions that are already running keep the old principal.

---

## Findings

| ID | Title | Sev | Category | v1-blocker |
|---|---|---|---|---|
| C-01 | AI-trigger guardrail preset is ignored; trigger runs under the global active preset | High | Security | yes |
| C-02 | ProtectedResources is trivially bypassed (container ID/prefix, embedded in commands) | High | Security | yes |
| C-03 | Untrusted log/event text is injected into autonomous trigger prompts | High | Security | yes |
| C-04 | Approval decisions and policy/trigger changes are not audited; approvals are in memory only | High | Operations / Commercial readiness | yes |
| C-05 | Tool "Enabled" toggle and stricter stored levels are ignored for the agent and cookie callers | High | Security | yes |
| C-06 | AUTH_DISABLED makes unauthenticated /mcp callers admin; all approvals attributed to one synthetic user | High | Security | yes (needs verification of deployment guards) |
| C-07 | Expired approvals can still be approved; expiry is lazy | Medium | Security / Reliability | no |
| C-08 | Orphaned agent sessions keep waiting; a later approval executes unattended | Medium | Reliability / Security | no |
| C-09 | Live guardrail config is aliased to UI state after save | Medium | Security / Reliability | no |
| C-10 | No separation of duties; Viewers can reject or see every approval | Medium | Security / Commercial readiness | no |
| C-11 | Agent-path approval level uses a different source (default "write") than the gate | Low | Architecture | no |
| C-12 | Actor identity is lossy or inconsistent across stores | Medium | Operations | no |
| C-13 | Direct-call Agent History rows are unreliable (success = HTTP status, shared verdict) | Medium | Operations | no |
| C-14 | Guardrails silently disappear when the Agent module is off | Medium | Security / UX | no |
| C-15 | ForbiddenArgPatterns is a bypassable denylist and fails open | Medium | Security | no |
| C-16 | Key AllowedTools bypasses the level check | Medium | Security | no |
| C-17 | Audit data has no tamper evidence, a fixed 90-day purge and is visible to all Viewers | Medium | Commercial readiness | no |
| C-18 | Approval resolver keyed by non-unique provider ToolCallId | Low | Security | no |
| C-19 | Rate limits are per session only; pending approvals are unbounded | Low | Reliability | no |
| C-20 | ClaudeCodeRuntime: dead code, fail-open rights check, fixed-key deputy | Low | Security / Architecture | no |
| C-21 | Transcripts persist unredacted user/assistant text and tool arguments | Low | Security | no |
| C-22 | Approval notifications carry no context (no ID, args or link) | Low | UX | no |
| C-23 | Missing tests for the above (trigger preset, ID bypass, expiry-on-resolve, approval audit) | Low | Testing | no |
| C-24 | Settings `SaveMcpPermissions` has no in-method admin check | Low | Security | no |

### C-01: AI-trigger guardrail preset is ignored (High, Security, v1-blocker: yes)
- **Evidence:**
  - Triggers/AiTriggerDispatcher.cs:101-111 builds `ctx` with the trigger's preset policy.
  - AgentService.cs:76 always passes `_guardrailStore` into `new AgentSession(...)`.
  - AgentSession.cs:192-195 `LivePolicy => _guardrailStore?.Current ?? _context.Policy`, and the comment says pinned trigger runs should use the context. The engine (AgentSession.cs:204), the rate limit (:168) and the invoker (:175 via LiveContext) all use the global active preset.
  - No test covers this (AgentModuleTests/AgentSessionTests do not pin a trigger preset).
- **Impact:** An admin who builds a restrictive "Trigger" preset gets the global preset, which may be more permissive. This is the main safety control for unattended runs, and the UI shows it as configured even though it is not applied.
- **Recommendation:** Add a `pinPolicy` flag, or pass `guardrailStore: null` for `AgentOrigin.Trigger` sessions. Optionally, apply the most restrictive of the pinned and global presets so the global read-only kill switch still applies. Add a regression test.

### C-02: ProtectedResources is trivially bypassed (High, Security, v1-blocker: yes)
- **Evidence:**
  - BuiltInGuardrailRules.cs:114-138: an anchored glob is matched against whole argument values only (`GlobMatch` :24-29).
  - Mcp/Tools/McpInputValidation.cs:25-31 resolves containers by exact ID, by name, or by a unique ID prefix.
  - Default protection is `whiskers*`/`serverwatch*` (Configuration/GuardrailPolicy.cs:44).
  - Passing `containerId="3f2a"`, or `execute_command` with `docker stop whiskers`, does not match.
- **Impact:** The feature presented as "Whiskers itself is protected" can be defeated by the LLM (including through prompt injection) or by any direct MCP client. Destructive actions on protected workloads are possible within the caller's level.
- **Recommendation:** Resolve targets before evaluation (tool-specific target extractors mapping ID or prefix to the canonical name and labels) and match protection on resolved entities. Deny commands that reference protected names by substring. Treat `execute_command` and similar shell tools as unprotectable and require Confirm or Deny when protections exist.

### C-03: Untrusted text in autonomous trigger prompts (High, Security, v1-blocker: yes)
- **Evidence:**
  - AiTriggerDispatcher.cs:151-171 appends `evt.ImageInfo`, `ContainerName` and `Image` verbatim into the task (`Info: ...` at :165).
  - For log alerts `ImageInfo` contains the matched third-party log line (Services/LogMonitor/LogMonitorService.cs:206).
  - The run has no human in the loop and holds write level by default (Models/AiTrigger.cs:27).
- **Impact:** Anyone who can write a log line in a monitored container can instruct the agent ("ignore previous… restart X / run Y"). The actions stay within the effective preset, which per C-01 is the global one. With a permissive preset this becomes autonomous, attacker-driven change.
- **Recommendation:** Wrap event data in clearly delimited untrusted blocks and strip control phrases. Force read-only or Confirm-only for trigger runs unless an explicit, separate "autonomous write" opt-in exists per trigger with an allowlist of tools and targets. Record the triggering event in the call log.

### C-04: Approvals and policy changes not audited; approvals in memory only (High, Operations/Commercial readiness, v1-blocker: yes)
- **Evidence:**
  - ApprovalStore.cs:39-50 is in memory, and `TrimResolved` (:182-191) drops resolved entries beyond 200.
  - No `IAuditLogService` use exists in ApprovalStore, ApprovalCoordinator, DirectMcpCallGuard, GuardrailStore, GuardrailsView, AiTriggersView or AiOperationsView. Only AiTriggerDispatcher.cs:174-183 writes audit rows.
  - GuardrailStore.cs:86 logs to ILogger only.
  - `McpToolCallEntity` has no ApprovedBy, ApprovalId or PolicyVersion fields (src/Whiskers.Data/Entities/McpToolCall.cs).
- **Impact:** After execution nobody can answer "who approved this destructive action, when, under which policy". History is lost on restart, and guardrail relaxations or trigger creation leave no audit trail. A governance or approvals product cannot pass a customer audit without these.
- **Recommendation:**
  - Persist approvals (create, resolve, expire, cancel) to the DB with approver identity and level.
  - Add `ApprovalId`, `ApprovedBy`, `PolicyPreset` and a policy hash to the call-log row.
  - Write AuditLog rows for guardrail config saves (with a diff), preset activation, trigger create/update/delete and MCP tool-toggle changes.

### C-05: Tool "Enabled" toggle and stricter stored levels are ignored outside bearer keys (High, Security, v1-blocker: yes)
- **Evidence:**
  - `IsToolAllowed` checks `toolConfig.Enabled` and the stored `RequiredLevel` (McpPermissionService.cs:163-185) but is used only for bearer keys (McpPermissionCheck.cs:43-49).
  - The cookie and AgentSynthetic branch uses `DefaultToolLevels` (code defaults) and never checks Enabled (McpPermissionCheck.cs:28-35).
  - The agent registry and catalog use `DefaultToolLevels` (AgentToolRegistry.cs:60; AgentToolCatalog.cs:28-33).
  - The Settings UI presents a global per-tool "Enabled" switch (Settings.razor:458-478).
- **Impact:** An admin who disables, for example, `execute_command` or raises `cloud_hard_reset` to admin still leaves it usable by the in-process agent (all origins, including triggers) and by any cookie user posting to `/mcp`.
- **Recommendation:** Centralise the decision in one `IToolAuthorization.Check(principal, tool)` that honours Enabled and the stored level for all principal kinds. Use it in CheckAccess, AgentToolCatalog/Invoker and DirectMcpCallGuard.

### C-06: AUTH_DISABLED gives anonymous /mcp callers admin (High, Security, v1-blocker: yes; needs verification of deployment guards)
- **Evidence:**
  - WhiskersPipelineExtensions.cs:136-148 sets a synthetic authenticated principal for every request, including `/mcp`.
  - With no Authorization header, `CheckAccess` takes the cookie branch and maps AuthDisabled to Admin (McpPermissionCheck.cs:28-35, 60-61).
  - `AgentPrincipalResolver` does the same (:319-321).
  - Approvals are all resolved as `local@serverwatch.local`.
- **Impact:** Any host that can reach the port can call `execute_command` (root on managed hosts) with no key, and attribution is meaningless.
- **Recommendation:**
  - Never apply the AuthDisabled principal to `/mcp`; require a key there always.
  - Refuse AUTH_DISABLED unless the app binds to loopback or a private network, or an explicit ack flag is set.
  - Show a persistent banner.
  - Cross-check with the auth audit area.

### C-07: Expired approvals can still be approved (Medium, Security/Reliability, v1-blocker: no)
- **Evidence:**
  - `ResolveAsync` checks only `Status != Pending` (ApprovalStore.cs:115). `IsExpired` (Approval.cs:83) is evaluated only by `SweepExpired`, called from `GetPending`/`GetAll` (ApprovalStore.cs:91-108, 154).
  - The inline chat Resolve calls `ResolveAsync` directly (AgentView.razor:445; AiChat.razor:308).
  - The session waits without a timeout (AgentSession.cs:150).
- **Impact:** A card left open past 15 minutes can still execute. A session can also hang indefinitely if no one opens the Approvals list.
- **Recommendation:** Check `ExpiresAt` inside `ResolveAsync` under the CAS. Run a timer-based sweep. Give `WaitAsync` a timeout equal to the TTL.

### C-08: Orphaned sessions execute later approvals unattended (Medium, Reliability/Security, v1-blocker: no; needs verification of circuit-dispose behaviour)
- **Evidence:**
  - `SendAsync` is called without a CancellationToken and the components do not implement IDisposable (AgentView.razor:377; AiChat.razor:261). `CancelForSessionAsync` runs only on "Clear chat" (AgentView.razor:424; AiChat.razor:335).
  - The pending approval stays on the shared Approvals page. Approving it there resumes the loop in a session nobody is watching (AgentSession.cs:150-178), and the loop may continue for up to `MaxToolIterations`.
- **Recommendation:** Implement IDisposable with a CTS and cancel session approvals on dispose. Alternatively, mark agent-web approvals as "requester-session only" and cancel them when the circuit ends.

### C-09: Live guardrail config aliased to UI state (Medium, Security/Reliability, v1-blocker: no)
- **Evidence:**
  - GuardrailStore.cs:83 `_config = config` stores the caller's instance, and :30 exposes the live instance.
  - GuardrailsView clones on load (:186) but not after `SaveAll` (:311-325). From then on, `@bind-Value` on `_edit.Policy.*` (:78-115) and `DeletePreset`/`MakeActive` (:270-279) mutate the live policy immediately, without saving and without logging.
  - Lists are also mutated while rules enumerate them on other threads (BuiltInGuardrailRules.cs:120-131), which can throw "collection modified".
- **Recommendation:** Deep-clone on save and in the getter, or make the policy immutable (records with ImmutableArray). Have `Current` return a frozen snapshot.

### C-10: No separation of duties; Viewers can reject or see all approvals (Medium, Security/Commercial readiness, v1-blocker: no)
- **Evidence:**
  - ApprovalsView.razor:166-173 has no `Actor != resolver` check.
  - Rejecting is always allowed (ApprovalStore.cs:118-121).
  - The page is visible to Viewer (AgentModule.cs:41; Approvals.razor:2 `[Authorize]` only) and shows ParamsJson (ApprovalsView.razor:60-64).
- **Recommendation:** Add a policy option "requester cannot approve" (four-eyes) for admin-level or protected actions. Require at least the approval's level to reject. Restrict param visibility to Operator and above.

### C-11: Approval level source differs from the gate (Low, Architecture, v1-blocker: no)
- **Evidence:** ApprovalCoordinator.cs:46 uses `DefaultToolLevels.GetValueOrDefault(call.Name, "write")`, while the gate uses the registry entry's `RequiredLevel` (AgentSession.cs:204). The direct path defaults to admin (DirectMcpCallGuard.cs:71). Today these agree because the registry only contains tools listed in DefaultToolLevels (AgentToolRegistry.cs:60).
- **Recommendation:** Pass the evaluated `RequiredLevel` into `RaiseAsync`, and fail closed to admin for unknown tools.

### C-12: Actor identity is lossy or inconsistent (Medium, Operations, v1-blocker: no)
- **Evidence:**
  - All triggers share `McpKeyId: "ai-trigger"` (AiTriggerDispatcher.cs:110). Both actor builders prefer McpKeyId over DisplayName (AgentToolInvoker.cs:96, 223; ApprovalCoordinator.cs:126), so the trigger name is lost in Agent History and in the AuditLog.
  - Agent actions are recorded with ActorType `web` (IAuditLogService.cs:29-31).
  - Key identity appears as the Name in the AuditLog, `key:<Name>` in the call log (McpCallLogMiddleware.cs:150) and `key:<Id>` in approvals (DirectMcpCallGuard.cs:174). Names are mutable and not unique.
- **Recommendation:**
  - Use one structured identity everywhere: kind, stable ID, display name, plus an on-behalf-of chain (human → agent → tool).
  - Give each trigger its own ID.
  - Add an ActorId column.

### C-13: Direct-call Agent History rows are unreliable (Medium, Operations, v1-blocker: no)
- **Evidence:**
  - `success = StatusCode < 400` (McpCallLogMiddleware.cs:57), but tool denials and errors are returned as normal strings with HTTP 200.
  - The verdict defaults to "allow" when the filter did not run (:170-171).
  - Cookie callers that CheckAccess denies are logged as allow and success.
  - Batched calls share one verdict.
  - There is no ResultSummary or correlation ID, and the request body is fully buffered without a size cap (:69-71).
  - Unauthorized requests are short-circuited by UseAuthorization (WhiskersPipelineExtensions.cs:151-152) before this middleware (:251), so they are not recorded.
- **Recommendation:** Record inside the MCP call filter, where the real `CallToolResult` (IsError) and principal are available. Make tool denials return `IsError=true`. Add a correlation ID and log auth failures.

### C-14: Guardrails silently disappear when the Agent module is off (Medium, Security/UX, v1-blocker: no)
- **Evidence:** McpGuardrailFilter.cs:29 (`guard is null` passes through), as documented at :17-18 and AgentModule.cs:16-21.
- **Recommendation:** Move the guardrail engine, store and direct guard into Core. At minimum, show a startup warning and a Settings banner, and make the read-only kill switch independent of the module.

### C-15: ForbiddenArgPatterns is bypassable and fails open (Medium, Security, v1-blocker: no)
- **Evidence:**
  - Defaults such as `rm\s+-rf\s+/` (GuardrailPolicy.cs:47-50) miss `rm -fr /`, `rm -r -f /`, `find / -delete` and base64 or `$IFS` tricks.
  - An invalid regex or a timeout returns false, meaning allow (BuiltInGuardrailRules.cs:166-172).
- **Recommendation:** Treat a regex timeout or error as Deny (or Confirm). Validate patterns on save. Document the denylist as best-effort and require Confirm for every shell/SQL tool by default.

### C-16: Key AllowedTools bypasses the level check (Medium, Security, v1-blocker: no)
- **Evidence:** McpPermissionService.cs:177-178 (and :202-205) return `AllowedTools.Contains` without checking the level. A "read" key can therefore hold admin tools. `PrincipalCeilingRule` also checks the level (BuiltInGuardrailRules.cs:46-50), but only while the Agent module is on (C-14).
- **Recommendation:** Require both a whitelist hit and a sufficient level, or derive the key's level from the maximum of its allowed tools and show that in the UI.

### C-17: Audit data has no tamper evidence, fixed retention and broad visibility (Medium, Commercial readiness, v1-blocker: no)
- **Evidence:**
  - Plain EF rows (AuditLogService.cs:20-34; McpCallLogStore.cs:34-47).
  - Hard-coded 90-day `ExecuteDelete` (MetricsCollectorService.cs:277-280).
  - The `audit-log` and `agent-history` pages are Viewer-visible (AllInOnePseudoModule.cs:57, 71) and show redacted params and result summaries for all users (AgentCallDetailDialog.razor:46).
  - No export or SIEM forwarding was found.
- **Recommendation:** Make retention configurable. Add a hash-chained (or append-only) audit table with periodic signed checkpoints. Provide syslog/OTLP/SIEM export and restrict detail views to Operator and above.

### C-18: Resolver keyed by non-unique provider ToolCallId (Low, Security, v1-blocker: no)
- **Evidence:** AgentSession.cs:59-63, 144; ApprovalCoordinator.cs:67; Gemini IDs `call_{n}` reset every turn (GeminiWire.cs:158); fallback IDs `call_{i}`/`toolu_{i}` (OpenAiWire.cs:170; AnthropicWire.cs:178). A stale pending approval could resolve a later call that reuses the same ID. This is unlikely with the current UI flows.
- **Recommendation:** Key `_pending` and the resolver by `CorrelationId`, or by approval ID together with ToolCallId.

### C-19: Rate limits per session only; pending approvals unbounded (Low, Reliability, v1-blocker: no)
- **Evidence:**
  - `MaxActionsPerSession` is per session (AgentSession.cs:168), and `instruct_agent` creates a new session per call (AgentTools.cs:42-45).
  - Each distinct direct-call argument set raises an approval and a notification (DirectMcpCallGuard.cs:102-146).
  - `TrimResolved` never trims pending entries (ApprovalStore.cs:182-191).
- **Recommendation:** Add per-principal rate limits and a cap on pending approvals per actor.

### C-20: ClaudeCodeRuntime (Low, Security/Architecture, v1-blocker: no)
- **Evidence:**
  - There are no callers apart from DI registration (AgentModule.cs:90-91).
  - When `keyConfig == null` (invalid or unknown key) the rights check is skipped (ClaudeCodeRuntime.cs:53-58).
  - The CLI acts under the configured key, so its actions are attributed to the key and not to the caller.
- **Recommendation:** Remove it, or fail closed and mint a short-lived per-caller key.

### C-21: Transcripts not fully redacted (Low, Security, v1-blocker: no)
- **Evidence:** AgentTranscriptStore.cs:80-81 redacts Tool-role text only. User and assistant text, and `ToolCalls[].ArgumentsJson`, are persisted as-is.
- **Recommendation:** Redact every text field and the tool arguments before saving.

### C-22: Notification context (Low, UX, v1-blocker: no)
- **Evidence:** ApprovalCoordinator.cs:72-77 and DirectMcpCallGuard.cs:142-146 include no approval ID, target, risk or deep link.
- **Recommendation:** Include the approval ID, risk, target and a link to `/approvals#id`. Keep the arguments out of external channels.

### C-23: Test gaps (Low, Testing, v1-blocker: no)
- **Evidence:** There are no tests for:
  - trigger preset pinning (C-01);
  - protected resource matched by container ID (C-02);
  - resolve-after-expiry (C-07);
  - Enabled-toggle enforcement for the agent (C-05);
  - audit rows for approvals (C-04).
- Existing coverage: ApprovalStoreTests, GuardrailEngineTests, DirectMcpCallGuardTests, AgentSessionTests (src/Whiskers.Tests).

### C-24: Settings SaveMcpPermissions lacks an in-method admin check (Low, Security, v1-blocker: no)
- **Evidence:** Settings.razor:1038-1060. The sibling methods do check (`HasRoleAsync(Admin)` at :1004, :1019), but this one relies only on the page render gate (:52).
- **Recommendation:** Add the same guard for defence in depth.

---

## Positive controls
- The guardrail engine is deterministic, stateless and most-restrictive-wins, and it re-checks at execution time against the live policy (GuardrailEngine.cs; AgentToolInvoker.cs:61-66).
- On the agent path the approved call is the same immutable object that executes; nothing is supplied again (AgentSession.cs:143-175).
- Direct-call approvals are bound to SHA-256(actor, tool, args), single-use and time-limited (DirectMcpCallGuard.cs:86-106, 164-171).
- Exactly-once resolution through Interlocked CAS makes double-click on Approve safe (ApprovalStore.cs:129).
- An approver's level must be ≥ the action's level (ApprovalStore.cs:120-126).
- PrincipalCeilingRule caps the agent at the caller's level and whitelist. The synthetic agent context carries the real level, not admin (BuiltInGuardrailRules.cs:34-54; McpPermissionCheck.cs:56-57).
- Trigger and `instruct_agent` Confirms are auto-denied, never auto-approved (AiTriggerDispatcher.cs:122-127; AgentTools.cs:59-65).
- `instruct_agent` is excluded from the agent's own tool set (AgentToolRegistry.cs:28-29, 62).
- Guardrail and trigger stores refuse non-admin editors (GuardrailStore.cs:135-140; AiTriggerStore.cs:55-58), and the code floor stops stored tool levels being looser than code (McpPermissionService.cs:252-266).
- MCP keys are stored as SHA-256 only, compared in constant time and generated with a CSPRNG. Revocation is immediate (McpPermissionService.cs:99-144, 306-364).
- Secrets are redacted before persistence in the call log, the approvals and the audit commands, and redaction fails closed on regex timeout (SecretRedactor.cs:61-84).
- The direct guardrail filter fails closed when the caller cannot be identified (McpGuardrailFilter.cs:33-36).
- All 86 MCP tool methods call CheckAccess with matching names (scripted check). Unknown tools default to admin.
- One active run per session is enforced and history access is locked (AgentSession.cs:70-83).
