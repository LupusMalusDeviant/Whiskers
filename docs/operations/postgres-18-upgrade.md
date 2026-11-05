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
| a 17 cluster | not restored by the guard | **refused** (a blind pull) |
| a 17 cluster | restored by the guard from a dump of exactly this 17 data | starts |
| a 17 cluster that changed after the restore | a restore of the older state | **refused** (after a rollback) |
| something else, or unreadable | anything | **refused** |

A volume that holds only `lost+found` (a volume on the root of its own file system) counts as "something else" and is
refused as unrecognised content. If you never ran PostgreSQL 17 on it, remove the directory:
`docker run --rm -v <project>_whiskers-db-data:/old postgres:18-alpine rmdir /old/lost+found`.

The restore is the guard's own job (step 2), and it only marks a restore it checked:

- the dump is complete;
- the dump carries the fingerprint of the current 17 cluster (`global/pg_control`), taken in step 1;
- the server is PostgreSQL 18, and it does not hold Whiskers' tables yet;
- the restore ran in one transaction;
- the restored database has Whiskers' migration history.

Only then does it write the fingerprint into the 18 volume. Starting PostgreSQL 17 again changes `pg_control`. After
that, the guard no longer accepts the older 18 copy, and a dump taken before that start is no longer accepted either.

**If you just pull the new overlay and run `up`, nothing breaks and nothing is lost.** The database and Whiskers stay
down, and `docker compose … logs db-upgrade-guard` shows:

```
whiskers: PostgreSQL 17 data found in the volume whiskers-db-data, and the PostgreSQL 18 volume whiskers-db18-data holds no restore of it.
whiskers: not starting an empty database. Follow docs/operations/postgres-18-upgrade.md (dump, restore, start).
whiskers: The volumes are whiskers-db-data (PostgreSQL 17) and whiskers-db18-data (PostgreSQL 18); docker volume ls shows them with your compose project as a prefix.
```

## The upgrade: dump on 17, restore on 18

The way is a logical dump and restore. It is version-independent, and the test suite runs it on every change
(`PostgresUpgradeTests`): Whiskers' schema and data on 17, then the dump, fingerprint and restore below. It was also
run end to end with Docker by hand:

- a blind pull refused;
- the upgrade;
- a rollback followed by a refused 18 start;
- a second upgrade;
- a stale dump refused;
- an 18 database Whiskers had initialised itself refused;
- a fresh install.

Plan a few minutes of downtime. Whiskers' database is small; the dump of a busy install is usually a few megabytes.

Run the commands from the directory with your `docker-compose.yml`, in `sh`, `bash` or `zsh`. Two shell functions stand
for the compose command with the new and with the previous overlay:

```sh
c()   { docker compose -f docker-compose.yml -f deploy/docker-compose.postgres.yml "$@"; }
c17() { docker compose -f docker-compose.yml -f deploy/docker-compose.postgres17.yml "$@"; }
```

If you run compose with `-p <name>` or set `COMPOSE_PROJECT_NAME`, add the same to both functions.

`c17` needs the previous overlay, the PostgreSQL 17 one, for the dump. `c` needs the new one, so update your files
before step 1.

- **If you have not updated the files yet:** copy the old overlay before you do:
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

### 1. Stop Whiskers, dump the 17 database and fingerprint it

```sh
c17 stop serverwatch
c17 up -d --no-deps --wait db
(umask 077; c17 exec -T db sh -c '
  n=$(psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "select count(*) from pg_stat_activity where datname = current_database() and pid <> pg_backend_pid() and backend_type = (select backend_type from pg_stat_activity where pid = pg_backend_pid())")
  if [ "$n" != 0 ]; then echo "whiskers: other clients are connected to the PostgreSQL 17 database ($n); stop Whiskers first: c17 stop serverwatch" >&2; exit 1; fi
  exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner --no-privileges' > whiskers-pg17.sql)
c17 stop db
c run --rm --no-deps -T -e WHISKERS_FINGERPRINT=1 db-upgrade-guard >> whiskers-pg17.sql
```

- `umask 077` creates the dump readable by you only. It holds your whole database, including password hashes and the
  encrypted vault entries; keep it as private as a backup.
- The dump command first checks that no other client is connected to the 17 database. If Whiskers (or anything else)
  is still connected, it refuses with `whiskers: other clients are connected …` and exits non-zero. The file it leaves
  behind is then empty, and so is any file a failed `pg_dump` cut short: neither has the dump's closing line, so the
  restore in step 2 refuses it. Stop the client and run step 1 again from the top.
- The last command appends one comment line, `-- whiskers-pg17-control: <fingerprint>`, taken from the stopped 17
  cluster. It refuses while PostgreSQL 17 still runs (`c17 stop db` first). With no client connected at the dump and
  17 stopped right after it, the fingerprint stands for the state the dump holds: if PostgreSQL 17 runs again later,
  the restore refuses this dump, and you take a new one.
- This is the last time anything writes to the 17 volume, unless you go back.

### 2. Start PostgreSQL 18 alone and restore

```sh
c up -d --no-deps --wait db
c run --rm --no-deps -T -e WHISKERS_RESTORE=1 db-upgrade-guard < whiskers-pg17.sql
```

- `--no-deps` starts the database without the guard's normal run, which still refuses at this point.
- The restore mode reads the dump, checks it and the server (see [the guard](#the-guard)), restores it in one
  transaction (`--single-transaction` with `ON_ERROR_STOP`), checks the migration history and only then writes the
  marker. It ends with `whiskers: restored: <n> migrations in the PostgreSQL 18 database; …`.
- **If it fails before or during the restore** (the messages end in "nothing restored"), nothing was written. Fix the
  cause and run the same command again. One exception: "the PostgreSQL 18 database already holds Whiskers' tables"
  means Whiskers started on the 18 database at some point. Remove the 18 volume as below and run step 2 again. If
  Whiskers really ran on 18 and you need what it wrote, dump that first, as in
  [going back](#going-back-to-17).
- **If it fails after the restore** (the marker could not be written, or no migration history), remove the 18 volume
  and run step 2 again:
  ```sh
  c down
  docker volume rm <project>_whiskers-db18-data
  ```
  "No migration history" means the dump is not of Whiskers' database. Check `WHISKERS_DB_NAME` (and
  `WHISKERS_DB_USER`) in your `.env`, and take a new dump from the right database (step 1) before step 2.

Until the restore mode succeeds, the guard keeps refusing. A failed or skipped restore therefore never ends in
Whiskers starting on an empty database, and neither does a database Whiskers initialised on its own.

### 3. Start everything

```sh
c up -d
```

The guard finds the restore it marked and lets the start through. Whiskers starts on the restored database and
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
- **To upgrade again later,** remove the 18 volume first, then follow the steps from step 1. The old
  `whiskers-pg17.sql` no longer fits: step 1 takes a new dump.
- **Removing the 18 volume deletes what was written on 18 before you went back.** If you need any of it, dump it first:
  ```sh
  c17 stop
  c up -d --no-deps --wait db
  (umask 077; c exec -T db sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner --no-privileges' > whiskers-pg18-period.sql)
  c stop db
  ```
  That file is for reference: nothing restores it for you. Then remove the volume:
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
