# Whiskers.Migrations.Sqlite

The **SQLite** EF Core migrations for `MetricsDbContext` (which lives in [`Whiskers.Data`](../Whiskers.Data/)). Kept in its own assembly, separate from the PostgreSQL migrations, because EF Core allows only one model snapshot per context per assembly ([ADR-0004](../../docs/adr/0004-postgres-provider-support.md)).

- `20260707164258_InitialCreate` (+ `.Designer`) — the frozen baseline. Its **name must never change** (ADR-0003): `DatabaseInitializer` stamps legacy `EnsureCreated` databases against exactly this id. SQLite types are TEXT-for-everything (incl. `DateTime`) with `Sqlite:Autoincrement` primary keys.
- `MetricsDbContextModelSnapshot` — the SQLite model snapshot.

The app pins this assembly with `UseSqlite(..., o => o.MigrationsAssembly("Whiskers.Migrations.Sqlite"))`.

Scaffold a new migration (SQLite is the default provider — no env var needed):
```
dotnet ef migrations add <Name> --project src/Whiskers.Migrations.Sqlite --startup-project src/Whiskers
```

References `Whiskers.Data` only — never the app, so there is no circular dependency.
