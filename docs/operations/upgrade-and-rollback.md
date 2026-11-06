# Upgrade and rollback

How to move a Whiskers instance to a newer release and how to go back. This page is the procedure; the mechanics
behind it (what the pre-upgrade backup is, when it is taken, how the restore stages) are in
[backup-and-recovery.md](backup-and-recovery.md#upgrades). Whiskers 1.0 runs as one instance, so an upgrade has a
short downtime and the way back is a backup, not a second replica.

## Version policy

- **Pin a release.** Every install names one release (`X.Y.Z`, optionally with the verified digest). There is no
  `latest` and no pre-release channel: the installer refuses both (`deploy/README.md`, "Version").
- **Releases move forward only.** A newer release may migrate the database. Database migrations are not reversed; a
  downgrade is always "the older release on a backup taken before the upgrade" (below), never the older image on the
  migrated data. The installer refuses a downgrade.
- **Read the release notes** of every release between yours and the target, in order. They name breaking changes,
  required configuration and one-time steps (for example the PostgreSQL 17 → 18 move in
  [postgres-18-upgrade.md](postgres-18-upgrade.md)).

## Before the upgrade

1. **Readiness card green or understood.** *Settings → Production-ready?*: every line that is not OK is either
   fixed or a known decision. An upgrade is a bad moment to discover a wrong `VAULT_KEY`.
2. **A current self-backup off the host.** Whiskers takes its own pre-upgrade backup, but it lives on the same data
   volume. Take a self-backup under *Settings → Backup & Restore* and copy it off the host
   ([backup-and-recovery.md](backup-and-recovery.md#keep-a-copy-off-the-volume)).
3. **`VAULT_KEY` at hand.** Without it neither backup can be restored.
4. **No restore pending.** If `.restore-pending` exists in the data directory, a staged restore is waiting for the
   next start; let it finish (or give it up) first.
5. **PostgreSQL only:** the client tools in the image must be at least as new as the server (the readiness line
   "PostgreSQL-Client-Tools für das Self-Backup" says so), or the pre-upgrade backup fails and the start stops.
6. **Large database:** raise the start-up allowance before you start (Compose: the health check's `start_period`, 5 minutes in the image, see the comment in `docker-compose.yml`;
   Helm: `probes.startup.failureThreshold`). Otherwise the orchestrator restarts the container in the middle of the
   migration.

## Upgrade

**Installer (Docker Compose written by `install.sh`).** Run the `install.sh` of the target release, or
`bash install.sh --version X.Y.Z`. It changes only the `image:` line, keeps the previous compose file as
`docker-compose.yml.bak-<time>` (delete it after a successful upgrade: it can hold secrets), pulls and starts the new
image and waits for `/healthz`.

**Your own compose file.** Change the image to `ghcr.io/lupusmalusdeviant/whiskers:X.Y.Z` (better: with the digest
from the release), then `docker compose pull && docker compose up -d`.

**Helm.** `helm upgrade whiskers oci://ghcr.io/lupusmalusdeviant/charts/whiskers --version X.Y.Z -f your-values.yaml`.
The chart runs one replica with the `Recreate` strategy, so the old pod stops before the new one starts.

On its first start the new release:

1. checks the vault key;
2. takes a self-backup `pre-upgrade-<yyyyMMdd_HHmmss>` (if that fails, the start stops and nothing has changed). `WHISKERS_UPGRADE_SKIP_BACKUP=true` skips this backup — only with a recovery point of your own; the log warns at every such start;
3. migrates the database;
4. records the release as the one that last started.

## After the upgrade

- `/healthz` answers `Healthy` (the process is up) and `/readyz` answers `Healthy` (database, server configuration
  and audit trail ready; `Degraded` names a problem that keeps Whiskers serving — see
  [troubleshooting.md](troubleshooting.md#healthz-and-readyz)).
- Sign in, open *Settings → Production-ready?* and the *Whiskers itself* page (`/self-status`).
- The *Audit log* (Admin) shows new events after the start; a self-backup taken now succeeds.
- Remove `docker-compose.yml.bak-<time>` once you are satisfied.

## Rollback

Pick the case that matches.

| Situation | What to do |
|---|---|
| The start stopped during the upgrade (the log names a failed migration and the pre-upgrade backup) | Either fix the cause and start the **same** release again (it keeps the same pre-upgrade backup), or go back to the previous release: [When a migration fails](backup-and-recovery.md#when-a-migration-fails). |
| The new release runs, but you need the previous one | [Downgrade](backup-and-recovery.md#downgrade): stage the pre-upgrade backup with the **newer** image (`--restore-backup latest-pre-upgrade --for-previous-release`), then start the **previous** image. |
| Data is damaged, the release is fine | Restore a self-backup on the same release: [Restore on the same instance](backup-and-recovery.md#restore-on-the-same-instance). |
| The data volume is gone | [Restore after the volume is lost](backup-and-recovery.md#restore-after-the-volume-is-lost-off-volume-recovery). |

What a rollback loses: changes made after the backup you restore (metrics, audit events, settings). The pre-restore
safety backup that every restore takes first still holds them. Infrastructure changes Whiskers made on managed hosts
in the meantime are not undone by restoring Whiskers' own data.

Never start an older image on data a newer release has migrated: the installer refuses it, and by hand it can fail
in ways a backup has to repair.
