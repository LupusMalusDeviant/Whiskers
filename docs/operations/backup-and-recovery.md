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
recreate it: it must own the `public` schema (on PostgreSQL 15 and later the database owner does) and hold `CREATE` on the database. A role without that right still
takes backups, but the restore is refused at its dry run, before anything changes. Check it once after setting up
the database, by restoring a backup.

Give Whiskers a database of its own and a dedicated role that owns it, not a superuser. The PostgreSQL overlay
`deploy/docker-compose.postgres.yml` connects as the role `POSTGRES_USER` creates, which is a superuser; for
production, create an owner role without superuser rights and connect as it:
`CREATE ROLE whiskers_app LOGIN PASSWORD '…'; ALTER DATABASE whiskers OWNER TO whiskers_app;
ALTER SCHEMA public OWNER TO whiskers_app;` (and `REASSIGN OWNED BY whiskers TO whiskers_app;` in that database for
the existing tables). A restore runs the archive's
SQL with that role's rights; a superuser (or a member of `pg_execute_server_program`) could be made to run commands on
the database host by a crafted archive, and without `VAULT_KEY` archives are not authenticated. The `public` schema
must hold Whiskers' tables only: a restore drops everything in it, and the dump brings back only what Whiskers put
there. Extensions in `public`: one the role does not own (installed by a DBA or superuser) cannot be dropped with the
schema, so the dry run refuses the restore; move it to another schema first (`ALTER EXTENSION … SET SCHEMA`). One
the role owns is dropped with the schema and not recreated: install it again after the restore. Whiskers' tables must be in
`public`: a backup whose dump does not hold them (for example because a `Search Path` in the connection string put
them elsewhere) fails instead of writing an archive without the database.

## Recovery point and time

- **Recovery point (RPO):** the newest archive you still have. With a daily *Self-backup* task you can lose up to a
  day of changes: metrics, audit events and settings. The restored database does not contain audit events written
  after the archive was taken; the pre-restore safety backup still holds them.
- **Recovery time (RTO):** start a Whiskers container, upload the archive and wait for one restart. On PostgreSQL,
  the database is restored during that restart, in one transaction, before Whiskers answers its health check. That
  takes about as long as the dry run did. On Kubernetes the Helm chart's startup probe allows five minutes
  (`probes.startup.periodSeconds` × `probes.startup.failureThreshold`); for a larger database raise
  `probes.startup.failureThreshold` before the restore, or the pod is restarted mid-restore, the transaction rolls
  back and the restore repeats at every start.

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
2. Whiskers checks the archive before it commits anything:
   - the archive's entries;
   - its schema against this build;
   - the stores and the key files;
   - on SQLite, it opens the database and runs the migrations on a copy;
   - on PostgreSQL, it first takes the **pre-restore safety backup**, then reads the dump with `pg_restore` and does
     a dry run: it applies the dump to the database in a transaction and rolls the transaction back. The dry run
     executes the archive's SQL with the role's rights, so a crafted archive can change the database before it is
     refused; the safety backup taken first is what holds the state from before. A script that ends the
     transaction on its own is detected: the restore is refused with "ended its transaction early"; then follow
     [After a restore refused for an early commit](#after-a-restore-refused-for-an-early-commit). Inside that transaction it also checks that the migration
     histories match the archive's manifest, runs the migrations the next start would run, and reads every table and
     column this version uses. The database is locked while the dry run runs, so metric collection pauses for that
     time; on a large database that can take minutes. The dry run writes the whole dataset once and rolls it back:
     the database server needs about the database's size in free disk space (plus WAL) for it, which it reclaims
     afterwards. A dry run that meets a running scheduled self-backup (its `pg_dump` holds a lock on every table)
     fails after a minute with a lock timeout; restore again once the backup has finished.
3. On SQLite Whiskers now takes the **pre-restore safety backup**. Then it commits the restore and stops.
4. The container restart (through the Docker restart policy, systemd or Kubernetes) applies the restore:
   - On PostgreSQL, the `public` schema is dropped and recreated from the dump in **one transaction**. If any step
     fails, the whole transaction is rolled back and the start stops. The restore stays pending and runs again at
     the next start.
   - Then the files are swapped.
5. To give up a pending restore, remove `/app/data/.restore-pending`. The pre-restore safety backup holds the state
   from before the restore. On PostgreSQL, if the start's log already says "The PostgreSQL database was restored from
   the archive", the database is the archive's while the files are not yet: after giving up, restore the pre-restore
   safety backup so both match again.

## Upgrades

Each start checks whether it is an upgrade: another release than the one that last finished its start (a rebuild of
the same release, which differs only after the `+` of its version, is not), or schema migrations pending in the
database. A restore staged from the command line resets this, so the next start of a newer release is an upgrade
again. Before an upgrade changes anything, Whiskers takes a self-backup labelled
`pre-upgrade-<date>`:

1. It is taken right after the vault has checked `VAULT_KEY`, before anything rewrites data in a newer format: the
   server list, the move of credentials into the vault, the data-protection key ring and the database schema. So the
   backup holds the database and the files **as the previous release left them**, with one exception: `vault.json`,
   which the vault's own check may update first (it adds its key check, and converts entries of a vault from before
   R1-28; the untouched original then stays beside it as `vault.json.legacy-cbc.bak`). The log names the backup's id.
2. When that backup cannot be taken, for example for lack of space, a wrong `VAULT_KEY` or missing PostgreSQL client
   tools, the start **stops before anything is changed**. Fix the cause and start again. Only if you have a recovery
   point of your own, set `WHISKERS_UPGRADE_SKIP_BACKUP=true` to upgrade without the backup (the log warns about it
   at every such start). An invalid value of either upgrade setting also stops the start, before a backup is taken.
3. Then the migrations run. The log shows how long the backup and each schema took. When both schemas are current,
   the upgrade is finished and the release is recorded as the one that last started.

Until then the upgrade counts as unfinished. A start after a failed migration (or after the orchestrator killed the
container mid-upgrade) takes **no new backup**: it keeps the one from before the upgrade, which stays the way back,
and the log says so. The scheduled self-backup retention does not delete pre-upgrade backups; Whiskers keeps the
newest three (`WHISKERS_UPGRADE_KEEP_BACKUPS`), deletes older ones when it takes a new one, and never the one an
unfinished upgrade depends on, nor any while an unfinished upgrade's own backup is unknown. A new database gets no
pre-upgrade backup: there is nothing in it yet (also when only the database file was removed from an existing data
directory; back the directory up yourself before you do that).

The backup and the migrations take longer as the database grows, and nothing answers the health check meanwhile.
The image's health check waits five minutes; on Kubernetes the Helm startup probe allows five minutes
(`probes.startup.*`). Raise both for a large database, or the orchestrator restarts the container mid-upgrade.

### When a migration fails

The start stops and the log says which pre-upgrade backup to restore. The database may be partly migrated. Either fix
the cause and start this release again (it keeps the same pre-upgrade backup), or go back to the previous release.
You do not need the UI for that. Stop Whiskers, then run the restore from the command line with **this** release's
image:

```bash
docker compose run --rm serverwatch --restore-backup latest-pre-upgrade --for-previous-release
```

`serverwatch` is the service name in the shipped compose file. `latest-pre-upgrade` is the backup of the unfinished
upgrade (else the newest pre-upgrade backup); `--restore-backup <id>` picks a specific archive, and without an id the
command lists them. Outside a container it is `dotnet Whiskers.dll --restore-backup …`; on Kubernetes, scale the
deployment to 0 and run the image once with the same volume, environment and arguments.

The command checks the archive like **Settings > Backup** does (its entries, the key, the provider, the database's
integrity and migration history), takes a safety backup of the current state, on PostgreSQL does the dry run, stages
the restore and exits; the next start applies it. `--for-previous-release` leaves out one check: this release's
migrations are not run on the archive, because that may be exactly what fails. Without it the archive is staged for
this release and must migrate. A restore that is already staged is not applied by the command; it refuses until a
start has applied it (or you remove `.restore-pending` to give it up).

### Downgrade

An older release does not run on a database a newer release has migrated: it does not know the newer schema, and
its restore refuses an archive with a newer schema. A downgrade is therefore always **the previous release on the
matching pre-upgrade backup**:

1. Stop Whiskers.
2. With the **newer** image, stage the pre-upgrade backup:
   `--restore-backup latest-pre-upgrade --for-previous-release` (or its id).
3. Start the **previous** image on the same data directory and database. It applies the restore before it opens the
   database, and starts on the schema and the files it knows.

Data written after the upgrade is not in the pre-upgrade backup; the safety backup taken in step 2 holds it, but only
the newer release can restore that one. A previous release from before R1-23 writes no `selfbackup.restored` audit
event for the restore it applies.

On PostgreSQL, step 3 needs a previous release that restores the database at its start (the first release with
PostgreSQL database backups, R1-33, or later). An older release only swaps the files and would start on the newer
schema; Whiskers' own tools cannot take a PostgreSQL instance back that far, so keep a `pg_dump` of your own before
such an upgrade.

## After a restore refused for an early commit

A dump whose script ends the dry run's transaction on its own (its own `COMMIT`) may already have committed part of
its work, for example the dropped and recreated `public` schema. Whiskers then may not start or let anyone sign in,
and a restore from **Settings > Backup** fails, because it first takes a new safety backup of the damaged database.
Recover from the pre-restore safety backup taken just before the dry run, the same way as after a lost volume:

1. Stop Whiskers. Copy the newest `pre-restore-*` archive out of `backups/self/` in the data directory. If Whiskers
   can no longer connect at all, the script may have changed the role's own password (a role may always do that);
   reset it as a database administrator (`ALTER ROLE … PASSWORD …`) first.
2. Give Whiskers an empty database: point `WHISKERS_DB_CONNECTION` at a new database, or drop and recreate the
   `public` schema of the current one as its owner.
3. Move the data directory aside and follow
   [Restore after the volume is lost](#restore-after-the-volume-is-lost-off-volume-recovery) with the copied archive.
4. A crafted script may also have left its own session running (a long sleep under its own statement timeout)
   after Whiskers stopped it: look for `application_name = 'whiskers-backup'` in `pg_stat_activity` and end it with
   `pg_terminate_backend(pid)`.
5. If the server allows prepared transactions (`max_prepared_transactions` above 0), check `pg_prepared_xacts`: a
   crafted script may have left one holding locks; end it with `ROLLBACK PREPARED '<gid>'`.
6. Delete the archive that caused the refusal, and do not restore archives from a source you do not trust
   (set `VAULT_KEY`, so that only archives sealed with it are accepted).

## Restore after the volume is lost (off-volume recovery)

You need:

- the archive, from your copy off the volume;
- `VAULT_KEY`;
- on PostgreSQL, a database that Whiskers may replace.

1. Deploy Whiskers as before, with the same image version or a newer one; an older build refuses an archive with a
   newer schema. Use the **same `VAULT_KEY`**. On PostgreSQL, point `WHISKERS_DB_CONNECTION` at the database to
   restore into; a new, empty database is fine. The connecting role must be allowed to drop and recreate the
   `public` schema: it must own the `public` schema (on PostgreSQL 15 and later the database owner does) and hold `CREATE` on the database.
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
  the environment variable `WHISKERS_PG_BIN_DIR` to their directory; the image sets it. It is read from the
  environment only, never from the settings files in the data directory. Without them, a backup
  of a PostgreSQL instance fails; Whiskers never writes an archive without the database. Each tool run may take
  30 minutes; set `WHISKERS_PG_TIMEOUT_MINUTES` (environment only) for a larger database.
- The restore script goes straight from `pg_restore` to `psql` and is never written to disk. Whiskers runs it only
  when it opens with `\restrict`, the protection of client tools 17.6 and newer, so a crafted dump cannot run psql
  meta-commands such as `\!` (which would run a shell command). Older tools are refused for a restore.
- Whiskers passes the password to the tools through their environment, never on their command line. `PG*`
  variables inherited from Whiskers' own environment are not passed on, except `PGPASSWORD` (when the connection
  string has no password), `PGUSER` (when it has no user) and `PGPASSFILE`, which Whiskers itself would use as well.
  `PGSSLCERT`, `PGSSLKEY`, `PGSSLROOTCERT` and `PGTARGETSESSIONATTRS` are passed on when the connection string
  does not set them, as Whiskers reads them too. An inherited `PGOPTIONS` is refused like `Options`. A bare IPv6
  address in `Host` is one host. Host, port, database, user,
  password, password file, SSL mode, root certificate, client certificate and key, target session attributes and the
  connect timeout and channel binding are taken over from the connection string; a connection string with `Options`
  is refused for backups (set those server settings on the role or the database). With `SSL Mode=VerifyCA` or `VerifyFull` and no root
  certificate, the tools verify against the system's trusted roots, as Whiskers does; `Require` encrypts without
  verifying in both. An encrypted client key (`SSL Password`) and several
  hosts in `Host` are not supported for backups. Of the tools' error output, only the error lines are logged; lines
  that quote a row or a statement, and quoted values, are left out. The restore at the start reads the database
  settings from the same places as the app (environment, `appsettings.json` and the data directory's settings
  files).
- The dump leaves out ownership and grants (`--no-owner --no-acl`). Restored objects belong to the role Whiskers
  connects as. If other roles had grants, for example a read-only reporting user, grant them again after the
  restore. Default privileges for the role Whiskers connects as (`ALTER DEFAULT PRIVILEGES FOR ROLE … GRANT`) would
  give every restored table grants, which the restore refuses: remove them before a restore and grant again after
  it. A dump that brings grants of its own, or unlogged or foreign tables, into `public` is refused, and so is
  one that adds default privileges or publications.
- The dump covers the `public` schema only. Objects in other schemas of the same database are not touched unless
  they depend on objects in `public` (a view or foreign key on Whiskers' tables is dropped with them). A restore
  that creates, drops or changes a schema, relation, function (its code or settings), trigger, rule, policy or
  operator in another schema, default privileges, a publication, or a role or database setting is refused: a schema
  named like the role comes first in the default search path and its tables would shadow Whiskers' own. Data in
  other schemas is not compared. `pg_db_role_setting` is shared by the whole server: on a shared server, an
  `ALTER ROLE … SET` by someone else during the dry run or the restore makes it fail; restore again;
  extensions are not part of the dump (Whiskers' migrations create none; a test keeps it that way).
- Whiskers' migrations create tables, sequences, indexes and constraints only. A dump that brings functions,
  triggers, rules, views, row policies or operators into `public` is refused, because they would run with Whiskers'
  rights on its reads and writes.
- A restore runs the archive's SQL with the rights of the role Whiskers connects as. Beyond what the checks above
  refuse, a crafted archive could still change what a role may always change about itself, such as its own
  password; Whiskers would then fail to connect after the restart. Restore only archives you trust, keep
  `VAULT_KEY` set so that only archives sealed with it are accepted, and if connecting fails after a restore, reset
  the role's password as a database administrator.
- Whiskers takes a restore as done only when psql ran the end of the script (its checks and its `COMMIT`) and printed
  a marker unknown to the archive; a script that leaves psql reading data, for example inside a `COPY` block, is
  refused even when psql ends without an error.
- The dump is written in UTF-8 (`pg_dump --encoding=UTF8`), whatever the database's encoding; a restore stops at a
  byte that is not valid UTF-8 instead of replacing it.
