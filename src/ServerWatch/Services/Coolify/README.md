# Services/Coolify

Integration with [Coolify](https://coolify.io/) (a self-hosted PaaS). Lets ServerWatch list and operate Coolify applications, databases and servers, manage env vars, and trigger deployments — including deploy-by-tag.

## Files

| File | Purpose |
|---|---|
| `ICoolifyService.cs` / `CoolifyApiService.cs` | Coolify API client: connection test, list/get applications, databases and servers, application logs, env vars, and deployment actions (start/stop/restart, deploy, deploy by tag). |
| `ICoolifyConfigService.cs` / `CoolifyConfigService.cs` | Stores the Coolify API connection settings (base URL + token). |

## Related

- Models: [`../../Models/Coolify/`](../../Models/Coolify/)
- UI: [`../../Components/Pages/Coolify.razor`](../../Components/Pages/Coolify.razor)
- MCP tools: `list_coolify_applications`, `get_coolify_application`, `get_coolify_application_logs`, `list_coolify_servers`, `list_coolify_databases`, `get_coolify_env_vars`, `set_coolify_env_var`, `deploy_coolify_application`, `deploy_coolify_by_tag`, `start_coolify_application`, `stop_coolify_application`, `restart_coolify_application`
