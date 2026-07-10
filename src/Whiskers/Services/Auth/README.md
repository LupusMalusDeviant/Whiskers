# Services/Auth

Authorization primitives used across the app: who the current user is, what role they hold, and whether their email is allowed in.

Authentication itself (Google OAuth / OIDC) is wired in [`../../Startup/WhiskersAuthenticationExtensions.cs`](../../Startup/WhiskersAuthenticationExtensions.cs); this folder is the **authorization** layer that sits on top.

## Files

| File | Purpose |
|---|---|
| `ICurrentUserService.cs` / `CurrentUserService.cs` | Resolves the current authenticated user (email, role) from the request context. |
| `IRoleService.cs` / `RoleService.cs` | Maps users to roles (Viewer/Operator/Admin). On first run seeds the configured admin email (`WHISKERS_ADMIN_EMAIL` / first `GOOGLE_ADMIN_EMAIL`) as Admin so a fresh instance is never admin-less (C5). |
| `IWhitelistService.cs` / `WhitelistService.cs` | The email whitelist, who may sign in; managed in the UI, applied without restart. Consults `IRoleService` for the fail-closed switch. |
| `AuthConstants.cs` | Well-known auth scheme names + claim types in one place: `AuthDisabledScheme` (trusted-LAN → Admin), `AgentSyntheticScheme` + `McpLevelClaim` (in-process agent execution, enforced at the caller's level — never Admin). |
| `LocalAdminSeeder.cs` | Local-auth first-admin bootstrap (F1): creates the `WHISKERS_ADMIN_EMAIL` user from `WHISKERS_ADMIN_PASSWORD_FILE` on first run. Idempotent; never throws (a bad seed password logs and is skipped, not a boot-brick). |

## Behaviour notes

- **Whitelist semantics (C5):** enabled **and non-empty** ⇒ only listed emails; enabled **and empty** ⇒ deny all. Disabled (never configured) ⇒ **fail-open only while the instance is unconfigured** (`IRoleService.HasAnyRoles()` is false); as soon as *any* role exists, a disabled whitelist is **fail-closed** — only users with an explicit role entry are admitted. So the admin bootstrap alone makes a fresh instance fail-closed without the operator touching the whitelist. The Settings UI refuses to save an enabled+empty whitelist so an admin can't lock themselves out. `SaveWhitelistAsync` deep-copies the incoming data so the enforcement snapshot never aliases a caller-owned list.
- **Admin bootstrap (C5):** `RoleService.InitializeAsync` seeds an Admin role from `WHISKERS_ADMIN_EMAIL` (provider-neutral) + the first `GoogleAuth:AllowedEmails` entry — **first run only** (missing `roles.json`); an existing file is loaded and never overwritten. Fixes the chicken-and-egg where the admin email seeded the whitelist but not the Admin role, leaving the Admin-gated Settings unreachable.
- **Role updates are snapshot-isolated:** `RoleService` clones role data before caching/persisting and snapshots under its write-lock before serializing — an unsaved UI edit never mutates the live enforcement list, and a concurrent write can't corrupt serialization (same discipline as the whitelist).
- **Synthetic agent identity:** agent tool execution runs under `AgentSyntheticScheme` carrying the caller's real MCP level in `McpLevelClaim`; tool-internal `McpPermissionCheck` enforces that level rather than granting Admin. This is deliberately **not** the `AuthDisabled` admin scheme.
- **Local login (F1):** ASP.NET Identity provides username/password auth *additively* — `AddIdentityCore<AppUser>` (no competing cookie scheme, no `RoleManager`), a separate `WhiskersIdentityDbContext` (users only) in `Whiskers.Data`, and the `POST /login-local` endpoint (in the pipeline) which validates the password, applies the **same** whitelist gate as Google/OIDC, and issues the **existing** cookie carrying a `ClaimTypes.Email` claim — so roles resolve from `roles.json` exactly as for a federated user. No parallel role system.

## Related

- MCP authorization (separate from web auth): [`../Mcp/`](../Mcp/)
- Agent principal resolution reuses roles: [`../Agent/AgentPrincipalResolver.cs`](../Agent/AgentPrincipalResolver.cs)
- UI: [`../../Components/Pages/Settings.razor`](../../Components/Pages/Settings.razor), [`Login.razor`](../../Components/Pages/Login.razor)
