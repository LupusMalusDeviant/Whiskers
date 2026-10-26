# Audit area A: Authentication, Authorization, HTTP/SignalR surface, Sessions

Repo: repository @ `498e70d` (read-only audit). Paths below are relative to `src/Whiskers/` unless noted.

## Scope and method

I read and checked: `Program.cs`, `Startup/WhiskersAuthenticationExtensions.cs`, `Startup/WhiskersPipelineExtensions.cs`, `Startup/WhiskersHostingExtensions.cs`, `Startup/SetupRedirectPaths.cs`, `Startup/MaintenancePaths.cs`, `Services/Auth/*`, `Services/Setup/SetupStateService.cs`, `Mcp/McpBearerAuthMiddleware.cs`, `Mcp/McpPermissionCheck.cs`, `Mcp/McpGuardrailFilter.cs`, `Services/Mcp/McpPermissionService.cs` (key validation), `Hubs/ContainerHub.cs`, `Services/Webhooks/WebhookService.cs`, `Utils/MetricsScrapeAuth.cs`, `Services/Backup/BackupService.cs` (path resolution and archive contents), `Services/Agent/Approvals/ApprovalStore.cs`, `Components/Routes.razor`, `Components/_Imports.razor`, and every page in `Components/Pages/*.razor` (attributes and role guards). I also looked at the related tests in `src/Whiskers.Tests`.

### Endpoint inventory (all `Map*` calls; there are no MVC controllers)

| Endpoint | Auth | Notes |
|---|---|---|
| `/healthz`, `/readyz` | Anonymous | Return only the status word |
| `GET /set-culture` | Anonymous | Uses `LocalRedirect` |
| `GET /login-google`, `GET /login-oidc` | Anonymous | Fixed `RedirectUri` |
| `POST /login-local` | Anonymous + antiforgery (`[FromForm]`) | Not mapped when `Auth:Disabled` |
| `GET /logout` | Anonymous | GET, so it can be triggered cross-site |
| `/hubs/containers` | `[Authorize]` (any role) | Read-only hub methods |
| `/mcp` | Policy: valid bearer key OR any authenticated cookie; each tool calls `CheckAccess` | |
| `GET /backups/self/{id}/download` | Policy: Admin or AuthDisabled | |
| `POST /api/webhooks/{id}` | Anonymous + HMAC | |
| `GET /metrics` | Anonymous + static bearer token (404 when no token is set) | |
| Static assets, `/_blazor*` | Anonymous | Page auth enforced by `[Authorize]` in `_Imports.razor` + fallback policy + `AuthorizeRouteView` |

---

## Findings

### A-01 Setup wizard lets the first anonymous visitor claim Admin (no setup token)
- **Severity:** High · **Category:** Security · **v1-blocker:** yes
- **Evidence:**
  - `Components/Pages/Setup.razor` carries `[AllowAnonymous]`. `CompleteAsync()` calls `SetupState.CompleteSetupAsync(new SetupAdminRequest{ IsLocal=_isLocal, Email=_email.Trim(), ...})`.
  - `Services/Setup/SetupStateService.cs:65-84`: the only gate is `if (_complete || AdminRoleExists()) return AlreadyComplete;`, followed by `await _roles.SetRoleAsync(email, AppRole.Admin);`.
  - `Startup/WhiskersPipelineExtensions.cs:91-96` funnels every HTML navigation to `/setup` until an admin exists.
- **Impact:** Between first boot and the moment the operator finishes the wizard, anyone who can reach the port can:
  - create a local admin with their own password, or
  - pick the "federated" option and type their own Google/OIDC email. No proof of email ownership is needed at this step; that email simply becomes Admin.

  Whoever completes the wizard first owns the instance, which includes root on every managed host (server terminal, `execute_command`). The default compose binds to `127.0.0.1`, which narrows the exposure. Installs on a public bind or behind a reverse proxy are fully exposed, and the hardened profile uses host networking.
- **Recommendation:**
  - Require a one-time setup token, printed to the container log and/or written to `/app/data/setup-token`, the way Jenkins and Grafana do. Alternatively, only allow `/setup` from loopback.
  - For the federated path, bind the admin to the identity that actually completes an OIDC/Google round-trip in the same session.

### A-02 Removing the last Admin re-opens the anonymous setup wizard after a restart
- **Severity:** High · **Category:** Security · **v1-blocker:** yes
- **Evidence:**
  - `Components/Pages/Settings.razor:1155-1159`: `SaveRoles()` persists `_roleEntries` as-is. There is no check that at least one Admin remains, and a grep for "last admin" finds nothing.
  - `Services/Setup/SetupStateService.cs:41-50`: `RefreshAsync` (run only at startup) sets `_complete = AdminRoleExists()` and deletes the flag file if no Admin role is found.
- **Impact:** An admin can leave zero explicit Admin entries, for example by setting `DefaultRole = Admin` and deleting the rows, or by demoting themselves. A restored or corrupted `roles.json` has the same effect. On the next restart `IsSetupComplete` becomes false, the [AllowAnonymous] wizard is live again, and A-01 applies: anyone can take over.
- **Recommendation:**
  - Refuse to save a role set with no explicit Admin (in both the UI and `RoleService.SaveRoleDataAsync`).
  - Make "setup complete" sticky: once the flag exists, never fall back to anonymous setup. Use a CLI or env-var recovery path instead.

### A-03 Local login has no brute-force protection (lockout is configured but not used; no rate limiting)
- **Severity:** High · **Category:** Security · **v1-blocker:** yes
- **Evidence:**
  - `Startup/WhiskersAuthenticationExtensions.cs:167` sets `options.Lockout.MaxFailedAccessAttempts = 5;`.
  - But `Startup/WhiskersPipelineExtensions.cs:220` uses `if (user is null || !await users.CheckPasswordAsync(user, password ?? ""))`. `UserManager.CheckPasswordAsync` neither checks `IsLockedOutAsync` nor calls `AccessFailedAsync`; only `SignInManager.CheckPasswordSignInAsync(lockoutOnFailure:true)` does.
  - A grep for `AddRateLimiter`/`UseRateLimiter` finds nothing in the codebase.
- **Impact:** Online password guessing against the local admin is unlimited. The only brake is PBKDF2 cost per request, so an exposed instance can be attacked continuously. There is also a timing oracle: a missing user returns immediately, while an existing user pays the PBKDF2 cost, which enables account enumeration even though the message is generic.
- **Recommendation:**
  - Use `SignInManager<AppUser>.CheckPasswordSignInAsync(user, pwd, lockoutOnFailure: true)`, or call `IsLockedOutAsync`, `AccessFailedAsync` and `ResetAccessFailedCountAsync` manually.
  - Add the ASP.NET Core rate limiter (per IP and per account) on `/login-local`, `/api/webhooks/*`, `/mcp` and `/metrics`.
  - Run a dummy hash when the user is missing.

### A-04 The "AuthDisabled" principal is trusted even when auth is enabled; anyone holding the DataProtection keys can forge an Admin cookie that skips the whitelist
- **Severity:** Medium · **Category:** Security · **v1-blocker:** no (recommended)
- **Evidence:**
  - `Services/Auth/CurrentUserService.cs:35`: `if (state.User.Identity?.AuthenticationType == "AuthDisabled") return AppRole.Admin;`
  - `Mcp/McpPermissionCheck.cs:60` (Admin) and `Startup/WhiskersPipelineExtensions.cs:285` (backup download) do the same.
  - `Startup/WhiskersAuthenticationExtensions.cs:75`: `OnValidatePrincipal` skips the whitelist re-check for this authentication type.
  - None of these checks consult `Auth:Disabled`.
  - DP keys: `Startup/WhiskersHostingExtensions.cs:42` calls `PersistKeysToFileSystem(...)` without `ProtectKeysWith*`. Self-backups pack every `/app/data` file (`BackupService.cs` `EnumerateDataFiles(RootDir)`) and are plaintext when `VAULT_KEY` is unset (`BackupService.cs:153` warns about this).
- **Impact:** A leaked data directory or unencrypted backup is enough to mint a cookie whose identity has `AuthenticationType="AuthDisabled"`. That cookie is unconditional Admin, cannot be revoked by the whitelist, and needs no role entry. Forging a normal cookie with the admin's email would also work. The magic string makes the forged session harder to detect and to revoke.
- **Recommendation:**
  - Honor `AuthDisabled` only when `Auth:Disabled` is actually true (inject the config into one central helper).
  - Reject cookie tickets that carry it otherwise.
  - Encrypt the DP key ring at rest (for example derived from `VAULT_KEY`), and consider excluding `keys/` from backups or forcing backup encryption.

### A-05 No session revocation: sliding 24h persistent cookies, no security stamp, and Blazor circuits never re-validate
- **Severity:** Medium · **Category:** Security · **v1-blocker:** no (should fix soon)
- **Evidence:**
  - `Startup/WhiskersAuthenticationExtensions.cs:62`: `ExpireTimeSpan = TimeSpan.FromHours(24)`. `SlidingExpiration` is left at its default (true).
  - `Startup/WhiskersPipelineExtensions.cs:236`: `IsPersistent = true` is always set for local login.
  - The cookie is self-contained, with no `ITicketStore` or security-stamp validation. `OnValidatePrincipal` (lines 70-95) only re-checks the whitelist, and fails open (`catch { }`, line 91).
  - There is no `RevalidatingServerAuthenticationStateProvider` (grep finds none).
  - Pages cache the role once in `OnInitializedAsync`, for example `_role = await CurrentUser.GetRoleAsync()` in `Settings.razor:725`, `Servers.razor:417`, `Dashboard.razor:591`. `TerminalView.razor:51` checks the role once and keeps the shell open.
- **Impact:**
  - `/logout` only deletes the client copy of the cookie, so a stolen cookie stays valid for as long as it keeps being used (sliding).
  - Removing a user from the whitelist or roles does not end the user's already-open Blazor circuit or terminal session.
  - There is no password-change or "sign out everywhere" capability.
  - OIDC IdP-side logout and revocation are not honored (no RP-initiated logout, `SaveTokens=true` without refresh).
- **Recommendation:**
  - Add a server-side session store or a security stamp, with "revoke all sessions".
  - Add a revalidating AuthenticationStateProvider (for example every 1-5 minutes) that re-checks the whitelist and role and tears down the circuit and terminal sessions on failure.
  - Set an absolute session lifetime.
  - Re-check the role inside the `Require()` helpers rather than using the cached field.

### A-06 Identity is keyed solely on the email claim; the OIDC email claim is configurable and `email_verified` is never checked
- **Severity:** Medium (needs verification per IdP) · **Category:** Security · **v1-blocker:** no (document + fix)
- **Evidence:**
  - `Startup/WhiskersAuthenticationExtensions.cs:146-148`: `var emailClaim = oidcSection["EmailClaim"] ?? "email"; options.ClaimActions.MapJsonKey(ClaimTypes.Email, emailClaim);`
  - Roles and whitelist resolve by email (`RoleService.GetRole(email)`, `WhitelistService.IsEmailAllowed`).
  - A grep for `email_verified` finds nothing.
- **Impact:** With a generic OIDC IdP that allows self-registration or user-editable, unverified emails (some Keycloak or Authentik setups), or when `EmailClaim` points to a user-controlled attribute such as `preferred_username`/`upn` on multi-tenant Entra, a user can assert the admin's email and inherit the Admin role. Local, Google and OIDC accounts that share an email also collapse into one identity.
- **Recommendation:**
  - Require `email_verified == true` (configurable), and/or bind roles to `(issuer, sub)`.
  - Document the trust requirement on the IdP.

### A-07 Webhooks: no replay protection, and an unauthenticated ID-enumeration oracle
- **Severity:** Medium · **Category:** Security · **v1-blocker:** no
- **Evidence:**
  - `Services/Webhooks/WebhookService.cs:105-111`: the HMAC covers only the body, compared with `FixedTimeEquals` (good). There is no timestamp, nonce, or delivery-ID dedupe (`X-GitHub-Delivery` is not used).
  - Lines 93-94 return distinct answers to unauthenticated callers: "Webhook not found", "Webhook is disabled", "Signature required" and "Invalid signature".
  - `WhiskersPipelineExtensions.cs:296` reads the body without a size cap (`ReadToEndAsync`; Kestrel's default 30 MB limit applies).
- **Impact:** Anyone who once observed a signed request (proxy logs, CI logs, a GitHub delivery replay) can replay it indefinitely to restart, rebuild or redeploy containers. Attackers can also probe which webhook IDs exist and whether they are enabled.
- **Recommendation:**
  - Require a signed timestamp header with a ±5 min window and/or dedupe delivery IDs.
  - Return one generic 401/404 for every failure.
  - Cap the body size (for example 1 MB) and rate-limit the endpoint.

### A-08 AUTH_DISABLED mode combined with `AllowedHosts: "*"` is open to DNS rebinding and cross-site WebSocket hijacking
- **Severity:** Medium (High while AUTH_DISABLED is in use) · **Category:** Security · **v1-blocker:** no (doc/guard)
- **Evidence:**
  - `appsettings.json`: `"AllowedHosts": "*"`.
  - `WhiskersPipelineExtensions.cs:136-149` gives every request an Admin principal.
  - `deploy/install.sh:190` recommends `Auth__Disabled=true` for a "localhost-only trial". That text is also stale: it says the wizard has not shipped yet.
  - SignalR hubs (`/hubs/containers`, `/_blazor`) have no Origin check.
- **Impact:** A loopback bind does not protect the instance against DNS rebinding. A malicious website the operator visits can rebind its hostname to 127.0.0.1 and use `/mcp` (Admin tools including `execute_command`), the backup download and the hub with same-origin privileges. Even without rebinding, a cross-site WebSocket to `/hubs/containers` (skipNegotiation) can read the container inventory. Needs verification for `/mcp` from a pure cross-site form POST (SDK content-type/Accept checks).
- **Recommendation:**
  - When `Auth:Disabled` is set, enforce a Host allowlist (refuse `AllowedHosts=*`, default to `localhost;127.0.0.1`).
  - Add an Origin check on hubs.
  - Log a loud startup warning.
  - Update `install.sh` to point to the setup wizard instead.

### A-09 Viewer role can perform write and destructive actions on shared state
- **Severity:** Medium · **Category:** Security · **v1-blocker:** no
- **Evidence:**
  - `Components/Pages/NotificationsLog.razor:158-160`: `ClearAll()` calls `Store.Clear()` with no role check. That runs `DELETE FROM "Notifications"` (`Services/Notifications/InAppNotificationStore.cs:78-83`).
  - `Components/Pages/Cves.razor:350-360`: `TriggerScan()` runs `Monitor.RunOneCycleAsync()` (fleet-wide Trivy scan) for any role.
  - `Services/Agent/Approvals/ApprovalStore.cs:118-121`: "Rejecting is always allowed", so a Viewer can reject Admin and Operator pending approvals. Approval also has no four-eyes rule: the requester can approve their own request.
- **Impact:** Any authenticated Viewer can:
  - wipe the global alert history (anti-forensics, hiding alerts),
  - trigger expensive scans on every host (DoS),
  - veto agent actions.
- **Recommendation:** Gate `Clear`, `MarkAllRead`, `TriggerScan` and approval resolution with `Require(AppRole.Operator/Admin)`. Optionally forbid self-approval for admin-level actions.

### A-10 No audit trail for authentication events or backup downloads
- **Severity:** Medium · **Category:** Operations · **v1-blocker:** no
- **Evidence:**
  - `/login-local` (`WhiskersPipelineExtensions.cs:211-238`), `WhitelistGate` (`WhiskersAuthenticationExtensions.cs` `WhitelistGate`), `/logout` and `/backups/self/{id}/download` (275-290) write neither audit log entries nor log lines.
  - A grep for login audit actions finds nothing.
- **Impact:** Failed or successful logins, whitelist rejections and downloads of the secret-bearing self-backup are invisible, so incident response and compliance (the commercial readiness bar) are not possible.
- **Recommendation:** Emit `auth.login.success/failure`, `auth.logout`, `auth.whitelist.rejected` and `backup.download` events (with IP and user) to `IAuditLogService`.

### A-11 No local user management (password change/reset, extra users, delete, MFA)
- **Severity:** Medium · **Category:** Commercial readiness · **v1-blocker:** no (product decision)
- **Evidence:** `UserManager.CreateAsync` is only called from `SetupStateService.cs:76` and `LocalAdminSeeder.cs:37`. There is no `ChangePasswordAsync`, `ResetPasswordAsync` or `DeleteAsync` anywhere, and no 2FA.
- **Impact:** A compromised local password cannot be rotated from the product, a second local admin cannot be created, and there is no MFA for an internet-facing control plane that holds root on hosts.
- **Recommendation:** Add a password-change UI (with stamp-based session invalidation, see A-05), Admin-managed local users, and TOTP 2FA, or document "use OIDC with MFA" as the supported production path.

### A-12 Fail-open whitelist before setup / with no roles: any Google or OIDC account gets Viewer, including the MCP read tools
- **Severity:** Medium · **Category:** Security · **v1-blocker:** no (closes once setup completes)
- **Evidence:**
  - `Services/Auth/WhitelistService.cs:83`: `return !_roles.HasAnyRoles() || _roles.HasExplicitRole(email);`
  - `RoleService.GetRole` defaults to `DefaultRole` (Viewer).
  - `/mcp` accepts any authenticated cookie (`WhiskersPipelineExtensions.cs:267-268`), and `get_container_logs` is Read level (`Models/McpPermission.cs:79`).
  - `OnValidatePrincipal` fails open on exceptions (`WhiskersAuthenticationExtensions.cs:91`).
- **Impact:** If Google/OIDC is configured without `WHISKERS_ADMIN_EMAIL` or `AllowedEmails`, every Google account in the world can sign in as Viewer until an admin exists. The setup redirect only intercepts HTML navigations, so the hub and `/mcp` read tools (container logs, which often contain secrets) are reachable.
- **Recommendation:** Fail closed whenever no admin exists: before setup, deny all federated logins except the wizard flow. Make the per-request re-check fail closed, or at least log the error.

### A-13 Missing security headers (frame-ancestors/X-Frame-Options, CSP, nosniff, Referrer-Policy) and no HSTS
- **Severity:** Low · **Category:** Security · **v1-blocker:** no
- **Evidence:** A grep for `X-Frame-Options`, `Content-Security-Policy`, `frame-ancestors` and `X-Content-Type-Options` finds nothing. Neither `UseHsts` nor `UseHttpsRedirection` is called.
- **Impact:** The admin UI can be framed (clickjacking). The SameSite=Lax cookie blocks most cross-site framing in auth mode, but same-site sibling subdomains and AUTH_DISABLED deployments are fully exposed.
- **Recommendation:** Add a header middleware (`frame-ancestors 'none'`, `nosniff`, `Referrer-Policy`, a baseline CSP compatible with Blazor), plus optional HSTS when `X-Forwarded-Proto=https`.

### A-14 Cookie hardening left at defaults
- **Severity:** Low · **Category:** Security · **v1-blocker:** no
- **Evidence:** `Startup/WhiskersAuthenticationExtensions.cs:58-97`: no `Cookie.SecurePolicy`, `Cookie.Name`, `Cookie.SameSite`, `AccessDeniedPath` or `SlidingExpiration` setting. The defaults are HttpOnly=true, SameSite=Lax, Secure=SameAsRequest. The OIDC correlation and nonce cookies are explicitly made non-Secure when `RequireHttpsMetadata=false` (lines 132-139). `SaveTokens=true` (118) puts IdP tokens into the cookie.
- **Impact:**
  - On plain HTTP the session cookie travels in clear.
  - `RequireHttpsMetadata=false` allows MITM of OIDC discovery.
  - An authenticated Viewer hitting the Admin-only backup URL is redirected to the non-existent `/Account/AccessDenied`.
  - Storing tokens in the cookie enlarges it and is not needed (they are never used).
- **Recommendation:** Set `SecurePolicy=Always` when served via HTTPS or a proxy (configurable), use the `__Host-` prefix, set `AccessDeniedPath`, set `SaveTokens=false` unless needed, and warn at startup when `RequireHttpsMetadata=false`.

### A-15 Forwarded headers trust all of RFC1918 by default
- **Severity:** Low · **Category:** Security · **v1-blocker:** no
- **Evidence:** `Utils/ForwardedHeadersConfig` defaults include `10/8`, `172.16/12`, `192.168/16` and `100.64/10`, and `WhiskersPipelineExtensions.cs:40-47` applies them.
- **Impact:** Any container on a Docker bridge network, and any LAN client NATed through docker-proxy (which appears as 172.x), can spoof `X-Forwarded-For`/`X-Forwarded-Proto`. Today that only corrupts the webhook `SourceIp` (the only IP consumer, line 298) and scheme detection. It will matter as soon as an IP-based rate limit or allowlist is added (see A-03).
- **Recommendation:** Default to loopback only, and require operators to set `ForwardedHeaders:TrustedNetworks` to their proxy address.

### A-16 Minor HTTP-surface robustness issues
- **Severity:** Low · **Category:** Reliability/UX · **v1-blocker:** no
- **Evidence and impact:**
  - `GET /logout` is anonymous and uses GET (line 241), so a cross-site `<img>` can log the user out (CSRF logout).
  - `/login-google` and `/login-oidc` call `ChallengeAsync` for schemes that may not be registered, which throws a 500 when the provider is not configured (lines 189-203).
  - `/set-culture?redirect=//evil` makes `LocalRedirect` throw a 500 rather than fall back to `/`.
  - The login always redirects to `/` and ignores `ReturnUrl` (line 237).
  - `/mcp` without credentials receives a 302 to `/login` rather than 401 + `WWW-Authenticate`, which confuses MCP clients.
  - `UseAntiforgery()` runs before `UseAuthentication()` (lines 70 and 132), against Microsoft's recommended order. It works for anonymous login, but posting `/login-local` while already signed in will probably fail token validation (claims mismatch). Needs verification.
  - Prometheus labels `name`, `image` and `project` are not escaped (line 370), unlike `Esc()` used elsewhere, so a container name containing `"` breaks the scrape.
- **Recommendation:** Use POST plus antiforgery for logout, map login endpoints only when the provider exists, use `Url.IsLocalUrl` with a fallback, honor `ReturnUrl` via `IsLocalUrl`, return 401 for `/mcp`, follow the documented middleware order, and escape metric labels.

### A-17 No per-server or tenant scoping (IDOR is "by design")
- **Severity:** Low (Architecture/Commercial) · **Category:** Commercial readiness · **v1-blocker:** no
- **Evidence:** Roles are global (`Models/UserRole.cs` has Viewer/Operator/Admin only). `ContainerHub.RequestContainerList(serverId)` and `RequestContainerStats(containerId, serverId)` (`Hubs/ContainerHub.cs:17-34`), the `/container/{ServerId}/{ContainerId}` pages and the MCP tools accept any server ID.
- **Impact:** Every user can see every server. A "team A sees only its servers" model (common MSP/enterprise ask) is not possible.
- **Recommendation:** Document that the model is single-tenant, and plan server-group scoping if needed.

### A-18 Test gaps on the auth surface
- **Severity:** Medium · **Category:** Testing · **v1-blocker:** no (strongly recommended)
- **Evidence:**
  - `src/Whiskers.Tests` covers the local login happy path (`LocalAuthIdentityTests.cs:212-220`), `SetupRedirectPaths`, `SetupStateService`, `MetricsScrapeAuth`, webhook signatures and MCP key scoping.
  - There is no integration test that:
    - sends anonymous requests to every protected endpoint (pages, `/hubs/containers`, `/backups/self/*`, `/mcp`) and expects 302/401,
    - checks that a Viewer gets 403 on the backup download,
    - verifies lockout after N failed logins,
    - verifies that `/setup` is dead after completion,
    - verifies that a whitelist removal invalidates the cookie.
- **Recommendation:** Add a `WebApplicationFactory` endpoint-authorization matrix test that enumerates `EndpointDataSource` and asserts that every endpoint not on the allowlist requires auth. This guards the fail-closed fallback policy against regressions.

---

## Positive controls (verified)

- **Fail-closed fallback policy.** `SetFallbackPolicy(RequireAuthenticatedUser)` (`WhiskersAuthenticationExtensions.cs:181`), a global `@attribute [Authorize]` in `_Imports.razor`, and explicit `[AllowAnonymous]` only on Login, Setup and Error. The `/_blazor` anonymous opt-out is targeted at the framework endpoints only.
- **Server-side role guards in pages.** Pages consistently guard mutating handlers with `Require(AppRole.X)` in the handler and hide the UI as well (Servers, Dashboard, ContainerDetail, Firewall, Nginx, SSL, Systemd, Networks, Webhooks, GitDeploy, ScheduledTasks, VolumeBackups, SelfBackup, AppStore, Deploy, ComposeEditor, LogSearch, Cloud). Settings, Guardrails and AI Triggers don't load sensitive data for non-admins. The container terminal requires Operator and the host terminal requires Admin. Env-var secrets are masked and never sent to the browser.
- **MCP.**
  - Keys are stored as SHA-256 hashes and compared with `FixedTimeEquals` (`McpPermissionService.cs:100-143`).
  - Cookie callers are mapped to their real role level, and unknown tools default to Admin (`McpPermissionCheck.cs`).
  - The agent's synthetic identity carries the caller's level, not Admin.
  - Guardrails fail closed when the caller cannot be identified (`McpGuardrailFilter.cs`).
- **Webhooks.** A secret is mandatory, HMAC-SHA256 is checked over the raw body with constant-time compare, and secrets are 256-bit random. The deploy target path is shell-quoted.
- **`/metrics`.** Disabled unless a token is configured, with constant-time token compare.
- **Self-backup download.** Admin-only policy, `SafeName` id validation and a full-path containment check (`BackupService.cs:190-200`).
- **Whitelist and roles.**
  - An enabled-but-empty whitelist denies everyone.
  - A disabled whitelist fails closed once any role exists.
  - The whitelist is re-checked per request.
  - The UI refuses to save an enabled, empty whitelist.
  - The admin bootstrap comes from `WHISKERS_ADMIN_EMAIL`.
- **Local login.** Antiforgery is required, the error is generic (`invalid`), and the password policy demands 12+ characters with the Identity complexity defaults.
- **OIDC.** Code flow with PKCE, and fixed `RedirectUri`s (no open redirect).
- **Forwarded headers.** Not trust-all; restricted to configured networks.
- **Setup completion.** Lock-protected and idempotent, re-checked inside the service (not just the UI), and the `/setup` route redirects after completion.
- **Compose defaults.** Bind to `127.0.0.1`, and the hardened profile listens only on loopback.
- **Approvals.** Approving requires at least the action's own level (`ApprovalStore.cs:120`).
- **SignalR.** `MaximumReceiveMessageSize` is capped at 64 KB.
