# Services/Persistence

The storage primitives every other service builds on. ServerWatch persists two ways: **SQLite** (via EF Core) for time-series and structured data, and **JSON file stores** for configuration. Everything lives under `/app/data` (a volume / bind-mount) — never in the image.

## Files

| File | Purpose |
|---|---|
| `MetricsDbContext.cs` | The EF Core `DbContext` for the SQLite database (metrics time-series and related structured data). |
| `JsonFileStore.cs` | A small generic helper for atomic load/save of a typed object to a JSON file (used by the config/settings/policy stores). |
| `AppSettingsStore.cs` | `IAppSettingsStore` — writes a config section into `/app/data/app-settings.json` (the last config layer, reload-on-change) so UI-edited settings apply live without a restart. |

## Related

- Time-series writers/readers: [`../Metrics/`](../Metrics/)
- JSON-backed stores: [`../ServerConfig/`](../ServerConfig/), [`../Agent/`](../Agent/), [`../Agent/Guardrails/`](../Agent/Guardrails/), [`../Vault/`](../Vault/)
