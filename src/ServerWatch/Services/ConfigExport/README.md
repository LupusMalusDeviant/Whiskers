# Services/ConfigExport

Exports the **non-secret** application configuration (servers, roles, whitelist, MCP setup) as JSON — useful for backup, review, or migrating to another instance. Secrets are never included.

## Files

| File | Purpose |
|---|---|
| `IConfigExportService.cs` / `ConfigExportService.cs` | Serialises the non-secret app configuration to JSON. |

## Related

- Secrets live in [`../Vault/`](../Vault/) and are excluded here
- UI: [`../../Components/Pages/Settings.razor`](../../Components/Pages/Settings.razor)
