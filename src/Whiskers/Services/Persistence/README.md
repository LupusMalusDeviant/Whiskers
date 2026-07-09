# Services/Persistence

The storage primitives every other service builds on. Whiskers persists two ways: **SQLite** (via EF Core) for time-series and structured data, and **JSON file stores** for configuration. Everything lives under the data directory — `WHISKERS_DATA_DIR`, default `/app/data` (a volume / bind-mount) — resolved centrally by [`../../Configuration/DataPathOptions.cs`](../../Configuration/DataPathOptions.cs), never in the image.

## Files

| File | Purpose |
|---|---|
| `MetricsDbContext.cs` | The EF Core `DbContext` for the SQLite database (metrics time-series and related structured data). |
| `DatabaseInitializer.cs` | Brings the SQLite schema up to date on startup via EF Core migrations, and **baselines** legacy `EnsureCreated` databases onto migrations without recreating existing tables (see below). |
| `MetricsDbContextFactory.cs` | Design-time `IDesignTimeDbContextFactory` used only by `dotnet ef` so migration scaffolding doesn't boot the whole app host. |
| `JsonFileStore.cs` | A small generic helper for atomic load/save of a typed object to a JSON file (used by the config/settings/policy stores). |
| `AppSettingsStore.cs` | `IAppSettingsStore`, writes a config section into the data dir's `app-settings.json` (the last config layer, reload-on-change) so UI-edited settings apply live without a restart. |

## Schema & migrations

The SQLite schema is managed by **EF Core migrations** (`Migrations/`), applied on startup by `DatabaseInitializer.InitializeAsync` (called from `Program.cs`). To change the schema:

1. Edit `MetricsDbContext` (add/adjust an entity + its indexes).
2. Scaffold a migration:
   ```
   dotnet ef migrations add <Name> --project src/Whiskers/Whiskers.csproj --output-dir Migrations
   ```
3. Commit the generated `Migrations/*.cs` — never hand-write DDL again. `MigrateAsync` applies it on the next boot.

**Legacy databases:** every deployment before ADR-0003 was created by `EnsureCreated` + hand-DDL and has no `__EFMigrationsHistory`. `DatabaseInitializer` detects that (tables present, no history), heals any missing table, records `InitialCreate` as already-applied, then migrates — non-destructive by construction. See [`docs/adr/0003`](../../../../docs/adr/0003-ef-core-migrations-baseline.md) and `DbMigrationBaselineTests` for the data-safety proof.

## Related

- Time-series writers/readers: [`../Metrics/`](../Metrics/)
- JSON-backed stores: [`../ServerConfig/`](../ServerConfig/), [`../Agent/`](../Agent/), [`../Agent/Guardrails/`](../Agent/Guardrails/), [`../Vault/`](../Vault/)
