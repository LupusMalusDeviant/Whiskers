# Contract 0014 — Structured principals on every path (INV-01)

Status: **Draft** · 10 November 2026. Not accepted yet; the decisions D1–D9 and the open questions Q1–Q7 at the end
need the maintainer. Nothing in this contract is implemented.

## Purpose

INV-01 (`docs/v1/SECURITY_INVARIANTS.md` §1) says:

> Every mutation is attributed to exactly one structured principal `{kind ∈ Human, ApiKey, Agent, Automation, Webhook,
> System; stable id; credential id; on-behalf-of principal; session id}`. Display strings are derived from it, never the
> other way round.

R1-03 made the gateway's principals structured for keys, agents and AI triggers. INV-01 is still ⚠️ because
(`docs/v1/RELEASE_GATE.md`, *Open invariants*): "webhook and system principals come with their packages; humans are
keyed by the account handle, not the credential". This contract closes those three gaps and the smaller ones found while
verifying them, so that INV-01 can be set to ✅ with tests that decide it.

The contract does **not** change who may do what. Every authority check (levels, owners, ceilings, PC-D1 rules,
approvals, separation of duties) decides exactly as today; only the record of *who* acts becomes complete.

## Current state (verified at `9a12f7a3`)

### The model is there, two kinds are never built

- `MutationPrincipalKind { Human, ApiKey, Agent, Automation, Webhook, System }` and the record
  `MutationPrincipal(Kind, Id, DisplayName, PermissionLevel, CredentialId, OnBehalfOf, AllowedTools, SessionId,
  DelegatedBy)` exist (`Services/Mutations/MutationModels.cs:33`, `:45-54`). The audit log has a column for every field
  (`Whiskers.Data/Entities/AuditLogEntry.cs:33-44`), written by `MutationAudit` (`Services/Mutations/MutationAudit.cs:293-300`).
- Every `new MutationPrincipal(` in production code is in `AutomationMutationRunner.cs:170,178,185,290,365,368,372,382`,
  `PrincipalIdentity.cs:84,87,89,99` and `UiMutationRunner.cs:180-181`. **None builds `Webhook` or `System`.**
- `MutationSurface.Webhook` and `MutationSurface.Terminal` are declared (`MutationModels.cs:57`) but no request uses them;
  the only reference is the origin mapping `MutationPolicy.cs:168`.

### Webhooks run as automations

- A webhook delivery is an `AutomationActor("webhook:" + id, …, owner, owner)` (`Services/Webhooks/WebhookService.cs:1198-1202`,
  also `:1407` for a removed webhook) and goes through `IAutomationMutationRunner`, so its principal is
  `Kind = Automation`, `Surface = Automation` (`AutomationMutationRunner.cs:262`, `:293`, `:372`).
- What authenticated the delivery — the webhook's signing secret, checked by HMAC (INV-42, R1-10, R1-31) — is not named
  on the principal: `CredentialId` stays null for every automation (`AutomationMutationRunner.cs:365-382`).
- Three decisions key on `Surface == Automation` / `Kind == Automation` and would change if webhooks simply got their own
  kind: the policy engine choice (`MutationPolicy.cs:93-99`), "nobody is there to confirm" (`MutationGateway.cs:972-973`),
  and the planned-deployment arguments only Whiskers may set (`MutationTargets.cs:262`).

### Automation ids are stable, but outside the scheme

- `PrincipalIds` knows `user:`, `key:`, `trigger:`, `agent:` and `anonymous` (`Services/Mutations/PrincipalIdentity.cs:12-69`).
  The doc comment of `MutationPrincipal.Id` also names `task:` (`MutationModels.cs:35-36`), which `PrincipalIds` does not
  know.
- The automation runners use their own ids: `task:<id>` (`Services/Scheduler/TaskExecutor.cs:495-497`), `check:<id>`
  (`Services/EndpointChecks/EndpointCheckRunner.cs:65-69`), `gitdeploy:<id>` (`Services/GitDeploy/GitDeployOwners.cs:12-16`),
  `webhook:<id>` (above), `autoupdate` (`Services/AutoUpdate/AutoUpdateService.cs:216`, `:371`), `autopatch` and
  `autopatch-preview` (`Services/AutoPatch/AutoPatchService.cs:210`, `:359`, `:403`), `vpn-bootstrap`
  (`Services/Vpn/VpnBootstrapHostedService.cs:23`), and `unowned:<runner>` for an item without an owner
  (`AutomationMutationRunner.cs:290`, `:382`). `AutomationActor` is a public record anyone can construct with any string
  (`AutomationMutationRunner.cs:26`).

### Humans are keyed by the account handle, without credential or session

- A person in the UI is `new MutationPrincipal(Human, PrincipalIds.User(email), email, level)` — no `CredentialId`, no
  `SessionId` (`UiMutationRunner.cs:173-183`). The web agent's user is built the same way (`Services/Agent/AgentPrincipalResolver.cs:39-46`).
- The session already carries what identifies the credential: a federated session has the binding id
  (`AuthConstants.BindingIdClaim = "whiskers:binding"`, `Services/Auth/AuthConstants.cs:31`, issued in
  `Services/Auth/FederatedSignIn.cs:53-57`), a local session the Identity user id and its security stamp
  (`Startup/WhiskersPipelineExtensions.cs:324-335`), both re-checked per request (`Services/Auth/SessionValidator.cs:69-73`,
  `:93`, `:106-118`). **No claim identifies the session itself**; the cookie lives 24 h
  (`Startup/WhiskersAuthenticationExtensions.cs:58`).
- With `Auth:Disabled` the user is the synthetic `local@serverwatch.local` (`Services/Auth/CurrentUserService.cs:18`,
  `AgentPrincipalResolver.cs:39-41`).
- Contract 0003 fixed that "the account handle (the configured address) stays the key of roles, owners, approvals and
  audit" (`Services/Auth/IIdentityBindingStore.cs:9-13`). The R1-03 evidence explains why `CredentialId` was left empty
  for humans: it is part of the approval binding (`Services/Approvals/ApprovalBinding.cs:31-33`), of the approve-then-retry
  actor key (`MutationGateway.cs:897`) and of the audit details' `keyId` (`MutationAudit.cs:330-338`).
- When an agent acts for a user, the user's `SessionId` is overwritten with the agent session
  (`PrincipalIdentity.cs:98`); the user's own web session is not kept.

### Audit rows written outside the gateway carry strings only

`IAuditLogService.LogAsync` / `TryLogAsync` / `TryLogAtAsync` take `string actor, string actorType`
(`Services/AuditLog/IAuditLogService.cs:7-21`) and leave every principal column null. Rows written that way today:

| Row | Where | Actor today |
|---|---|---|
| `audit.retention` | `Services/AuditLog/AuditChain.cs:925-933` | `"system"` |
| `webhook.suspended` (authority lost) | `WebhookService.cs:1514` | `"system"` |
| `terminal.close` | `Services/Terminal/TerminalSessionManager.cs:205` | the owner's **display name**; the id only in details (`:199`) |
| `agent.run` (trigger ran / refused before its session) | `Services/Agent/Triggers/AiTriggerDispatcher.cs:279`, `:290` | `trigger:<id>`, kind not recorded |
| `auth.identity_bound` / pending | `Services/Auth/IdentityBindingStore.cs:261-334`, `:457-470` | the account address / a CLI label |
| first administrator | `Services/Setup/SetupStateService.cs:151`, `:188` | the address, type `setup` |
| restore applied at boot | `Services/Backup/RestoreBootHandler.cs:114-115` | a stored string, `(unknown)` if none |
| recovery code issued (host CLI) | `Services/Auth/LocalAccountRecoveryCli.cs:119-151` | a CLI label |
| recovery code redeemed / refused | `Services/Auth/LocalAccountService.cs:348`, `:394`, `:444` | the address or `anonymous` |
| self-backup download, audit export | `WhiskersPipelineExtensions.cs:416-492` | `GetActorFromHttpContext` (`IAuditLogService.cs:34-45`): email or key **name** |

Approval expiry and cancellation are recorded as decided by `"system"` (`Services/Approvals/ApprovalStore.cs:351-397`).

### Display strings used as identity

- The pending-approval quota counts a request without requesters against its **displayed actor**
  (`ApprovalStore.cs:159`, `:206`). The gateway always passes requesters, so this is a fallback, but it is the one place
  where a display string decides something.
- `AuditLogEntity.Actor` is documented as "email or API key name" (`AuditLogEntry.cs:7-8`) — a display value.

### Tests today

`PrincipalPropagationTests` (named by INV-01) covers Human → Agent → Automation, ApiKey → Agent across a rename, two
triggers with one name, the anonymous principal and the legacy owner migration (`src/Whiskers.Tests/PrincipalPropagationTests.cs:38-241`).
Nothing checks the webhook or system kinds, a human's credential or session, or the principal columns of rows written
outside the gateway.

## Boundaries (fixed)

- **No authority changes.** Levels, owners and their re-check (INV-04), ceilings (INV-02), the PC-D1 rules, approvals and
  separation of duties (INV-30–37) decide exactly as today. Where a decision keys on the kind or surface, it is widened so
  that the new kinds decide as the old ones did (D3), and a test pins that.
- **The account handle stays the human's id** (contract 0003): roles, owners, requesters and approvers keep
  `user:<email>`. The credential and the session become fields beside it, never a replacement.
- **The audit trail is append-only and signed** (R1-23). Rows written before the upgrade are not rewritten or backfilled;
  only new rows carry the new columns.
- **No change to the auth middleware or its order**, the OAuth whitelist, the mTLS/WireGuard layer or the socket-proxy
  verbs. D2 adds one claim where the two sign-in handlers already build the session's claims; that is sign-in code, not
  middleware, and it is listed as such in the plan.
- **No secret in any id.** A credential id names a credential (a key id, a binding id, a local account id, a webhook
  secret's version), never a value derived from the secret.
- **Additive storage only.** The audit log already has every principal column. The one new column is the webhook
  secret's generation (D4), additive in both migration assemblies (ADR-0004).

## Types and interfaces

### Principal id scheme (`PrincipalIds`, additive)

| Kind | `Id` | `CredentialId` | `OnBehalfOf` | `SessionId` |
|---|---|---|---|---|
| Human | `user:<email>` (unchanged) | `binding:<bindingId>` (federated), `local:<identityUserId>` (local password), `auth-disabled` | — | the web session `whiskers:sid` (D2) |
| ApiKey | `key:<keyId>` (unchanged) | `<keyId>` (unchanged) | — | the MCP session id when the transport has one, else null (D5) |
| Agent | `agent:<sessionId>` (unchanged) | the source's (unchanged) | the Human, ApiKey or trigger it acts for | the agent session (unchanged) |
| Automation | `task:<id>`, `check:<id>`, `gitdeploy:<id>`, `trigger:<id>`, `autoupdate`, `autopatch`, `autopatch-preview`, `vpn-bootstrap`; `unowned:<runner>` for an item without an owner | null — no credential authenticates an unattended run | the owner (Human or ApiKey), or none | the run's operation id when it has one, else null |
| Webhook | `webhook:<id>` | `webhook-secret:<id>@<version>` — the secret generation the HMAC was checked against (D4) | the webhook's owner | the delivery's run id |
| System | `system:<component>` from a closed list (D6) | null | none | `ProcessIdentity.Current` (`Services/Mutations/ProcessIdentity.cs:11`) |

```csharp
public static class PrincipalIds
{
    // existing: UserPrefix, KeyPrefix, TriggerPrefix, AgentPrefix, Anonymous, User/Key/Trigger/Agent, EmailOf, …
    public const string TaskPrefix = "task:", CheckPrefix = "check:", GitDeployPrefix = "gitdeploy:",
        WebhookPrefix = "webhook:", UnownedPrefix = "unowned:", SystemPrefix = "system:";
    public static readonly IReadOnlySet<string> AutomationSingletons; // autoupdate, autopatch, autopatch-preview, vpn-bootstrap
    public static readonly IReadOnlySet<string> SystemComponents;     // D6

    public static string Task(string id); public static string Check(string id); public static string GitDeploy(string id);
    public static string Webhook(string id); public static string System(string component); // throws outside SystemComponents

    /// <summary>The kind an id belongs to, or null for an id outside the scheme.</summary>
    public static MutationPrincipalKind? KindOf(string? id);
}
```

### Credential ids (new, `Services/Mutations/PrincipalCredentials.cs`)

```csharp
public static class PrincipalCredentials
{
    public static string Binding(string bindingId);         // "binding:<id>"
    public static string LocalAccount(string userId);       // "local:<id>"
    public const string AuthDisabled = "auth-disabled";
    public static string WebhookSecret(string webhookId, int version); // "webhook-secret:<id>@<version>"
}
```

### Automation actors are built by factories only

`AutomationActor` keeps its shape; its constructor becomes `internal` and every runner uses a named factory
(`AutomationActor.ForTask`, `ForCheck`, `ForGitDeploy`, `ForWebhook`, `ForAutoUpdate`, `ForAutoPatch`,
`ForAutoPatchPreview`, `ForVpnBootstrap`), so a runner id outside the scheme cannot be written. The existing ids are kept
byte for byte (D1), so audit history, suspensions and requesters stay continuous.

### Web session id (D2)

`AuthConstants.SessionIdClaim = "whiskers:sid"`: 128 random bits, base64url, issued by both sign-in handlers
(`WhiskersPipelineExtensions.cs:324-335`, `FederatedSignIn.cs:53-57`) and by the `Auth:Disabled` identity.
`ICurrentUserService` gets `Task<(string? CredentialId, string? SessionId)> GetPrincipalFieldsAsync()`; `UiMutationRunner`
and `AgentPrincipalResolver` fill the two fields from it. `AgentPrincipal` gets two optional fields
`CredentialId` and `WebSessionId` (`Models/Agent/AgentRuntime.cs:10-17`, additive), and `MutationPrincipals.Agent` keeps the
user's web session on `OnBehalfOf` instead of overwriting it with the agent session (`PrincipalIdentity.cs:98`).

### Webhook principal (D3, D4)

`IAutomationMutationRunner` gets a webhook entry point, or `AutomationActor` a `Kind` (decided in implementation, either
satisfies this contract):

```csharp
public sealed record AutomationActor(...)
{
    /// <summary>Automation (default) or Webhook. Only AutomationActor.ForWebhook sets Webhook.</summary>
    public MutationPrincipalKind Kind { get; init; } = MutationPrincipalKind.Automation;
    /// <summary>For a webhook delivery: PrincipalCredentials.WebhookSecret(id, version).</summary>
    public string? CredentialId { get; init; }
}
```

A webhook run's request is `Surface = Webhook`, principal `Kind = Webhook`, `Id = webhook:<id>`, `OnBehalfOf = owner`.
The three places that key on automation (`MutationPolicy.cs:93-99`, `MutationGateway.cs:972-973`, `MutationTargets.cs:262`)
read one predicate instead:

```csharp
internal static class Unattended
{
    /// <summary>An automation or a webhook delivery: nobody is there to confirm; PC-D1 rules when owned by a person.</summary>
    public static bool Is(MutationRequest r) =>
        r.Surface is MutationSurface.Automation or MutationSurface.Webhook || r.TriggerId is not null;
    public static bool IsUnattendedPrincipal(MutationPrincipal p) =>
        p.Kind is MutationPrincipalKind.Automation or MutationPrincipalKind.Webhook;
}
```

### Audit rows outside the gateway

```csharp
public interface IAuditLogService
{
    /// <summary>Writes a row with every principal column (INV-01). The display actor is derived from the principal.</summary>
    Task<bool> TryLogAsync(MutationPrincipal principal, string action, string targetType, string targetId,
        string targetName, string? details = null, string? serverId = null, bool success = true,
        DateTime? occurredUtc = null);

    [Obsolete("INV-01: pass a MutationPrincipal")] Task LogAsync(string actor, string actorType, …);   // kept for the
    [Obsolete("INV-01: pass a MutationPrincipal")] Task<bool> TryLogAsync(string actor, string actorType, …); // MCP tools'
}                                                                                                      // enrichment
```

`ActorType` and `Actor` become derived: `Actor = principal.DisplayName`, `ActorType` from the kind and surface as the
gateway already does (`MutationAudit.cs:280`). `IToolAuditLog` (the MCP tools' enrichment of the gateway event, R1-20) is
not affected: it writes no row of its own.

## Decisions (D1–D9, recommendation first)

- **D1 — Automation ids.** Keep the existing runner ids byte for byte and register them in `PrincipalIds` as a closed
  set; make `AutomationActor` constructible only through factories. *Alternative:* rename to one uniform
  `automation:<kind>:<id>` scheme — cleaner, but every stored owner, suspension, requester and audit query would see a
  new id, and old rows could not be joined with new ones without a mapping.
- **D2 — Human session id.** A new claim `whiskers:sid` (random, per sign-in) carried as the human's `SessionId`. A cookie
  issued before the upgrade has no such claim: its UI mutations are **refused once with "sign in again"** and the session
  is ended, exactly as R1-26 did for cookies without a security stamp (`SessionRevocationReasons.StampMismatch`); the
  cookie lives at most 24 h anyway. *Alternative:* accept such a cookie with `SessionId = null` until it expires; INV-01
  would then not hold for up to 24 h after the upgrade, and the gate test would need an exception for "legacy session".
- **D3 — Webhook kind.** Webhook deliveries become `Kind = Webhook`, `Surface = Webhook`, and every decision that keyed
  on automation reads the `Unattended` predicate, so policy, confirmation and planned-argument checks decide exactly as
  today (pinned by running the existing webhook suites unchanged plus one equivalence test). *Alternative:* keep them as
  `Automation` and amend INV-01's wording to drop `Webhook` as a kind of its own; that needs the invariant text and
  `PRODUCT_CONTRACT.md` §3 changed and accepted again.
- **D4 — Webhook credential id.** `webhook-secret:<id>@<version>`, where the version is the secret's generation, raised
  by every regeneration (`regenerate secret`, `rotate_git_deploy_webhook_secret`). `WebhookEntity` has no such field today
  (`Whiskers.Data/Entities/UpdatePolicy.cs`, class `WebhookEntity`: `Secret`, `Owner`, no version); it gets
  `SecretVersion` (int, existing rows 1), one additive migration in each provider assembly (ADR-0004). *Alternative:* `webhook-secret:<id>` without a generation — the
  audit then cannot tell which secret a delivery was signed with across a rotation.
- **D5 — API key session.** The key's principal gets the MCP transport session id (`Mcp-Session-Id`, already recorded as
  the request's `SessionId`) as its `SessionId` when the transport has one; a stateless call has none and says so
  (`null` is "no session", documented). *Alternative:* leave the principal's session empty for keys and rely on the
  request's `SessionId` column — then "session id" means two different things depending on the kind.
- **D6 — System principals.** `System` is used for exactly these components, each with its own id: `system:audit-retention`
  (S-08), `system:startup` (start-up migrations, pre-upgrade backup S-09, restore applied at boot), `system:approval-expiry`
  (expiry and cancellation of approvals), `system:authority-check` (switching off an automation whose owner lost its
  authority, e.g. `webhook.suspended`), `system:host-cli:<command>` for the host CLIs (S-10, S-11, S-15). A closed list,
  pinned by a test; a new component needs a contract change. *Alternative:* a separate kind `HostOperator` for the CLIs —
  more precise, but a seventh kind that INV-01 does not list.
- **D7 — Rows outside the gateway.** Every audit row Whiskers writes carries `PrincipalKind` and `PrincipalId` (and the
  other columns where the table above defines them), including reads that are recorded (self-backup download, audit
  export) and the sign-in rows of contract 0003; the string overloads are obsolete and allowed only in `ToolAuditLog`.
  *Alternative:* only rows of mutations by the §3 definition; the reads would keep strings, and the gate test would need
  a list of exempt actions.
- **D8 — Display strings never decide.** The pending-approval quota counts a request without requesters as refused
  (fail closed) instead of against its displayed actor (`ApprovalStore.cs:159`, `:206`); `AuditLogEntity.Actor` is
  documented as "display, derived from the principal". *Alternative:* count it under `anonymous`.
- **D9 — Human credential id where it is bound.** Fill `CredentialId` for humans and keep it in the approval binding
  (`ApprovalBinding.cs:31`) and the approve-then-retry key (`MutationGateway.cs:897`): an approval is then usable only by
  the same sign-in credential that requested it. Approvals pending at the upgrade become unusable (fail closed; they
  expire as usual) — named in the CHANGELOG. The audit details get a `credential` field; `keyId` stays for keys only.
  *Alternative:* record the credential in the audit only and leave it out of the binding and the retry key — no pending
  approval is lost, but an approval could then be used from another credential of the same person.

## Tests (acceptance)

- **`PrincipalCompletenessTests`** (new): for each kind, a principal built by its production factory has every field the
  scheme table requires and none it forbids; `PrincipalIds.KindOf` maps every factory's id back to its kind; no id is
  derived from a display name (renaming a key, a task, a check, a webhook, a git-deploy app keeps the id).
- **`AutomationActorSchemeTests`** (new): reads the source — `new AutomationActor(` appears only inside the factories; each
  factory's `RunnerId` is in the scheme; the ids equal today's strings byte for byte.
- **`WebhookPrincipalTests`** (new): a signed delivery is one gateway event with `PrincipalKind = Webhook`,
  `Surface = Webhook`, `PrincipalId = webhook:<id>`, `CredentialId = webhook-secret:<id>@<version>`, `OnBehalfOf` = owner;
  after a secret rotation the version differs; **equivalence:** for each action (restart, rebuild, compose deploy, git
  deploy) the policy engine, the unattended rule and the planned-argument check decide as before
  (`Unattended.Is` is true; `MutationTargets` accepts Whiskers' own planned arguments from a webhook surface).
  The existing `WebhookMutationTests`, `WebhookReplayTests` and `AuditPathMatrixTests` pass unchanged except for the
  expected surface/kind strings, which change exactly from `Automation` to `Webhook` for webhook rows.
- **`HumanPrincipalTests`** (new): a local sign-in yields `local:<userId>` and a session id; a federated one
  `binding:<bindingId>`; `Auth:Disabled` yields `auth-disabled`; two sign-ins of one person have different session ids and
  the same `Id`; a cookie without `whiskers:sid` is refused and ended (D2); an agent acting for a user keeps the user's
  web session on `OnBehalfOf` and its own session on itself.
- **`ApprovalCredentialBindingTests`** (new, D9): an approval requested under one credential is not usable by a call of the
  same person under another; separation of duties still compares `user:<email>` (the requester cannot approve their own
  non-interactive request from a second sign-in method).
- **`AuditPrincipalColumnsTests`** (new, D7): every row-writing path in the table above writes `PrincipalKind` and
  `PrincipalId` (driven per path against a real `AuditLog`); a source check fails any use of the obsolete string
  overloads outside `ToolAuditLog`; `terminal.close` names the owner's id, not only its display name.
- **`SystemPrincipalTests`** (new, D6): every `system:` id used in the source is in `PrincipalIds.SystemComponents`.
- `PrincipalPropagationTests.Human_to_agent_to_automation_keeps_user_and_session_identity` is updated for D2 (the user's
  web session survives the agent hop); its other tests stay.

## Gate change

- `SECURITY_INVARIANTS.md` INV-01: status ✅; the evidence names this contract and drops "Open: …"; the test column names
  `PrincipalPropagationTests`, `PrincipalCompletenessTests`, `AutomationActorSchemeTests`, `WebhookPrincipalTests`,
  `HumanPrincipalTests`, `ApprovalCredentialBindingTests`, `AuditPrincipalColumnsTests`, `SystemPrincipalTests`.
- §11 counts: ✅ 55 → 56, ⚠️ 7 → 6 (INV-01 leaves the list).
- `RELEASE_GATE.md`: the INV-01 row leaves *Open invariants*.
- `.github/required-tests/in-process.txt`: the generated block regenerated from the `.actual` file the gate writes.
- `PRODUCT_CONTRACT.md` §3 (principal kinds): the System row names the closed component list (D6); the Webhook row stays.
  §7 rows S-04 and S-05 name the webhook principal.
- CHANGELOG (Unreleased): behaviour changes — sessions from before the upgrade sign in again (D2); approvals pending at
  the upgrade become unusable (D9); webhook rows in the audit log say `Webhook` instead of `Automation`.

## Plan (steps and triage)

1. [Claude-prüfbar] `PrincipalIds` scheme, `PrincipalCredentials`, `AutomationActor` factories; `AutomationActorSchemeTests`,
   `PrincipalCompletenessTests` (automation, key, agent part).
2. [Claude-prüfbar] Webhook kind and surface with the `Unattended` predicate; `WebhookEntity.SecretVersion` (additive
   migration, both assemblies); `WebhookPrincipalTests` and the existing webhook suites.
3. [Claude-prüfbar] System principals and the principal overload of `IAuditLogService`; move every row of the table
   above to it; `AuditPrincipalColumnsTests`, `SystemPrincipalTests`.
4. [Claude-prüfbar, **touches sign-in code** — needs the maintainer's go for D2] the `whiskers:sid` claim in the local and
   the federated sign-in handler and for `Auth:Disabled`; the refusal of cookies without it in `SessionValidator`; the
   human credential and session in `UiMutationRunner` and `AgentPrincipalResolver`; `HumanPrincipalTests`.
5. [Claude-prüfbar] D9 in the approval binding, the retry key and the audit details; `ApprovalCredentialBindingTests`;
   the D8 quota change.
6. [Claude-prüfbar] Documentation: INV-01 row, §11, `RELEASE_GATE.md`, `PRODUCT_CONTRACT.md` §3/§7, the Mutations and
   Auth folder READMEs, CHANGELOG; regenerate the required-test manifest; full test run with the lock.
7. [braucht Nutzer] After deployment: sign in once on the live instance (D2) and check in the audit log that a UI action,
   a webhook delivery and the daily retention row carry kind, id, credential and session as the scheme table says.

## Open questions for the maintainer

- **Q1 — Sessions from before the upgrade (D2).** Recommended: end them once (everyone signs in again after the update),
  as R1-26 did. Alternative: let them run without a session id until they expire (at most 24 h); INV-01 then has a known
  gap during that window.
- **Q2 — Webhook as its own kind (D3).** Recommended: yes, `Kind = Webhook` and `Surface = Webhook`, with the
  equivalence pinned by tests. Alternative: keep webhooks as automations and change INV-01's wording (one kind fewer).
- **Q3 — Sign-in code (step 4).** The session claim is added where the two sign-in handlers build the session; the
  middleware and its order stay. Recommended: allow this change as part of the package. Alternative: no change to sign-in
  code; then D2 falls back to Q1's alternative permanently and humans have no session id — INV-01 would need an
  accepted exception for it.
- **Q4 — Approvals and the credential (D9).** Recommended: bind approvals to the requesting credential (an approval asked
  for with the Google sign-in is not usable from a local sign-in of the same person); approvals pending at the upgrade
  expire. Alternative: credential only in the audit, binding unchanged.
- **Q5 — Recorded reads (D7).** Recommended: every audit row gets the structured principal, also backup downloads,
  audit exports and sign-in rows. Alternative: only mutations; reads keep their string actor.
- **Q6 — Host CLIs (D6).** Recommended: `System` with `system:host-cli:<command>`. Alternative: a seventh kind
  `HostOperator`, which changes INV-01's kind list.
- **Q7 — Webhook secret generation (D4).** Recommended: a new additive column `SecretVersion`, so the audit names which
  secret signed a delivery. Alternative: no new column, credential id `webhook-secret:<id>` only.
