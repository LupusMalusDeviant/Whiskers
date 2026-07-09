# Services/Backup

Docker **volume backups**: create and manage backups of container volumes. Backups are written to `/app/data/backups` on the target host as `tar.gz` archives; metadata rows live in the SQLite DB.

**Restore safety (2026-07):** `RestoreVolumeAsync` no longer wipes the target volume before it has verified the restore can succeed. The sequence is now: (1) verify the archive exists and is an intact gzip tar (`tar tzf` in a read-only alpine container) and abort untouched if not; (2) take an automatic pre-restore safety backup of the current volume so the wipe is reversible; (3) clear the volume (including dotfiles, via `find /data -mindepth 1 -delete`) and extract. A missing/corrupt archive can no longer destroy the live volume.

## Files

| File | Purpose |
|---|---|
| `IVolumeBackupService.cs` / `VolumeBackupService.cs` | Creates, lists and restores Docker volume backups; restore validates the archive and takes a safety backup before wiping. |
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
