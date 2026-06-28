# Services/Auth

Authorization primitives used across the app: who the current user is, what role they hold, and whether their email is allowed in.

Authentication itself (Google OAuth / OIDC) is wired in [`Program.cs`](../../Program.cs); this folder is the **authorization** layer that sits on top.

## Files

| File | Purpose |
|---|---|
| `ICurrentUserService.cs` / `CurrentUserService.cs` | Resolves the current authenticated user (email, role) from the request context. |
| `IRoleService.cs` / `RoleService.cs` | Maps users to roles (e.g. Admin/User) and resolves a role's permission level. |
| `IWhitelistService.cs` / `WhitelistService.cs` | The email whitelist, who may sign in; managed in the UI, applied without restart. |

## Related

- MCP authorization (separate from web auth): [`../Mcp/`](../Mcp/)
- Agent principal resolution reuses roles: [`../Agent/AgentPrincipalResolver.cs`](../Agent/AgentPrincipalResolver.cs)
- UI: [`../../Components/Pages/Settings.razor`](../../Components/Pages/Settings.razor), [`Login.razor`](../../Components/Pages/Login.razor)
