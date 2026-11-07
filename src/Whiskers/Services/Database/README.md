# Services/Database

Detect and operate **databases running inside managed containers**: recognise the engine (Postgres, MySQL/MariaDB, ...) from a container, then list databases/tables, inspect schema, run queries, and trigger backups.

## Files

| File | Purpose |
|---|---|
| `IDatabaseService.cs` / `DatabaseService.cs` | Connects to a database inside a container and exposes list/schema/query/backup operations. R1-42: `docker exec -- <container>` — the container reference is checked by `CommandOperands` and follows `--`. R1-48: an omitted `serverId` means Whiskers' own host — declared once on the interface (`[OmittedServer(OmittedServer.Local)]`) and read by the implementation through `OmittedServer.For<IDatabaseService>`, as the scope guard does. INV-19: every database name — of a listing, a query, a dump (and so of the dump's host file name) — passes `CheckDatabaseName` (`CommandOperands.RequireDatabaseName`; for MongoDB also no `.`) before any command is built; the database panel, `execute_query` and the scheduled DB backup check it before they call the service too. |
| `DatabaseDetector.cs` | Detects the database engine/type from a container image name. |
| `DbDumpFiles.cs` | The host file name of a dump: `{container}@{db}_{yyyyMMdd}_{HHmmssfff}_{8 hex}.sql` (`.gz` for MongoDB), the container being its name (its short id when the name is unusable or longer than 64); the milliseconds and the random suffix keep two dumps in the same second apart. Picks the dumps of exactly one container and database from an `ls -1Aq` listing (each name once, never trimmed, newest first by the name's time stamp) for the scheduled DB-backup retention and its verifier, so a same-named database in another container or `app_db` for `app` is never touched. Dumps in the format before (`{db}_{yyyyMMdd}_{HHmmss}.sql[.gz]`) are recognised as legacy (`IsLegacyDumpOf`) and never counted or pruned. |

**Backup durability (2026-07):** `BackupDatabaseAsync` dumps to the DB container's `/tmp`, then **copies the dump out to the host** (`/app/data/backups`, via `docker cp`) and removes the in-container copy. Previously the dump stayed in the container's `/tmp` and was silently lost on the next container recreate (image update / redeploy). The returned path is the durable host path.

**Secret hygiene (2026-07):** database passwords are passed to the CLI via environment variables (`MYSQL_PWD` / `PGPASSWORD` / `NEO4J_PASSWORD`) under `sh -c`, never as a `-p<pw>` argv, so they don't appear in the container's process list. The host-command debug logs are redacted (`SecretRedactor`).

## Related

- Container access: [`../Docker/`](../Docker/)
- UI: the *Database* tab in [`../../Components/Pages/ContainerDetail.razor`](../../Components/Pages/ContainerDetail.razor)
- MCP tools: `detect_database`, `list_databases`, `list_tables`, `get_schema`, `execute_query`, `backup_database`
