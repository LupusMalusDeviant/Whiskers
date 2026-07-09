# Services/Persistence

The storage primitives every other service builds on. Whiskers persists two ways: a **relational database** (via EF Core) for time-series and structured data, and **JSON file stores** for configuration. The relational provider is chosen at startup — **SQLite** (zero-config default) or **PostgreSQL** (opt-in). JSON stores, keys and certificates always live under the data directory — `WHISKERS_DATA_DIR`, default `/app/data` — resolved centrally by [`../../Configuration/DataPathOptions.cs`](../../Configuration/DataPathOptions.cs), never in the image.

> The EF Core `DbContext`, its 15 entity types and the UTC value converter now live in the standalone [`Whiskers.Data`](../../../Whiskers.Data/) project; the per-provider migrations live in [`Whiskers.Migrations.Sqlite`](../../../Whiskers.Migrations.Sqlite/) and [`Whiskers.Migrations.Postgres`](../../../Whiskers.Migrations.Postgres/). See [ADR-0004](../../../../docs/adr/0004-postgres-provider-support.md) for why.

## Files

| File | Purpose |
|---|---|
| `DatabaseRegistration.cs` | `AddWhiskersDatabase` — reads `Database:Provider` / `WHISKERS_DB_PROVIDER` (+ `_CONNECTION` / `_CONNECTION_FILE` secret) and registers `MetricsDbContext` for SQLite or PostgreSQL, pinning the matching `MigrationsAssembly`. An unknown provider fails fast at startup. |
| `DatabaseInitializer.cs` | Brings the schema up to date on startup. The SQLite path baselines legacy `EnsureCreated` databases onto migrations + enables WAL; the PostgreSQL path is a straight `MigrateAsync` (see below). |
| `MetricsDbContextFactory.cs` | Design-time `IDesignTimeDbContextFactory` used only by `dotnet ef`; branches on `WHISKERS_DB_PROVIDER` so scaffolding targets the right migration assembly without booting the app host. |
| `SqliteToPostgresMigrator.cs` | The one-time `--migrate-to-postgres` data copy (SQLite → a fresh PostgreSQL). Read-only on the source, aborts on a non-empty target; the provider-agnostic core is unit-tested. See below. |
| `JsonFileStore.cs` | A small generic helper for atomic load/save of a typed object to a JSON file (used by the config/settings/policy stores). |
| `AppSettingsStore.cs` | `IAppSettingsStore`, writes a config section into the data dir's `app-settings.json` (the last config layer, reload-on-change) so UI-edited settings apply live without a restart. |

## Provider selection

Chosen at startup, SQLite by default:

- **SQLite** (default): nothing to configure — the DB file lives under `WHISKERS_DATA_DIR`.
- **PostgreSQL**: `WHISKERS_DB_PROVIDER=postgres` + a connection string (`WHISKERS_DB_CONNECTION`, or `WHISKERS_DB_CONNECTION_FILE` for a mounted secret — the file wins). Ready-to-run overlay: [`deploy/docker-compose.postgres.yml`](../../../../deploy/docker-compose.postgres.yml).

`DatabaseInitializer` branches on `db.Database.IsSqlite()`: SQLite keeps the legacy-baseline heal + WAL pragma; PostgreSQL just migrates (transient connect failures are absorbed by the provider's `EnableRetryOnFailure`). All `DateTime`s are normalized to UTC end-to-end so Npgsql's `timestamptz` mapping is happy — see `UtcDateTimeConverter` in `Whiskers.Data`.

**Migrating existing data (SQLite → PostgreSQL):** a one-time, offline copy, never during normal boot:

```
dotnet Whiskers.dll --migrate-to-postgres "Host=…;Database=whiskers;Username=…;Password=…"
```

It migrates the target schema, verifies the target is **empty** (aborts otherwise — no merge), then copies all 15 tables in batches. The source is never modified — back up `metrics.db` first. Surrogate `Id`s are reassigned by the target (no FKs between tables); unique business keys are preserved. See `SqliteToPostgresMigrator`.

## Schema & migrations

The schema is managed by **EF Core migrations**, one assembly per provider, applied on startup by `DatabaseInitializer.InitializeAsync` (called from `Program.cs`). To change the schema:

1. Edit `MetricsDbContext` (in `Whiskers.Data`) — add/adjust an entity + its indexes.
2. Scaffold the migration for **both** providers (the design-time factory picks the target via `WHISKERS_DB_PROVIDER`):
   ```
   dotnet ef migrations add <Name> --project src/Whiskers.Migrations.Sqlite --startup-project src/Whiskers
   $env:WHISKERS_DB_PROVIDER="postgres"
   dotnet ef migrations add <Name> --project src/Whiskers.Migrations.Postgres --startup-project src/Whiskers
   ```
3. Commit the generated `*.cs` — never hand-write DDL. `MigrateAsync` applies it on the next boot.

**Baseline (SQLite):** the original `InitialCreate` name is frozen (ADR-0003) — `DatabaseInitializer` recognises pre-migration `EnsureCreated` databases (tables present, no `__EFMigrationsHistory`), heals any missing table, records `InitialCreate` as already-applied, then migrates — non-destructive by construction. See [ADR-0003](../../../../docs/adr/0003-ef-core-migrations-baseline.md) and `DbMigrationBaselineTests` for the data-safety proof.

## Related

- Data layer (context + entities + converter): [`../../../Whiskers.Data/`](../../../Whiskers.Data/)
- Time-series writers/readers: [`../Metrics/`](../Metrics/)
- JSON-backed stores: [`../ServerConfig/`](../ServerConfig/), [`../Agent/`](../Agent/), [`../Agent/Guardrails/`](../Agent/Guardrails/), [`../Vault/`](../Vault/)
