# Whiskers.Data

The shared EF Core data layer, extracted into its own assembly so the per-provider migration projects can reference it without a circular dependency on the app ([ADR-0004](../../docs/adr/0004-postgres-provider-support.md)).

Contains:

- **`MetricsDbContext`** — the single `DbContext`, with 15 `DbSet`s (metrics, alerts, audit, MCP tool calls, volume backups, scheduler + run history, log-alert rules, update/webhook policies + history, CVE first-seen, notifications).
- **The 15 entity types** (`Entities/`) — kept in their **original namespaces** (`Whiskers.Services.Persistence`, `Whiskers.Models`, `Whiskers.Models.Cve`) so no consumer `using` had to change when they moved here. That is why the extraction was a pure move with zero churn at the ~20 call sites.
- **`UtcDateTimeConverter`** — normalizes every `DateTime` to UTC on write and marks it UTC on read, registered globally via `ConfigureConventions`. SQLite is unaffected (still UTC text); PostgreSQL's `timestamptz` requires it, since Npgsql rejects `Unspecified`/`Local` kinds.

Depends only on `Microsoft.EntityFrameworkCore` (base). The concrete providers (SQLite / Npgsql) and the migrations live in the app and the `Whiskers.Migrations.*` projects, **not** here — this project stays provider-agnostic.

## Related

- Provider registration + startup migrate: [`../Whiskers/Services/Persistence/`](../Whiskers/Services/Persistence/)
- Migrations: [`../Whiskers.Migrations.Sqlite/`](../Whiskers.Migrations.Sqlite/) · [`../Whiskers.Migrations.Postgres/`](../Whiskers.Migrations.Postgres/)
