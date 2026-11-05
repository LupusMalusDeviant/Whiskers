# Upgrading Whiskers' own database from PostgreSQL 17 to 18

This applies only if you run Whiskers on PostgreSQL with the overlay `deploy/docker-compose.postgres.yml`. SQLite
installs (the default) and Helm installs with an external database are not affected.

## What changed and why there is a step to take

The overlay now runs `postgres:18-alpine`. PostgreSQL does not read the data directory of an older major version, and
the 18 image also refuses the old mount point `/var/lib/postgresql/data`, so the data moves:

| | Before (17) | Now (18) |
|---|---|---|
| Volume | `whiskers-db-data` | `whiskers-db18-data` |
| Mount point | `/var/lib/postgresql/data` | `/var/lib/postgresql` (data in `18/docker` below it) |

The 17 volume is never written by the new overlay. It stays as your way back until you remove it yourself.

**If you just pull the new overlay and run `up`, nothing breaks and nothing is lost:** the service `db-upgrade-guard`
sees PostgreSQL 17 data next to an empty 18 volume and stops the start with:

```
whiskers: PostgreSQL 17 data found in the volume whiskers-db-data, and the PostgreSQL 18 volume whiskers-db18-data is empty.
whiskers: not starting an empty database. Follow docs/operations/postgres-18-upgrade.md (dump, restore, start).
```

The database and Whiskers stay down until you do the steps below. A fresh install (no 17 data) starts directly on 18.

## The upgrade: dump on 17, restore on 18

The way is a logical dump and restore. It is version-independent and was tested end to end:

- 17.11 → 18.6;
- data, migration history and sequences carried over;
- the guard letting the start through afterwards;
- rollback to 17.

Plan a few minutes of downtime. Whiskers' database is small; the dump of a busy install is usually a few megabytes.

The commands below use `C` for your compose command. Run them from the directory with your `docker-compose.yml`:

```sh
C="docker compose -f docker-compose.yml -f deploy/docker-compose.postgres.yml"
```

They need the previous overlay, the PostgreSQL 17 one, for the dump.

- **If you have not updated the files yet:** copy it before you do:
  ```sh
  cp deploy/docker-compose.postgres.yml deploy/docker-compose.postgres17.yml
  ```
- **If you already updated:** take it from the last release before the change, for example:
  ```sh
  git show v0.15.0:deploy/docker-compose.postgres.yml > deploy/docker-compose.postgres17.yml
  ```
  You can also download that file from the v0.15.0 release on GitHub.

```sh
C17="docker compose -f docker-compose.yml -f deploy/docker-compose.postgres17.yml"
```

### 1. Stop Whiskers and dump the 17 database

```sh
$C17 stop serverwatch
$C17 up -d --no-deps --wait db
$C17 exec -T db pg_dump -U "${WHISKERS_DB_USER:-whiskers}" -d "${WHISKERS_DB_NAME:-whiskers}" \
    --no-owner --no-privileges > whiskers-pg17.sql
$C17 stop db
```

- **Check the dump:** it should not be empty and should end with `PostgreSQL database dump complete`:
  ```sh
  tail -n 3 whiskers-pg17.sql
  ```
- **Keep it private:** it holds your whole database, including the encrypted vault entries, so keep it as private
  as a backup:
  ```sh
  chmod 600 whiskers-pg17.sql
  ```

### 2. Start PostgreSQL 18 alone and restore

```sh
$C up -d --no-deps --wait db
$C exec -T db psql -U "${WHISKERS_DB_USER:-whiskers}" -d "${WHISKERS_DB_NAME:-whiskers}" \
    -v ON_ERROR_STOP=1 --single-transaction -q < whiskers-pg17.sql
```

- `--no-deps` starts the database without `db-upgrade-guard`. The guard would still refuse, because the 18 volume is
  empty until this restore.
- `--single-transaction` with `ON_ERROR_STOP` makes the restore all or nothing. If it reports an error, nothing was
  written. Fix the cause and run it again on the still-empty database.

### 3. Start everything

```sh
$C up -d
```

The guard now finds the 18 volume filled and lets the start through. Whiskers starts on the restored database and
applies any newer migrations as usual.

- **Check:** sign in, look at the audit log and a few pages.
- **Optional, the server version:**
  ```sh
  $C exec db psql -U "${WHISKERS_DB_USER:-whiskers}" -d "${WHISKERS_DB_NAME:-whiskers}" -tAc 'select version()'
  ```

## Going back to 17

The 17 volume was only read. To return to it:

```sh
$C stop
$C17 up -d
```

Anything written after the upgrade is in the 18 volume only.

## Cleaning up

Once you are sure, remove the dump file and the 17 volume. The volume's full name carries your compose project as a
prefix; `docker volume ls` shows it.

```sh
rm whiskers-pg17.sql
docker volume rm <project>_whiskers-db-data
```

The overlay still declares `whiskers-db-data`, so `up` creates it again, empty. An empty 17 volume is harmless: the
guard only stops a start when that volume holds a cluster.

## Other ways

- **Whiskers' self-backup** (Settings → Backup, `docs/operations/backup-and-recovery.md`) is a good extra copy
  before you start; the image's client tools are PostgreSQL 18 and back up a 17 server. Restoring a backup taken on 17
  into 18 is not part of the tested path. The dump above is that path, and it does not need Whiskers running.
- **`pg_upgrade`** needs the binaries of both versions in one container. It is not covered here. The new mount point
  at `/var/lib/postgresql` is what lets a later major upgrade use `pg_upgrade --link` inside one volume.
