# Module: volumebackups

Docker-volume backup + restore: create tar backups of named volumes, list/restore/delete them, and (via the
Scheduler module) run them on a cron. The `/backups` page and `IVolumeBackupService`.

| | |
|---|---|
| **Id** | `volumebackups` |
| **Enabled by default** | yes |
| **Toggle** | `Features:volumebackups:Enabled` (env `Features__volumebackups__Enabled=false`) — restart required |
| **Depends on** | — (consumed by the Scheduler module via a Core no-op default) |
| **Nav** | `backups` — "Backups" (group *Infrastruktur*) |
| **MCP tools** | — |
| **Services** | `IVolumeBackupService` |

When **disabled**: the `backups` nav entry disappears and `/backups` shows a "module disabled" notice.

**Coupling:** the Scheduler module's task executor uses `IVolumeBackupService` for scheduled `VolumeBackup`
tasks. A `NoopVolumeBackupService` default keeps that graph resolvable when this module is off — but its
backup/restore operations **throw** instead of faking success, so a scheduled volume backup fails visibly
rather than silently doing nothing. Scheduled `DbBackup` tasks are unaffected (they use the database service +
host-file retention, not this module).

No settings section (backups are created/managed on the `/backups` page).

Code: [`src/Whiskers/Modules/VolumeBackups/`](../../src/Whiskers/Modules/VolumeBackups/) · service in
[`src/Whiskers/Services/Backup/`](../../src/Whiskers/Services/Backup/).
