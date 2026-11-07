# Services/AutoPatch

Fleet-wide security auto-patching: every night, containers with **fixable** High/Critical vulnerabilities are
rolled onto the newest image **under the tag they already use** — never a new tag, never a new major version.
Off by default; the first enabled night is a dry run.

## Files

| File | Purpose |
|---|---|
| `AutoPatchPlanner.cs` | Pure selection: which containers are candidates (fixable findings ≥ `MinSeverity`, matched by server + name) and which are skipped with a reason (Whiskers' own connection, excluded, `whiskers.autopatch=false`, not compose-managed, stopped, digest-pinned, **built locally by compose** (image label `com.docker.compose.project` — not the RepoDigest, which local builds also get under the containerd store), **already on the newest image of its tag** per the update check, over the nightly limit). Stateless services before databases, known updates before unknown ones. |
| `AutoPatchOutcome.cs` | Reads the host script's verdict (`UPD-OK`, `UPD-SKIP-MAJOR`, `UPD-FAILED-ROLLBACK-…`) into an `AutoPatchResultKind`; says what counts as an attempt and what needs a person tonight. |
| `IAutoPatchRunner.cs` / `ScriptAutoPatchRunner.cs` | Patches one container: installs `img-update.sh` on the host when its hash differs, verifies the hash, runs it, returns the outcome and the log tail. R1-48: `PatchAsync` is marked `container.update`: it runs only inside the per-container decision of the night (or the preview). |
| `img-update.sh` | The host procedure (embedded resource, LF-normalised on load). Skips compose-built images (`UPD-SKIP-LOCAL-BUILD`). Drift guard via `compose --dry-run up` with the tag pointed at the running image (the config-hash label never matches `config --hash` for env_file services), backup tag, same-tag pull, major guard (OCI version label / `*_VERSION` env; unknown version on a floating tag = skip), verified dump for postgres/mysql/mariadb (last three kept in `/root/.img-backup`), `compose up --no-deps`, health check, rollback to the old image ID. |
| `IAutoPatchService.cs` / `AutoPatchService.cs` | The nightly loop (one cycle per day inside `RunAtHourUtc`, run through the Mutation Gateway as `autopatch.run` on the authority of the Admin who took AutoPatch over in Settings → Automation owners; **without an owner, or with an unreadable owner entry, the night is skipped** — recorded as the gateway's Denied `autopatch.run` event at read level (INV-45), PC-D6; when the owner loses their authority — before the cycle or half-way through it — AutoPatch is suspended in the owner store, an `automation_suspended` notification goes out and the cycle stops, R1-04), the report for the MCP tool, history rows in `UpdateHistory`, and an `auto_update_failed` notification on rollback / unverified dump / unknown result. Stops the night after a rollback that could not be verified. R1-39: the night (day, start, end) is kept through `IAutoPatchNightlyStore`, the day written before the cycle starts, so a restart in the window hour does not run it twice and a cycle that never ended shows as interrupted in `LastCycle`; once stopping began (`IShutdownDrain`) no further container is patched (`Stopped before <name>: Whiskers is shutting down.`). The start of the night must be saved before anything is patched — a failed save skips the night (`NightNotRecorded`), fail closed; a night that ended early saves its error with its end, so the status still says why after a restart — also a night that did not run (no owner, owner unreadable, refused, failed) and one cancelled while its last container ran (`NightCancelled`). |
| `AutoPatchNightlyStore.cs` | R1-39: `IAutoPatchNightlyStore` — the last nightly cycle (`Day`, `StartedAt`, `CompletedAt`, `DryRun`, `Error` — redacted with `SinkRedaction.Text` before it is written, INV-51) in `autopatch-nightly.json` (written atomically by `DurableFile`); not restored from a self-backup. `InMemoryAutoPatchNightlyStore` for tests. Not a `JsonFileStore`: the store keeps no previous generation (that would be the night before, making tonight due again); an unreadable file throws, and no night runs until it is fixed or removed (`NightStateUnreadable`, fail closed). A cycle cancelled before a container says so (`Stopped before …: the cycle was cancelled.`), so the night is not recorded as complete. |

## Why compose on the host and not `RecreateContainerAsync`

The Docker-API recreate copies the old container's `Env`/`Cmd` — including the values baked into the old image —
and drops what compose layered on top (healthcheck, user, stop signal). Compose recreates the service from the
file it was deployed from, and the config hash proves that file still matches what runs. Containers without
compose labels are therefore skipped, not patched another way.

## Settings

Section `AutoPatch` of `/app/data/app-settings.json` (applied live): `Enabled` (false), `DryRun` (true),
`RunAtHourUtc` (3), `MinSeverity` (High), `MaxPerCycle` (10), `ExcludeContainers` ([]). See
[`../../Configuration/AutoPatchSettings.cs`](../../Configuration/AutoPatchSettings.cs).

## MCP

- `get_auto_patch_status` (read): settings and the last cycle's report.
- `preview_auto_patch` (write): a dry run in the background — pulls, runs every guard, puts the old tag back,
  recreates nothing regardless of the settings. There is deliberately no tool that applies. The preview is owned by
  the service (`StartPreview`, R1-38): one cycle at a time, at most `PreviewDeadline` (2 h), cancelled and awaited
  when the host stops, and it does not run inside the calling tool's operation scope.

## Related

- Detecting updates (and why it now reads the running image, not the tag): [`../ImageUpdate/`](../ImageUpdate/)
- Per-container opt-in auto-update with C12 rollback snapshots: [`../AutoUpdate/`](../AutoUpdate/)
- Findings that drive candidate selection: [`../Cve/`](../Cve/)
