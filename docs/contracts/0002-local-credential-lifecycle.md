# Contract 0002 — Local credential lifecycle (R1-49)

Status: **Proposed** · 3 November 2026, implemented on branch `r1/49` for review. The maintainer accepts or amends it in
the pull request; open decisions are listed at the end.

## Purpose

Local (ASP.NET Identity) accounts become a supported product flow: own password change, Admin add/reset/delete,
recovery without a temporary password, and a published MFA posture (WORK_PACKAGES R1-49; A-11, TA-24; INV-61).

## Boundaries (fixed)

- No change to the protected layers: middleware order, cookie validation (`SessionCookieValidation` →
  `ISessionValidator`), the OAuth/OIDC whitelist logic, `/login-local`, mTLS/WireGuard, the socket-proxy whitelist.
  Session revocation is R1-26's security-stamp check; every credential change changes the stamp.
- ASP.NET Identity does the hashing, the password policy (`IdentityOptions.Password`: 12 characters, all character
  classes), the lockout (5 failures, 15 minutes) and the stamp. No home-made crypto.
- No database change. Recovery codes live in the existing Identity token table (`AspNetUserTokens`).
- Every write by a signed-in user runs through the Mutation Gateway; the service re-checks session and role itself.
- No password, hash or recovery code is logged, audited, put into a gateway argument or stored readable.

## Interfaces

`Whiskers.Services.Auth.ILocalAccountService` (scoped):

| Member | Capability | Level / authority | Result |
|---|---|---|---|
| `ListAsync()` | — (read) | Admin; others get an empty list | `LocalAccountInfo` (email, role, explicit?, state, code expiry) |
| `GetOwnAccountAsync()` | — (read) | valid session | own `LocalAccountInfo` or `null` |
| `ChangeOwnPasswordAsync(current, new)` | `account.password_change` | **read** (every signed-in user, own account only), current password | `LocalAccountResult` |
| `CreateAsync(email, role)` | `account.create` | Admin | result with `RecoveryCode` (invitation) |
| `ResetPasswordAsync(email)` | `account.password_reset` | Admin, not the own account | result with `RecoveryCode` |
| `DeleteAsync(email)` | `account.delete` | Admin, not the own account, not the last Admin who can sign in | result |
| `RedeemRecoveryCodeAsync(email, code, new)` | — (anonymous, like sign-in) | holder of the code | result |

`LocalAccountResult(Success, Error)` with `PolicyErrors`, `RecoveryCode`, `RecoveryExpiresUtc`; `Error` ∈
`LocalAccountErrors` (`not_authorized`, `session_invalid`, `not_found`, `already_exists`, `invalid_email`,
`role_conflict`, `own_account`, `last_admin`, `wrong_password`, `locked_out`, `password_policy`, `invalid_code`,
`store_unavailable`, `conflict`, `auth_disabled`). `IRecoveryAttemptLimiter` (singleton) and `IClientAddress`
(scoped, the circuit's client address) back the redemption's limit. The circuit's address comes from its hub
connection: `ClientAddressHubFilter` (a global, read-only hub filter) exposes the calling connection's address during
each hub call, and `CircuitClientAddress` (a `CircuitHandler`, created inside the hub call that starts the circuit) keeps it for the circuit. Gateway target kind `account`, id argument `account` (the email); `account.create`
also records `role`. Verification: `NotVerifiable` (own account store).

Host CLI: `dotnet Whiskers.dll --recover-local-account <email>` — exit codes `0` issued, `2` usage, `3` no such local
account, `1` nothing changed. Audit actions: `auth.recovery_issued` (CLI, written before the change),
`auth.recovery_redeemed`, `auth.recovery_refused` (reason only).

UI: `/account` (*My account*), `/account/recover` (anonymous), Settings panel *Local accounts* (`LocalAccountsPanel`).

## Rules

1. **Recovery codes, not temporary passwords.** 120 random bits, shown once, valid 24 h and once, stored only as an
   Identity password hash; issuing one removes the password and changes the stamp in one save. A code cannot sign in;
   it only sets a new password — the change on first use is enforced by construction.
2. **Recovery authority:** an Admin (reset, invitation) or the host operator (CLI inside the running container, an
   existing account only, never a role). The CLI's audit row comes first; without it nothing changes.
3. **Last Admin:** an Admin is deleted only while another Admin can sign in (a local account with a password, or a
   Google/OIDC user seen signing in). Admin changes are serialised process-wide (single instance). Demotion stays with
   the role store's rule (TA-02).
4. **Deletion** also removes the account's explicit role entry.
5. **MFA posture:** none for local accounts in 1.0; production uses the IdP (OIDC) with MFA — stated in README,
   SECURITY.md, `docs/operations/local-accounts.md`, Settings and *My account*.

## Open decisions for the maintainer

1. Recovery mechanism: CLI inside the running container (chosen) vs. a file the operator places in the data directory.
2. Password policy stays Identity's (12 + all character classes); no configuration knob in 1.0.
3. Code lifetime 24 h for resets, invitations and host recovery alike.
4. Self-service password change at read level is allowed while the kill switch is on.
5. Deleting an account removes its explicit role (a federated user with the same address loses it too).
6. No local TOTP (out of scope unless selected).
7. Admin impersonation by design: an Admin who resets or adds an account receives its code and could redeem it
   themselves — for another Admin's address too, including a Google/OIDC Admin whose address gets a local account.
   Audited (`account.password_reset` / `account.create`); the owner notices their password stopped working.
8. The kill switch stops neither the recovery-code redemption nor the host CLI (both restore a person's own access).
9. With `AUTH_DISABLED`, adding, resetting and deleting accounts — and redeeming a code — are refused
   (`auth_disabled`, fail closed). The host CLI still issues codes; they are redeemable once authentication is on.
10. Recovery-page limits: 10 attempts per client per minute (an IPv4 address, or an IPv6 /64), 10 per account per 15
    minutes, enforced in the service (the page runs in a Blazor circuit, outside `/login-local`'s HTTP limiter; the
    protected pipeline stays unchanged). The client address is the circuit's hub connection's (WebSockets and long
    polling), as the forwarded-headers middleware resolved it. Unknown client addresses share one bucket; each table
    holds at most 4096 keys, beyond which new keys share one overflow bucket (fail closed). Every answer of the page
    takes at least one second. Operators should narrow `ForwardedHeaders:TrustedNetworks` to the real proxy.
11. Demotion of the only Admin who can sign in is guarded only by the role store's explicit-Admin rule (TA-02); not
    extended in R1-49.
