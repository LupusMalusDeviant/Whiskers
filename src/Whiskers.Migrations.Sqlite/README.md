# Whiskers.Migrations.Sqlite

The **SQLite** EF Core migrations for the app's two DbContexts (both in [`Whiskers.Data`](../Whiskers.Data/)). Kept in its own assembly, separate from the PostgreSQL migrations, because EF Core allows only one model snapshot per context per assembly ([ADR-0004](../../docs/adr/0004-postgres-provider-support.md)).

- `MetricsDbContext` (root of this project): `20260707164258_InitialCreate` (+ `.Designer`) — the frozen baseline. Its **name must never change** (ADR-0003): `DatabaseInitializer` stamps legacy `EnsureCreated` databases against exactly this id. `MetricsDbContextModelSnapshot` is its snapshot.
- `WhiskersIdentityDbContext` (under `Identity/`, F1 local auth): the ASP.NET Identity user schema, applied at boot into its **own `__IdentityMigrationsHistory`** table so it never touches the metrics baseline path. `WhiskersIdentityDbContextModelSnapshot` is its snapshot.

The app pins this assembly with `MigrationsAssembly("Whiskers.Migrations.Sqlite")` for both contexts.

Scaffold a new migration — `--context` is **mandatory** now (two contexts share this assembly); SQLite is the default provider (no env var):
```
# metrics
dotnet ef migrations add <Name> --context MetricsDbContext --project src/Whiskers.Migrations.Sqlite --startup-project src/Whiskers
# identity (keep under Identity/)
dotnet ef migrations add <Name> --context WhiskersIdentityDbContext --project src/Whiskers.Migrations.Sqlite --startup-project src/Whiskers --output-dir Identity
```

References `Whiskers.Data` only — never the app, so there is no circular dependency.
