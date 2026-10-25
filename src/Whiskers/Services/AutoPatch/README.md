# Services/AutoPatch

Fleet-wide security auto-patching: every night, containers with **fixable** High/Critical vulnerabilities are
rolled onto the newest image **under the tag they already use** — never a new tag, never a new major version.
Off by default; the first enabled night is a dry run.

## Files

| File | Purpose |
|---|---|
| `AutoPatchPlanner.cs` | Pure selection: which containers are candidates (fixable findings ≥ `MinSeverity`, matched by server + name) and which are skipped with a reason (Whiskers' own connection, excluded, `whiskers.autopatch=false`, not compose-managed, stopped, digest-pinned, over the nightly limit). Stateless services before databases. |
| `AutoPatchOutcome.cs` | Reads the host script's verdict (`UPD-OK`, `UPD-SKIP-MAJOR`, `UPD-FAILED-ROLLBACK-…`) into an `AutoPatchResultKind`; says what counts as an attempt and what needs a person tonight. |
| `IAutoPatchRunner.cs` / `ScriptAutoPatchRunner.cs` | Patches one container: installs `img-update.sh` on the host when its hash differs, verifies the hash, runs it, returns the outcome and the log tail. |
| `img-update.sh` | The host procedure (embedded resource, LF-normalised on load). Drift guard via compose's config hash, backup tag, same-tag pull, major guard (OCI version label / `*_VERSION` env; unknown version on a floating tag = skip), verified dump for postgres/mysql/mariadb (last three kept in `/root/.img-backup`), `compose up --no-deps`, health check, rollback to the old image ID. |
| `IAutoPatchService.cs` / `AutoPatchService.cs` | The nightly loop (one cycle per day inside `RunAtHourUtc`), the report for the MCP tool, history rows in `UpdateHistory`, and an `auto_update_failed` notification on rollback / unverified dump / unknown result. Stops the night after a rollback that could not be verified. |

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
  recreates nothing regardless of the settings. There is deliberately no tool that applies.

## Related

- Detecting updates (and why it now reads the running image, not the tag): [`../ImageUpdate/`](../ImageUpdate/)
- Per-container opt-in auto-update with C12 rollback snapshots: [`../AutoUpdate/`](../AutoUpdate/)
- Findings that drive candidate selection: [`../Cve/`](../Cve/)
