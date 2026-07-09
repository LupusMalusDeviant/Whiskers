# Modules/VolumeBackups

Docker-volume backup + restore — the `/backups` page and the `IVolumeBackupService`. The fifth feature
extracted from `AllInOnePseudoModule` (RoadToSAP Phase 1).

- `VolumeBackupsModule.cs` — `Id = "volumebackups"`, enabled by default. `ConfigureServices` (moved
  **verbatim** from `Program.cs`) registers `IVolumeBackupService` → `VolumeBackupService`. Nav: the `backups`
  entry ("Backups", group *Infrastruktur*). No MCP tools.

**Toggle:** `Features:volumebackups:Enabled` (`Features__volumebackups__Enabled=false`), restart-only. When
off, `/backups` shows a "module disabled" notice (via `ModuleGuard`).

**Cross-module coupling + the data-safe no-op.** The Scheduler module's `TaskExecutor` injects
`IVolumeBackupService` (for `VolumeBackup` tasks and their retention). Because `TaskExecutor` is a **singleton**,
`ValidateOnBuild` would fail to construct it if VolumeBackups were off and the service unregistered. So Core
registers a [`NoopVolumeBackupService`](../../Services/Backup/NoopVolumeBackupService.cs) default before the
module loop; the real service wins by last-registration when enabled. The no-op's reads return empty, but
`BackupVolumeAsync`/`RestoreVolumeAsync` **throw** rather than fake success — a scheduled `VolumeBackup` task
with the module off then records a *visible failure* (caught by `TaskExecutor`) instead of silently backing up
nothing. `DbBackup` tasks are unaffected (they use Core's database service + host-file retention, not this
service).

**DI-safe page guard.** [`VolumeBackups.razor`](../../Components/Pages/VolumeBackups.razor) is a thin route
wrapper — `<ModuleGuard ModuleId="volumebackups"><VolumeBackupsView/></ModuleGuard>`. The interactive logic
that injects `IVolumeBackupService` lives in the child
[`VolumeBackupsView.razor`](../../Components/Pages/VolumeBackupsView.razor). Service code stays in
[`../../Services/Backup/`](../../Services/Backup/).

See [RoadToSAP](../../../../docs/roadmap/RoadToSAP.md) and
[`docs/modules/volumebackups.md`](../../../../docs/modules/volumebackups.md).
