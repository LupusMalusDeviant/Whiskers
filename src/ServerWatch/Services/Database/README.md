# Services/Database

Detect and operate **databases running inside managed containers**: recognise the engine (Postgres, MySQL/MariaDB, …) from a container, then list databases/tables, inspect schema, run queries, and trigger backups.

## Files

| File | Purpose |
|---|---|
| `IDatabaseService.cs` / `DatabaseService.cs` | Connects to a database inside a container and exposes list/schema/query/backup operations. |
| `DatabaseDetector.cs` | Detects the database engine/type from a container image name. |

## Related

- Container access: [`../Docker/`](../Docker/)
- UI: the *Database* tab in [`../../Components/Pages/ContainerDetail.razor`](../../Components/Pages/ContainerDetail.razor)
- MCP tools: `detect_database`, `list_databases`, `list_tables`, `get_schema`, `execute_query`, `backup_database`
