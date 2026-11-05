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

The full volume names carry your compose project as a prefix, for example `whiskers_whiskers-db-data`.
`docker volume ls` shows them.

The new overlay mounts the 17 volume read-only and never writes to it. The upgrade itself does run PostgreSQL 17 on it
once more, read-write, to take the dump (step 1), and so does a rollback. The way back is therefore your data as of
the dump, not a volume nobody has touched since you pulled.

## The guard

The service `db-upgrade-guard` runs before the database and decides whether PostgreSQL 18 may start:

| The 17 volume `whiskers-db-data` | The 18 volume `whiskers-db18-data` | Result |
|---|---|---|
| empty (a fresh install, or you removed the 17 volume) | anything | starts |
| a 17 cluster | no confirmed restore | **refused** (a blind pull) |
| a 17 cluster | a restore confirmed for exactly this 17 data | starts |
| a 17 cluster that changed after the confirmation | a confirmed restore of the older state | **refused** (after a rollback) |
| something else, or unreadable | anything | **refused** |

A restore counts as confirmed once step 2 below has checked it: the guard's confirm run finds Whiskers' migration
history in the running 18 database and then writes a fingerprint of the 17 cluster (`global/pg_control`) into the 18
volume. Starting PostgreSQL 17 again changes that file, so after a rollback the guard no longer accepts the older 18
copy.

**If you just pull the new overlay and run `up`, nothing breaks and nothing is lost.** The database and Whiskers stay
down, and `docker compose … logs db-upgrade-guard` shows:

```
whiskers: PostgreSQL 17 data found in the volume whiskers-db-data, and the PostgreSQL 18 volume whiskers-db18-data holds no confirmed restore of it.
whiskers: not starting an empty database. Follow docs/operations/postgres-18-upgrade.md (dump, restore, confirm, start).
whiskers: The volumes are whiskers-db-data (PostgreSQL 17) and whiskers-db18-data (PostgreSQL 18); docker volume ls shows them with your compose project as a prefix.
```

## The upgrade: dump on 17, restore on 18

The way is a logical dump and restore. It is version-independent, and the test suite runs it on every change
(`PostgresUpgradeTests`): Whiskers' schema and data on 17, the dump and restore below, the confirm run. It was also run
end to end with Docker by hand: a blind pull refused, the upgrade, a rollback followed by a refused 18 start, and a
fresh install.

Plan a few minutes of downtime. Whiskers' database is small; the dump of a busy install is usually a few megabytes.

Run the commands from the directory with your `docker-compose.yml`, in `sh`, `bash` or `zsh`. Two shell functions stand
for the compose command with the new and with the previous overlay:

```sh
c()   { docker compose -f docker-compose.yml -f deploy/docker-compose.postgres.yml "$@"; }
c17() { docker compose -f docker-compose.yml -f deploy/docker-compose.postgres17.yml "$@"; }
```

`c17` needs the previous overlay, the PostgreSQL 17 one, for the dump.

- **If you have not updated the files yet:** copy it before you do:
  ```sh
  cp deploy/docker-compose.postgres.yml deploy/docker-compose.postgres17.yml
  ```
- **If you already updated:** take it from the last release before the change, for example:
  ```sh
  git show v0.15.0:deploy/docker-compose.postgres.yml > deploy/docker-compose.postgres17.yml
  ```
  You can also download that file from the v0.15.0 release on GitHub.

The database user, database name and password come from your `.env` as before. The commands below read user and
database inside the container (`$POSTGRES_USER`, `$POSTGRES_DB`, in single quotes), so they match whatever your `.env`
sets.

### 1. Stop Whiskers and dump the 17 database

```sh
c17 stop serverwatch
c17 up -d --no-deps --wait db
(umask 077; c17 exec -T db sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner --no-privileges' > whiskers-pg17.sql)
c17 stop db
```

- `umask 077` creates the dump readable by you only. It holds your whole database, including password hashes and the
  encrypted vault entries; keep it as private as a backup.
- This is the last time anything writes to the 17 volume, unless you go back.

### 2. Start PostgreSQL 18 alone, restore and confirm

```sh
c up -d --no-deps --wait db
grep -q 'PostgreSQL database dump complete' whiskers-pg17.sql \
  && c exec -T db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 --single-transaction -q' < whiskers-pg17.sql \
  && c run --rm --no-deps -e WHISKERS_CONFIRM_RESTORE=1 db-upgrade-guard
```

- `--no-deps` starts the database without `db-upgrade-guard`, which still refuses at this point.
- The chain stops at the first failure:
  - **`grep`** refuses an empty or cut-off dump.
  - **`psql`** restores all or nothing (`--single-transaction` with `ON_ERROR_STOP`). It prints a few `set_config` and
    `setval` results; that is normal. If it reports an error, nothing was written. Fix the cause and run the chain
    again on the still-empty database.
  - **The confirm run** checks over the network that the 18 database holds Whiskers' migration history, and only then
    writes the confirmation into the 18 volume. It ends with
    `whiskers: restore confirmed: <n> migrations in the PostgreSQL 18 database; …`.
- If only the confirm run failed (the restore went through), run just its command again:
  `c run --rm --no-deps -e WHISKERS_CONFIRM_RESTORE=1 db-upgrade-guard`.

Until the confirm run succeeds, the guard keeps refusing. A failed or skipped restore therefore never ends in Whiskers
starting on an empty database.

### 3. Start everything

```sh
c up -d
```

The guard finds the confirmed restore and lets the start through. Whiskers starts on the restored database and
applies any newer migrations as usual.

- **Check:** sign in, look at the audit log and a few pages.
- **Optional, the server version:**
  ```sh
  c exec db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "select version()"'
  ```

## Going back to 17

```sh
c stop
c17 up -d
```

- Whiskers runs on the 17 data as of the dump. Anything written on 18 after the upgrade is in the 18 volume only.
- PostgreSQL 17 runs read-write again, so the 17 data changes. From then on the guard refuses to start 18 on the older
  copy:
  ```
  whiskers: the PostgreSQL 17 data changed after the upgrade (a rollback?). Whiskers will not start on the older 18 copy; to upgrade again remove the 18 volume and follow docs/operations/postgres-18-upgrade.md.
  ```
- **To upgrade again later,** remove the 18 volume first, then follow the steps from step 1:
  ```sh
  c down
  docker volume rm <project>_whiskers-db18-data
  ```
  `c down` removes the containers (the guard's included, which would otherwise keep the volume "in use") and no
  volumes.

## Cleaning up

Once you are sure, remove the dump file and the 17 volume. The volume's full name carries your compose project as a
prefix; `docker volume ls` shows it. The guard's exited container still references the volume, so remove it first:

```sh
rm whiskers-pg17.sql
c rm -f db-upgrade-guard
docker volume rm <project>_whiskers-db-data
```

The overlay still declares `whiskers-db-data`, so the next `up` creates it again, empty. An empty 17 volume is
harmless: the guard lets the start through.

**Careful with `down -v`:** `c down -v` removes every volume the files declare, now including the 17 volume you keep
for the way back, besides the 18 data itself.

## Other ways

- **Whiskers' self-backup** (Settings → Backup, `docs/operations/backup-and-recovery.md`) is a good extra copy
  before you start; the image's client tools are PostgreSQL 18 and back up a 17 server. Restoring a backup taken on 17
  into 18 is not part of the tested path. The dump above is that path, and it does not need Whiskers running.
- **`pg_upgrade`** needs the binaries of both versions in one container. It is not covered here. The new mount point
  at `/var/lib/postgresql` is what lets a later major upgrade use `pg_upgrade --link` inside one volume.
