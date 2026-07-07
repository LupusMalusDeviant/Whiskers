# Services/Auth

Authorization primitives used across the app: who the current user is, what role they hold, and whether their email is allowed in.

Authentication itself (Google OAuth / OIDC) is wired in [`Program.cs`](../../Program.cs); this folder is the **authorization** layer that sits on top.

## Files

| File | Purpose |
|---|---|
| `ICurrentUserService.cs` / `CurrentUserService.cs` | Resolves the current authenticated user (email, role) from the request context. |
| `IRoleService.cs` / `RoleService.cs` | Maps users to roles (e.g. Admin/User) and resolves a role's permission level. |
| `IWhitelistService.cs` / `WhitelistService.cs` | The email whitelist, who may sign in; managed in the UI, applied without restart. |
| `AuthConstants.cs` | Well-known auth scheme names + claim types in one place: `AuthDisabledScheme` (trusted-LAN → Admin), `AgentSyntheticScheme` + `McpLevelClaim` (in-process agent execution, enforced at the caller's level — never Admin). |

## Behaviour notes

- **Whitelist semantics:** disabled ⇒ everyone allowed; enabled **and non-empty** ⇒ only listed emails; enabled **and empty** ⇒ deny all (fail-closed). The Settings UI refuses to save an enabled+empty whitelist so an admin can't lock themselves out. `SaveWhitelistAsync` deep-copies the incoming data so the enforcement snapshot never aliases a caller-owned list.
- **Synthetic agent identity:** agent tool execution runs under `AgentSyntheticScheme` carrying the caller's real MCP level in `McpLevelClaim`; tool-internal `McpPermissionCheck` enforces that level rather than granting Admin. This is deliberately **not** the `AuthDisabled` admin scheme.

## Related

- MCP authorization (separate from web auth): [`../Mcp/`](../Mcp/)
- Agent principal resolution reuses roles: [`../Agent/AgentPrincipalResolver.cs`](../Agent/AgentPrincipalResolver.cs)
- UI: [`../../Components/Pages/Settings.razor`](../../Components/Pages/Settings.razor), [`Login.razor`](../../Components/Pages/Login.razor)
