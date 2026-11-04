# Contract 0003 — Federated identity binding and fail-closed admission (R1-27)

Status: **Accepted** · 4 November 2026 by the maintainer, including the decisions listed at the end (decisions 5 and 6
as recommended). To be implemented in R1-27.
Nothing is built before acceptance.

## Purpose

A Google or OIDC sign-in is admitted on the **stable subject** the provider vouches for — the pair *(issuer, subject)* —
not on the email claim alone (WORK_PACKAGES R1-27; INV-05, INV-01; TA-20, TA-23; A-06, A-12). The email claim is used
exactly once per provider: to **bind** a new subject to an address an administrator has already configured (a role entry
or a whitelist entry), and only when that email is verified by a provider trusted to verify it, or when the binding is
confirmed explicitly. From then on the subject is the identity; a different subject arriving with the same address is
someone else. An instance without an administrator admits no federated sign-in at all.

## Current state (verified at `58ea9489`)

- `WhiskersAuthenticationExtensions.cs` (OIDC block): `EmailClaim` (default `email`) is mapped to `ClaimTypes.Email`
  and used as `NameClaimType`; the Google handler maps `email`. Nobody reads `email_verified`, `sub` or the issuer.
- `FederatedSignIn.WhitelistGateAsync` (both providers' `OnTicketReceived`) admits on
  `IWhitelistService.IsEmailAllowed(email)` and issues the cookie with the provider's claims unchanged.
- `WhitelistService.IsEmailAllowed`: enabled → listed only; enabled and empty → nobody; **disabled →
  `!HasAnyRoles() || HasExplicitRole(email)`**, i.e. while no role exists every Google/OIDC account in the world is
  admitted as `DefaultRole` (Viewer). The PR #135 CHANGELOG entry documents exactly this. `/mcp` admits any
  authenticated cookie (`WhiskersPipelineExtensions`, `MapMcp(...).RequireAuthorization`), and a Viewer cookie reaches
  the Read-level MCP tools (container logs). The setup redirect only intercepts HTML navigations, so hubs and `/mcp` are
  reachable before setup.
- **Cookie and session validation already fail closed (R1-26).** The spec's "cookie validation catches errors and
  allows access" is stale: `SessionCookieValidation.ValidateAsync` turns any exception into
  `SessionCheck.Revoked(store_unavailable)`, `SessionValidator.CheckAsync` catches every store error the same way, and
  `SessionHubFilter` / `HubSessionSweep` do the same for open hub connections. R1-27 adds a federated branch to that
  check; it does not reopen the error handling.
- Humans are keyed by email everywhere downstream: `roles.json` (`UserRoleEntry.Email`), `whitelist.json`,
  `PrincipalIds.User(email)` (gateway principals, owners of tasks/triggers/webhooks/git apps/AutoUpdate/AutoPatch/VPN,
  approvals' resolver, terminal session owners), `IOwnerAccounts` (`known-local-accounts.json`, `federated` list filled
  by `FederatedSignInRecorder` from any non-local cookie), `McpPermissionCheck.WebUserLevel`, `CurrentUserService`,
  `AuthAuditLog` actors, `LocalAccountService` (last-Admin rule counts "a Google/OIDC user seen signing in").

## Boundaries (fixed)

- **Middleware order is unchanged.** No `Use*`/`Map*` call is added, removed or moved. `/login-local` is unchanged.
  The cookie options and the `OnValidatePrincipal` hookup are unchanged.
- **What changes inside the protected auth integration — and nothing else there:**
  1. Google options: one added claim action mapping `email_verified` (userinfo) to a claim of that type.
  2. OIDC options: one added claim action mapping `email_verified` from userinfo; `EmailClaim` and `NameClaimType` stay
     as they are.
  3. `FederatedSignIn.WhitelistGateAsync`: its body calls `IFederatedAdmission` and, on admission, replaces the
     ticket's principal by the minimal Whiskers principal (below). Its redirect target and the remote-failure path stay.
  4. `SessionValidator.CheckAsync`: a federated branch (below), in front of the existing whitelist/role read.
- `WhitelistService.IsEmailAllowed` keeps its logic; the "no Admin → nobody federated" rule lives in the admission and
  the session check, so local sign-in, `AutomationOwnerResolver` and the setup wizard are unaffected.
- Only additive data changes: one new JSON file in the data directory (`identity-bindings.json`). No change to
  `roles.json`, `whitelist.json`, `known-local-accounts.json`, the Identity schema or the main database; no migration.
- No home-made crypto. Binding ids come from `RandomNumberGenerator`; the subject fingerprint shown to people is the
  first 12 hex digits of SHA-256 over `issuer \n subject` (display only, never compared for authority).
- **Emails and subjects are never logged.** Logs carry binding ids, provider names, reasons and exception types only.
  Audit rows name the account (the configured address) as today; a refused federated sign-in whose address is not a
  configured entry is recorded as `anonymous` (aligned with local sign-in, which never records an unknown account).
- Fail closed: every error on the way to a binding or an admission is a refusal; nothing that cannot be read or written
  grants access. No synthetic principals; the principal issued is always a real binding of a real entry.
- Every Admin write runs through the Mutation Gateway. The automatic binding at sign-in is, like the setup wizard
  (S-13), a designed exemption — nobody exists yet whom the gateway could decide for — and is therefore audited
  **before** it is written; without that row nothing is written and the sign-in is refused.
- PC-D1…PC-D8 are not touched.

## Terms

- **Entry** — an address an administrator configured: an explicit role entry in `roles.json`, or a listing in an
  enabled whitelist. The entry's address is the **account handle**; it stays the key of roles, owners, approvals and
  audit (`user:<handle>`), so nothing downstream is migrated.
- **Binding** — *(issuer, subject) → account handle*, created once, used for every later sign-in.
- **Trusted provider** — a provider whose `email_verified=true` Whiskers accepts as proof for binding (per-provider
  flag, defaults in Decision 3).
- **Configured instance** — at least one explicit Admin role exists (the same truth as `ISetupStateService`).

## Interfaces

`Whiskers.Services.Auth.IFederatedAdmission` (singleton):

| Member | Called by | Result |
|---|---|---|
| `AdmitAsync(FederatedSignInClaims claims, CancellationToken ct)` | `FederatedSignIn.WhitelistGateAsync` | `AdmissionDecision` |

`FederatedSignInClaims(string Provider, string? Issuer, string? Subject, string? Email, string? EmailVerified,
bool EmailIsStandardClaim)` is extracted by `FederatedSignIn` from the ticket: `Provider` = scheme (`google`/`oidc`);
`Issuer` = `https://accounts.google.com` for Google (constant; the OAuth handler has no token issuer), for OIDC the
`Issuer` of the subject claim as set by the handler from the validated ID token (empty or `LOCAL AUTHORITY` → missing);
`Subject` = the `sub` (Google userinfo `sub`, OIDC `sub` / `NameIdentifier`); `EmailIsStandardClaim` = `EmailClaim` is
`email`. `AdmissionDecision(bool Admitted, string? Account, string? BindingId, string Reason, bool BoundNow)`.

`Whiskers.Services.Auth.IIdentityBindingStore` (singleton, `IInitializable`, `Order` 15 — after the whitelist, before
roles and setup):

| Member | Capability | Level / authority | Notes |
|---|---|---|---|
| `Find(issuer, subject)` | — (read) | internal | exact, ordinal issuer and subject |
| `BindingsOf(account)` / `Snapshot()` | — (read) | Admin (panel), internal | bindings and pending requests, never the raw subject in the UI (fingerprint) |
| `BindAtSignInAsync(...)` | — (designed exemption) | admission only; audit row `auth.identity_bound` first | holds `AccessControlWriteGate` |
| `RecordPendingAsync(...)` | — | admission only | only for addresses that are entries |
| `ConfirmAsync(pendingId)` | `identity.binding_confirm` | Admin, gateway | replaces an older binding of the same account and issuer |
| `RemoveAsync(id)` | `identity.binding_remove` | Admin, gateway | a binding or a pending request; refuses `last_admin` |
| `ConfirmFromHostAsync(account, issuer?)` | — (host) | host CLI; audit `auth.identity_bound` first (actor `cli (--confirm-identity-binding)`) | newest pending request of that account |
| `PruneOrphansAsync(lease)` | — | role / whitelist writers, start | removes bindings and pending requests of addresses that are no longer entries |

Results: `IdentityBindingResult(Success, Error)`, `Error` ∈ `not_authorized`, `session_invalid`, `not_found`,
`last_admin`, `ambiguous`, `store_unavailable`, `audit_unavailable`, `conflict`, `auth_disabled`. Gateway target kind
`identity`, id argument `binding` (the binding/pending id); verification `NotVerifiable` (Whiskers' own record), as for
the other `config.*` writes.

Host CLI: `dotnet Whiskers.dll --confirm-identity-binding <email> [--issuer <issuer>]` — exit codes `0` bound, `2` usage,
`3` no pending request for that address, `4` ambiguous (several issuers pending; prints issuer, time and fingerprint per
line, no email, no subject), `1` nothing changed. Like `--recover-local-account`: inside the running container, writes
its audit row first, never starts the web host.

UI: Settings panel *Sign-in identities* (`IdentityBindingsPanel`, Admin): bindings (account, provider, issuer, subject
fingerprint, how and when bound, last sign-in day) and pending requests (account, provider, issuer, fingerprint, why
pending, whether the provider asserted `email_verified`, expiry) with *Confirm* / *Remove*. `/login?error=unauthorized`
stays the one message for every refusal (no oracle telling a stranger that an address is configured); its text gains
"If you expected access, an administrator can confirm your sign-in under Settings → Sign-in identities."

New constants: `AuthConstants.FederatedScheme = "Federated"` (authentication type of the issued principal),
`AuthConstants.BindingIdClaim = "whiskers:binding"`. `AuthAuditReasons` + `setup_incomplete`, `no_subject`, `no_email`,
`email_unverified`, `provider_untrusted`, `pending_confirmation`, `subject_conflict`, `store_unavailable`,
`audit_unavailable`. `SessionRevocationReasons` + `binding_missing`, `binding_revoked`, `setup_incomplete`. Audit
actions + `auth.identity_bound`, `auth.identity_pending`.

## Data model — `identity-bindings.json` (new, data directory)

```json
{
  "version": 1,
  "bindings": [
    { "id": "b_<32 hex>", "account": "admin@example.com", "provider": "oidc",
      "issuer": "https://auth.example.com/application/o/whiskers/", "subject": "<sub>",
      "method": "verified_email | admin | host_cli", "boundUtc": "…", "boundBy": "sign-in | user:<handle> | cli",
      "lastSignInDay": "2026-11-04" }
  ],
  "pending": [
    { "id": "p_<32 hex>", "account": "admin@example.com", "provider": "oidc", "issuer": "…", "subject": "<sub>",
      "reason": "email_unverified | provider_untrusted | second_issuer | subject_changed | nonstandard_email_claim",
      "emailVerifiedAsserted": false, "requestedUtc": "…", "expiresUtc": "…" }
  ]
}
```

- Unique: *(issuer, subject)* across bindings; *(account, issuer)* across bindings (one binding per provider per
  account). `account` compares without case, `issuer`/`subject` ordinally.
- Pending: one per *(account, issuer)*, newest wins; 24 h lifetime; at most 64 in total (oldest dropped). Created only
  for addresses that are entries — strangers leave no record.
- `lastSignInDay` is written at most once per day per binding.
- Written through `JsonFileStore` (durable, `.bak` generation, R1-35), only under `AccessControlWriteGate`, persisted
  before it is published (as roles and whitelist). Added to `SelfBackupLayout.Files`. A missing file is an empty store;
  a file that cannot be read or parsed (or a directory in its place) makes the store **unavailable**: every federated
  admission and every federated session check refuses (`store_unavailable`), local sign-in is unaffected, the start
  continues, the readiness check reports it.

## Rules

1. **Admission order** (`AdmitAsync`; any exception → refuse `store_unavailable`):
   1. No configured instance (no explicit Admin) → refuse `setup_incomplete` (Decision 1).
   2. Issuer or subject missing → refuse `no_subject`.
   3. A binding for *(issuer, subject)* exists → its account must still be admitted by `IsEmailAllowed(account)` →
      admit, else refuse `not_whitelisted`. The email claim is not consulted (an address changed at the IdP keeps the
      account).
   4. No binding: email missing → refuse `no_email`. The address is not an entry → refuse `not_whitelisted`
      (recorded as `anonymous`).
   5. The address is an entry. The account already has a binding for this issuer (another subject) → pending
      `subject_changed`, refuse `pending_confirmation`. It has bindings only for other issuers → pending
      `second_issuer` (Decision 4).
   6. `EmailIsStandardClaim` is false → pending `nonstandard_email_claim` (`email_verified` vouches only for `email`).
      Provider not trusted → pending `provider_untrusted`. `email_verified` is not exactly `true` (case-insensitive;
      absent, `false`, anything else) → pending `email_unverified`. Each refuses `pending_confirmation`.
   7. Otherwise: write `auth.identity_bound` (method `verified_email`) — no row, refuse `audit_unavailable`; then bind
      under the gate (re-checking 3–6 under it) — a failed save refuses `store_unavailable`; then admit, `BoundNow`.
2. **The issued principal** replaces the provider's: authentication type `Federated`, claims `ClaimTypes.Email` and
   `ClaimTypes.Name` = the account handle, `BindingIdClaim` = the binding id. Nothing else from the IdP is carried
   (tokens saved by `SaveTokens` stay in the ticket properties as today; R1-50's business).
3. **Session check, federated branch** (`SessionValidator`): authentication type `Federated` → no configured instance →
   `setup_incomplete`; no binding id, or a binding that no longer exists → `binding_revoked`
   (`binding_missing` when the claim is absent); binding account ≠ cookie email → `binding_revoked`; then the existing
   whitelist and role read. Any authentication type that is not `LocalPassword`, `AuthDisabled` (in that mode) or
   `Federated` → `binding_missing` (this ends every federated cookie issued before the upgrade). Errors stay
   `store_unavailable`. Hubs, the hub sweep, circuits and `/mcp` inherit this unchanged.
4. **Local and federated stay apart:** a local session is checked against its Identity row and stamp only, never a
   binding; a federated session against its binding only, never an Identity row. Both may hold the same account
   (Decision 5). Removing a binding never ends a local session; changing a local stamp never ends a federated one.
5. **Binding removal and orphans:** when an address stops being an entry (role removed, whitelist save, local-account
   deletion's role removal), its bindings and pending requests are pruned **before** the role/whitelist write, under the
   same held gate; a failed prune refuses the write. So a recycled address never revives an old subject. The start also
   prunes.
6. **Last Admin:** `identity.binding_remove` refuses to remove the last way the last Admin who can sign in has
   (`last_admin`). R1-49's "an Admin who can sign in" becomes: a local account with a password, **or an active
   binding** (replacing "seen signing in"); `LocalAccountService` uses the same predicate.
7. **Owners (R1-04):** unchanged keying. `FederatedSignInRecorder` now only ever sees bound sessions, so the
   `federated` list in `known-local-accounts.json` is fed by bound subjects only; existing entries stay counted, so no
   automation is suspended by the upgrade.
8. **`AUTH_DISABLED`:** no federated scheme exists; the store is loaded but unused; the panel's writes refuse
   `auth_disabled`; the CLI still confirms (usable once authentication is on), like R1-49's recovery CLI.

## Upgrade — what an existing install sees on first start

- Nothing on disk changes; `identity-bindings.json` appears with the first binding. Roles, whitelist, owners, keys,
  local accounts and automations are untouched. MCP keys (bearer) are unaffected.
- **Every Google/OIDC browser session ends at its next request** (`binding_missing`, audited once per account); open
  circuits within 30 s, hub connections within 15 s. Local sessions continue.
- **Google admin:** signs in again; Google sends `email_verified=true` for its accounts → bound and admitted.
  **OIDC admin (e.g. Authentik):** OIDC is not trusted by default (decision 3 b), so the sign-in is refused with the
  generic message and a pending request is recorded; the operator runs
  `docker compose exec whiskers dotnet Whiskers.dll --confirm-identity-binding <address>` once (or a local Admin
  confirms in Settings), then signs in. An operator who has verified that the IdP emits `email_verified` honestly and
  that users cannot change their own address may set `Oidc:TrustEmailVerified=true` to bind automatically.
  **Local admin:** nothing changes.
- An instance **without** an Admin (setup not finished): federated sign-in is refused until the wizard has run; local
  accounts the operator created keep working.
- Restoring a backup from before R1-27 restores no bindings: every federated user binds again as above.
- CHANGELOG `[Unreleased]` carries this list, the CLI line, and replaces the PR #135 sentence "until a role exists every
  Google/OIDC account is admitted as Viewer". `docs/operations/` gets the IdP requirements (`email` scope, users cannot
  change their own address, `email_verified` emitted honestly).

## Files (estimate 16–20, above the spec's 10–14 because of the panel, the CLI and the tests)

`Startup/WhiskersAuthenticationExtensions.cs` (two claim actions), `Services/Auth/FederatedSignIn.cs`,
new `FederatedAdmission.cs` + `IFederatedAdmission.cs`, new `IdentityBindingStore.cs` + `IIdentityBindingStore.cs`,
new `IdentityBindingCli.cs` (+ `Program.cs` dispatch), `SessionValidator.cs`, `ISessionValidator.cs`,
`AuthConstants.cs`, `IAuthAuditLog.cs`, `RoleService.cs` / `WhitelistService.cs` (prune hook), `LocalAccountService.cs`
(predicate), `Configuration/DataPathOptions.cs`, `Services/Backup/SelfBackupLayout.cs`, `CapabilityCatalog` entries,
`Components/Shared/IdentityBindingsPanel.razor` + Settings, both resx files, `Login.razor` text, readiness check,
`Services/Auth/README.md`, `SECURITY_INVARIANTS.md` (INV-05, INV-01 note), `PRODUCT_CONTRACT.md` path matrix (new S-row
"federated binding at sign-in", U-row panel, CLI row), CHANGELOG, operations doc.

## Tests

- **`OidcIdentityBindingTests`** (named in the spec): two subjects / two issuers with one address are two identities —
  the second is pending, never admitted on the first's binding; a bound subject whose IdP address changed keeps its
  account; a local session and a federated session with the same address are validated by different stores
  (removing the binding leaves the local session, a stamp change leaves the federated one); a recycled address after
  prune does not revive the old subject; bindings survive a restart; the issued principal carries the handle, not the
  IdP address.
- **Unverified email:** `email_verified` absent, `false`, `"false"`, `"yes"`, non-standard `EmailClaim`, untrusted
  provider → no binding, no admission, pending only for entries, nothing for strangers; Google and OIDC handlers' real
  claim mapping pinned (the claim type the configured handler produces).
- **Unconfigured instance (boot test, Production, Google + OIDC configured, no Admin):** the gate refuses; a federated
  cookie minted with the app's data protection (with and without binding claim) gets no `/mcp` tool call, no
  `/hubs/containers` negotiate/call; local fail-open path for operator-created accounts unchanged.
- **Migration / fail closed:** empty store + existing entries → bound only via rule 1.7, audit row first; audit failure
  → no binding, refused; store save failure → refused, nothing published; corrupt/directory store → all federated
  refused, local fine, readiness degraded; old cookies → `binding_missing`; CLI exit codes 0/1/2/3/4 and its row-first
  ordering; confirm/remove go through the gateway (`MutationGatewayCoverageTests` pins `BindAtSignInAsync` to
  `FederatedAdmission` and `ConfirmFromHostAsync` to the CLI); `last_admin` refusal; prune-before-write ordering and its
  refusal on failure.
- **Logging:** captured logs of every path contain no address and no subject.
- `EndpointAuthMatrixTests`, `AuthAuditBootTests`, `SessionRevocationTests`, `FederatedSignInTests`,
  `LocalAccountLifecycleTests` updated; required-tests manifest extended.

## Out of scope

- Re-keying principals from `user:<email>` to a subject (owner/approval/audit migration). The handle stays the key;
  INV-01 keeps its ⚠️ for "human credential id": filling `MutationPrincipal.CredentialId` for humans would change the
  approval actor key and the audit details' `keyId` field and belongs with a later principal package.
- Cookie hardening, `SaveTokens`, `RequireHttpsMetadata` (R1-50); MFA; local-account changes beyond the predicate.

## Amendments accepted 4 November 2026

Accepted by the maintainer after review rounds 1 and 2 of R1-27; they tighten the data-model and rule-5 text above.

- **(a) Missing file next to its previous generation.** `identity-bindings.json` missing while
  `identity-bindings.json.bak` exists makes the store **unavailable**, like an unreadable file; the store is empty only
  when neither exists. The `.bak` is never taken over automatically (it may hold a revoked binding); a file that is
  readable again after it was unreadable (repaired, restored from the `.bak`) makes the store wait until a re-read and
  prune have succeeded. Recovery is the host operator's act.
- **(b) Lock file.** Every writer of every process (the running instance and the host CLI) holds an exclusive lock on
  `identity-bindings.json.lock` around its re-read, change and save, and writes a revision into it that readers compare
  (with the file's time and length) to follow changes without a restart. The lock file is not part of the self-backup.
  `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` must not be set.
- **(c) Role and whitelist writes prune first, additions included.** Every role or whitelist write that removes **or
  adds** an entry — the setup wizard's first-admin writes (`SetFirstAdminAsync`, `AddFirstAdminAsync`) included —
  prunes the bindings and pending requests of addresses that are no entries first, under the held gate; a prune that
  cannot be done (store unavailable, save failed) refuses the write. A failed save or a start-up prune that did not
  succeed (unreadable file, lock not taken) leaves the store unavailable until a re-read and prune succeed.
- **(d) Audit log lines.** No application log line of `AuditLogService` names the account of an `auth.*` row (written,
  failed or dropped from the sign-in queue); the identity rows' failure fallback also withholds their details. The
  persisted audit rows keep the account handle.

## Decisions for the maintainer

**Taken on 4 November 2026:** 1 → **a)** refuse federated sign-in until an Admin exists. 2 → **a)** automatic binding
at the first sign-in for a trusted provider with `email_verified=true`, everything else pending. 3 → **b)** Google
trusted, OIDC **not** trusted by default (`Oidc:TrustEmailVerified=false`): every OIDC user, the Authentik admin on
Badwolf included, is pending once after the upgrade and confirmed by an Admin or the host CLI. 4 → **a)** pending, Admin
confirms. 5 and 6 → **a)** as recommended (confirmed with the acceptance of this contract).

1. **Federated sign-in on an instance without an Admin (before setup).**
   - a) Refuse every federated sign-in until an Admin exists; local accounts (operator-made) unchanged. Closes A-12/TA-23;
     the wizard is anonymous + token-gated, so it loses nothing.
   - b) As a), but also refuse local accounts without an explicit role. Purer, risks locking out a pre-C5 local install
     whose roles file is empty.
   - c) Status quo: everyone admitted as Viewer until a role exists (fails the acceptance criterion).
   - **Recommendation: a)** — the hole is federated-only; local accounts exist only by the operator's hand.
2. **How an entry gets bound to a subject (also for existing entries at upgrade).**
   - a) Automatically at the first sign-in when the provider is trusted and `email_verified=true`; everything else
     pending → Admin confirms in Settings or the host CLI confirms.
   - b) Always explicit: every binding needs Admin or CLI confirmation. Strongest, but every existing federated user —
     including a federated-only Admin — is locked out until the operator runs the CLI.
   - c) Automatic for every verified email, no per-provider trust flag.
   - **Recommendation: a)** — the explicit, trustworthy part is the Admin having created the entry plus the operator's
     per-provider trust; after the first bind the subject is frozen, which removes the takeover-by-same-address risk
     for good. b) violates "must not lock out the existing admin" without host access.
3. **Whether `email_verified` is trusted by default (`GoogleAuth:TrustEmailVerified`, `Oidc:TrustEmailVerified`).**
   - a) Google true, OIDC true; operators of IdPs with self-registration or user-editable addresses set OIDC to false
     (documented).
   - b) Google true, OIDC false: every OIDC user (Authentik on Badwolf included) is pending after the upgrade until the
     operator flips the flag or runs the CLI.
   - c) Both false.
   - **Recommendation: a)** — keeps the upgrade lockout-free for Google and honest OIDC IdPs, while unverified addresses
     never bind. Caveat to verify before accepting: some Authentik versions' default `email` scope mapping emits
     `email_verified: true` unconditionally; with a) Whiskers then relies on users not being able to change their own
     address in that Authentik (its "users may change email" setting). If that cannot be assured, choose b) for OIDC.
4. **Same address arriving from a second issuer, or a new subject from the same issuer (IdP account re-created,
   Authority URL changed).**
   - a) Pending; Admin (or CLI) confirms, which adds the second binding or replaces the old one.
   - b) Auto-bind a second issuer when trusted and verified; same-issuer new subject still pending.
   - c) Refuse permanently; the Admin must remove the old binding first.
   - **Recommendation: a)** — a second provider vouching for an address that is already bound is exactly the
     cross-IdP takeover A-06 describes; confirmation is one click. Consequence: an Authority/issuer change re-pends every
     OIDC user (CLI for a federated-only Admin) — stated in the operations doc.
5. **Local account and Google/OIDC account with the same address.**
   - a) They share the entry (role, owners, approvals) as two credentials of one account; each session is still
     validated only by its own credential (rule 4); a federated subject binds to it only by rule 1.
   - b) Separate accounts: entries gain a kind, one address may hold two roles. Contradicts R1-49 decisions 5 and 7
     (deleting a local account removes the shared role) and re-keys owners.
   - **Recommendation: a)** — the acceptance criterion asks that the *identities* be distinguished, which rule 4 and the
     tests do; the entry is the administrator's grant and stays one per address.
6. **`Oidc:EmailClaim` set to something other than `email` (e.g. `preferred_username`, `upn`).**
   - a) Never counts as verified: such users bind only through Admin or CLI confirmation.
   - b) Treated as verified when the provider is trusted.
   - **Recommendation: a)** — `email_verified` vouches for `email` only; a different claim is often user-controlled
     (A-06's Entra example). Affects only installs that changed the default.
