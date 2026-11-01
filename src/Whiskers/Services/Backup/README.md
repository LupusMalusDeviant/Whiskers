# Services/Backup

Docker **volume backups**: create and manage backups of container volumes. Backups are written to `/app/data/backups` on the target host as `tar.gz` archives; metadata rows live in the SQLite DB.

**Restore safety (2026-07):** `RestoreVolumeAsync` no longer wipes the target volume before it has verified the restore can succeed. The sequence is now: (1) verify the archive exists and is an intact gzip tar (`tar tzf` in a read-only alpine container) and abort untouched if not; (2) take an automatic pre-restore safety backup of the current volume so the wipe is reversible; (3) clear the volume (including dotfiles, via `find /data -mindepth 1 -delete`) and extract. A missing/corrupt archive can no longer destroy the live volume.

## Files

| File | Purpose |
|---|---|
| `IVolumeBackupService.cs` / `VolumeBackupService.cs` | Creates, lists and restores Docker volume backups; restore validates the archive and takes a safety backup before wiping. |
| `IBackupService.cs` / `BackupService.cs` | Whiskers' own self-backup and restore of the data directory (F3). R1-23: the audit checkpoint file (`audit-checkpoint.json`) is staged *before* the database copy, so a restored database always reaches the restored checkpoint; the live checkpoint file and the restore note are not archived. R1-28: the archive key comes from `VAULT_KEY_FILE` or `VAULT_KEY` (`SecretSource`); while the vault rejects that key (`KeyState.Wrong`) no self-backup is created, so no archive is sealed with a mistyped key. |
| `RestoreBootHandler.cs` / `RestoreMarker.cs` | The deferred restore applied at the next start, before the database opens. R1-23: an archive with a database but no checkpoint file (from before the chain) moves the live checkpoint file aside, so the restored rows start a new chain; the restore leaves a note (actor, archive, time) that the start writes as a chained `selfbackup.restored` event. R1-32: writes only the targets of `SelfBackupLayout` and makes each equal to the archive (absent ones removed; the database and the VPN state `tailscale/`/`netbird/` only replaced, the audit outbox merged, the checkpoint kept aside), never touches `backups/`, then restricts `keys/`, `ssh-keys/`, `mtls/` and `vault.json` to the owner. Every step is idempotent, so a swap interrupted half-way is completed at the next start. |
| `SelfBackupLayout.cs` | R1-32: the one list of what a self-backup holds and a restore may write — the known stores and directories, what is local to the install (setup token, legacy key files, restore state, backups) and what is forbidden in an archive (backups, restore state, `-wal`/`-shm`/`-journal`, `.tmp`, `vault.key`). Packing and restoring both use it; a test fails when a new `DataPathOptions` path is not placed in it. |
| `TarHeaderScreen.cs` | R1-32: reads the tar structure as it passes to `TarReader` and refuses a header before the reader acts on it — entry types other than file and directory, data on a directory, and PAX records or GNU long names above 1 MiB (the reader allocates their declared size before reading). Follows PAX `size` records to stay aligned. |
| `StagedRestoreValidator.cs` | R1-32: checks the extracted restore before the commit marker — SQLite `integrity_check`, the migration history equal to the manifest, no tables the start would create again (a history-less database only when the start baselines it: `DatabaseInitializer.SentinelTable`), every JSON store a service reads at start deserializes as its type (the two configuration layers hold an object, the rest parses), every data-protection key file is XML. |
| `BackupArchiver.cs` / `BackupArchiveCipher.cs` / `SelfBackupManifest.cs` / `SelfBackupInfo.cs` | The self-backup archive (tar.gz), its VAULT_KEY-derived encryption and its manifest. R1-32: inspect and extract refuse forbidden, duplicate (case-insensitive), file/directory-conflicting and non-canonical (`a/../b`, `a//b`) entries and archives above `BackupExtractionLimits` (100k entries, 32 GiB, 256 MiB per JSON store, 1 MiB for the manifest, 1 MiB of headers per entry, 1 MiB per data-protection key file, names at most 4096 characters and 32 levels deep and 64 Mi characters together, the decompressed stream bounded too; backslashes in names refused; only `keys/*.xml` of the key ring), skip entries that are not restore targets and create every file owner-only. With `VAULT_KEY` set `BackupService` refuses an unencrypted archive and publishes an encrypted one by rename once it is complete; the start's migration is first run on a copy of the staged database and every mapped table and column read on it (a history behind its tables or a missing table would stop every start; the probe logs nothing of its own); the safety copy is taken only after the extracted archive passed the checks and the data volume has room for it; the sidecar is written before the archive is renamed into place, so no archive exists unlisted. |
| `NoopVolumeBackupService.cs` | Core default `IVolumeBackupService` for when the **VolumeBackups module** is off. Keeps the Scheduler task executor's singleton graph resolvable; reads return empty but backup/restore **throw** (never fake a success) so a scheduled volume backup fails visibly. Real service wins by last-registration when the module is on (RoadToSAP Phase 1). |

## Wiring

This is the opt-in **VolumeBackups module** ([`../../Modules/VolumeBackups/`](../../Modules/VolumeBackups/),
toggle `Features:volumebackups:Enabled`): its `ConfigureServices` registers `IVolumeBackupService` and owns the
`backups` nav entry. The Scheduler module's `TaskExecutor` consumes this service, so Core keeps the
`NoopVolumeBackupService` default above for when the module is off.

## Related

- UI: [`../../Components/Pages/VolumeBackups.razor`](../../Components/Pages/VolumeBackups.razor) (thin
  `ModuleGuard` wrapper) → [`../../Components/Pages/VolumeBackupsView.razor`](../../Components/Pages/VolumeBackupsView.razor)
- Container/volume operations: [`../Docker/`](../Docker/)
