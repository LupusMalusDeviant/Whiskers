# Services/Database

Detect and operate **databases running inside managed containers**: recognise the engine (Postgres, MySQL/MariaDB, ...) from a container, then list databases/tables, inspect schema, run queries, and trigger backups.

## Files

| File | Purpose |
|---|---|
| `IDatabaseService.cs` / `DatabaseService.cs` | Connects to a database inside a container and exposes list/schema/query/backup operations. |
| `DatabaseDetector.cs` | Detects the database engine/type from a container image name. |

**Backup durability (2026-07):** `BackupDatabaseAsync` dumps to the DB container's `/tmp`, then **copies the dump out to the host** (`/app/data/backups`, via `docker cp`) and removes the in-container copy. Previously the dump stayed in the container's `/tmp` and was silently lost on the next container recreate (image update / redeploy). The returned path is the durable host path.

## Related

- Container access: [`../Docker/`](../Docker/)
- UI: the *Database* tab in [`../../Components/Pages/ContainerDetail.razor`](../../Components/Pages/ContainerDetail.razor)
- MCP tools: `detect_database`, `list_databases`, `list_tables`, `get_schema`, `execute_query`, `backup_database`
