# Backup and recovery

This guide covers how Whiskers backs itself up, what a backup holds, and how to get an instance back, either on
the same host or on a new one after the data volume is gone. Whiskers 1.0 runs as a single instance and has no
replica to fail over to, so the recovery plan is a backup kept somewhere else.

## What a self-backup holds

A self-backup is one archive. You create it under **Settings > Backup** or with a scheduled *Self-backup* task. The
archive holds the database **and** the files from the same moment, so they always match:

| Instance | Database in the archive | Files in the archive |
|---|---|---|
| SQLite (default) | `metrics.db`, a `VACUUM INTO` snapshot | the stores and directories of the data directory (`/app/data`): roles, whitelist, vault, servers, settings, keys, SSH keys, mTLS material, chat history and so on |
| PostgreSQL | `database.pgdump`, a `pg_dump` (custom format) of the `public` schema. That schema holds the metrics tables and the sign-in tables, each with its migration history. | the same files |

With `VAULT_KEY` (or `VAULT_KEY_FILE`) set, the archive is encrypted with AES-256-GCM, using a key derived from
`VAULT_KEY`. Whiskers restores only an archive sealed with that key. **Without the key a backup cannot be
restored.** Keep `VAULT_KEY` in a password manager as well as on the host.

A backup does **not** hold:

- the volumes of your containers (use volume backups for those);
- the `.env` and compose files of the Whiskers deployment itself;
- `VAULT_KEY`.

Backups of a PostgreSQL instance made before R1-33 hold the files only. The backup list marks them **files only**,
and Whiskers does not restore them, because their files would no longer match the current database.

On PostgreSQL a restore replaces the `public` schema, so the role Whiskers connects as must be allowed to drop and
recreate it: it must own the schema, or on PostgreSQL 15 and later own the database. A role without that right still
takes backups, but the restore is refused at its dry run, before anything changes. Check it once after setting up
the database, by restoring a backup.

## Recovery point and time

- **Recovery point (RPO):** the newest archive you still have. With a daily *Self-backup* task you can lose up to a
  day of changes: metrics, audit events and settings. The restored database does not contain audit events written
  after the archive was taken; the pre-restore safety backup still holds them.
- **Recovery time (RTO):** start a Whiskers container, upload the archive and wait for one restart. On PostgreSQL,
  the database is restored during that restart, in one transaction.

## Keep a copy off the volume

Whiskers writes archives to `backups/self/` **on the data volume**. If you lose the volume, you lose them too. Keep
a copy somewhere else:

- Download them from **Settings > Backup**. Each download is recorded in the audit log before the first byte is
  sent.
- Or copy `/app/data/backups/self/*.tar.gz.enc` and the matching `*.meta.json` files off the host with your
  file-level backup (borg, restic, or rsync to another machine).

An encrypted archive is safe to keep on another machine, because it is useless without `VAULT_KEY`.

## Restore on the same instance

1. Open **Settings > Backup**, pick an archive (or upload one) and restore it.
2. Whiskers checks the archive before it changes anything:
   - the archive's entries;
   - its schema against this build;
   - the stores and the key files;
   - on SQLite, it opens the database and runs the migrations on a copy;
   - on PostgreSQL, it reads the dump with `pg_restore` and does a dry run: it applies the dump to the database in a
     transaction and rolls the transaction back. Inside that transaction it also checks that the migration
     histories match the archive's manifest, runs the migrations the next start would run, and reads every table and
     column this version uses. The database is locked while the dry run runs, so metric collection pauses for that
     time; on a large database that can take minutes.
3. Whiskers takes a **pre-restore safety backup**, commits the restore and stops.
4. The container restart (through the Docker restart policy, systemd or Kubernetes) applies the restore:
   - On PostgreSQL, the `public` schema is dropped and recreated from the dump in **one transaction**. If any step
     fails, the whole transaction is rolled back and the start stops. The restore stays pending and runs again at
     the next start.
   - Then the files are swapped.
5. To give up a pending restore, remove `/app/data/.restore-pending`. The pre-restore safety backup holds the state
   from before the restore.

## Restore after the volume is lost (off-volume recovery)

You need:

- the archive, from your copy off the volume;
- `VAULT_KEY`;
- on PostgreSQL, a database that Whiskers may replace.

1. Deploy Whiskers as before, with the same image version or a newer one; an older build refuses an archive with a
   newer schema. Use the **same `VAULT_KEY`**. On PostgreSQL, point `WHISKERS_DB_CONNECTION` at the database to
   restore into; a new, empty database is fine. The connecting role must be allowed to drop and recreate the
   `public` schema: it must own that schema, or on PostgreSQL 15 and later own the database.
2. Start it once. Whiskers creates an empty data directory and migrates the new database. Complete the setup wizard
   with a throwaway admin account; the restore replaces all accounts.
3. Sign in, open **Settings > Backup**, upload the archive and restore it. Whiskers validates it, restarts and
   applies it.
4. Sign in with an account from the backup. The restore appears in the audit log as a `selfbackup.restored` event.

The `PostgresBackupRestoreTests` run this procedure against a real PostgreSQL server. They back up one instance,
restore it into a fresh database and an empty data directory, and check that the sign-in accounts, scheduled tasks,
webhooks and audit rows come back.

## PostgreSQL notes

- The image ships the PostgreSQL client tools `pg_dump`, `pg_restore` and `psql` in version 18 from the PostgreSQL
  project. The tests restore with them into PostgreSQL 14 and 17 servers. If you run Whiskers outside the image,
  install the tools (17.6 or newer, at least as new as the server) and either put them on `PATH` or set
  `WHISKERS_PG_BIN_DIR` (or `Backup:PostgresBinDir`) to their directory. Without them, a backup of a PostgreSQL
  instance fails; Whiskers never writes an archive without the database.
- The restore script goes straight from `pg_restore` to `psql` and is never written to disk. Whiskers runs it only
  when it opens with `\restrict`, the protection of client tools 17.6 and newer, so a crafted dump cannot run psql
  meta-commands such as `\!` (which would run a shell command). Older tools are refused for a restore.
- Whiskers passes the password to the tools through their environment, never on their command line. `PG*`
  variables inherited from Whiskers' own environment are not passed on, except `PGPASSWORD` (when the connection
  string has no password) and `PGPASSFILE`, which Whiskers itself would use as well. Host, port, database, user,
  password, password file, SSL mode, root certificate, client certificate and key, target session attributes and the
  connect timeout are taken over from the connection string; an encrypted client key (`SSL Password`) is not
  supported for backups. Of the tools' error output, only the error lines are logged; lines that quote a row or a
  statement are left out.
- The dump leaves out ownership and grants (`--no-owner --no-acl`). Restored objects belong to the role Whiskers
  connects as. If other roles had grants, for example a read-only reporting user, grant them again after the
  restore.
- The dump covers the `public` schema only. Objects in other schemas of the same database are not touched, and
  extensions are not part of the dump (Whiskers' migrations create none; a test keeps it that way).
