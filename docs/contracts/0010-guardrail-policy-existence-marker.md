# Contract 0010 — Guardrail policy existence marker (INV-25)

Status: **Accepted (2026-11-10)** · 10 November 2026 by the maintainer, with D1–D8 as written and the questions
Q1–Q4 decided as recommended (see "Decisions on the open questions").

## Purpose

INV-25 says a missing policy yields Deny. One gap is left: with neither `guardrails.json` nor `guardrails.json.bak`
present, the store creates the restrictive default (`GuardrailConfig.SafeDefault()`), so deleting both files on the host
looks exactly like a first start, and whatever the Admin had configured (kill switch, allowlist, protected resources,
exemption blocks) is silently replaced by a weaker default.

Maintainer decision, 8 November 2026: **"Marker in der DB"**. The database records that a guardrail policy has existed.
When both files are missing afterwards, the store fails closed (the same `GuardrailPolicy.FailClosed()` as for an
unreadable file, R1-02/R1-35/R1-11) until it is repaired on the host. The database change is additive only.

## Current state (verified at `79ed43b2`)

- `GuardrailStore.LoadLockedAsync` (`Services/Guardrails/GuardrailStore.cs`): `!_store.Exists()` (neither the file nor
  `.bak`, nor a directory in their place) → `SafeDefault()` is saved and enforced. A file that is unreadable or invalid,
  or only `.bak` present, → `FailClosed(reason)` (state with `LoadError`), file kept as `guardrails.json.unreadable-<ts>`.
- The store warms up as `IInitializable` Order 80 in `RunWhiskersStartupAsync` (`Startup/WhiskersPipelineExtensions.cs:644`),
  **before** the metrics and identity schemas are migrated (`:661–676`). A new table therefore does not exist yet when
  the store loads on the first start after an upgrade.
- `InspectAsync` (contract 0009 A1.2, recovery CLI) runs the same load read-only; a missing file is the SafeDefault in memory.
- `MetricsDbContext` (`Whiskers.Data`) has no generic key/value table. `PolicySnapshots` (R1-12) holds every policy
  version a gateway decision was made under; it is content-addressed and not suited as a marker, but any row in it proves
  that this instance ran with a guardrail store.
- Self-backup (F3, R1-32): the archive holds `metrics.db` (SQLite) or a `pg_dump` (PostgreSQL) and `guardrails.json`;
  the boot swap makes every target equal to the archive and removes a target the archive does not hold.
  `guardrails.json.bak` is not a restore target.

## Decisions (D1–D8, recommendation first)

- **D1 — Storage.** A new table `InstanceMarkers` (`Key` text primary key, `RecordedAt` UTC, `Detail` text nullable),
  added by one migration in each of `Whiskers.Migrations.Sqlite` and `Whiskers.Migrations.Postgres` (ADR-0004). Additive
  only: no existing table or column changes. The guardrail marker is the row `Key = "guardrail-policy-existed"`,
  `Detail` = the source (`created`, `loaded`, `backfill`, `snapshot-evidence`, `reset-cli`). Access goes through a new
  `IGuardrailPolicyMarker` (`Services/Guardrails/`): `Task<bool> ExistsAsync(ct)` (throws on any read error) and
  `Task EnsureAsync(string source, ct)` (insert if absent; a concurrent duplicate insert counts as success). See Q1.
- **D2 — Never cleared, never updated.** Whiskers never deletes or rewrites the row: no UI, no MCP tool, no capability,
  no retention job, no reset path. `EnsureAsync` only inserts. The `SqliteToPostgresMigrator` copies the table; a source
  without it counts as a known older release (added to its older-release table sets).
- **D3 — When the marker is written.** `EnsureAsync` runs (a) at every start in which `guardrails.json` or `.bak`
  exists (readable or not: either proves a policy was written), (b) right after the first-start SafeDefault was
  **saved** — the file first, then the marker, so a crash in between leaves a file that the next start backfills,
  never a marker without a reason — and (c) best effort after every successful save (`SaveConfigAsync`, `SaveAsync`,
  `DisableKillSwitchAsync`). A failed marker write while a file exists changes nothing about the enforced policy; it is
  logged as an error, shown on the readiness card and retried at the next save or start.
- **D4 — Two-phase start.** Phase 1 stays the Order-80 warm-up. When neither file exists it no longer saves anything: the
  store enters fail-closed with the reason "guardrail policy not decided yet". Phase 2 is a new step in
  `RunWhiskersStartupAsync` right after `upgradeGuard.CompleteAsync()` (both schemas current) and before the app serves
  requests: `GuardrailStore.ReconcileMarkerAsync` decides the case per the table below, then raises `Changed`. A start that
  fails before phase 2 never leaves the SafeDefault behind.
- **D5 — The marker is consulted only when neither file exists.** A present file (readable or not) decides as today; the
  marker cannot relax and cannot tighten a present file. An unreadable database therefore only matters in the one case
  where the marker decides, and there it fails closed.
- **D6 — Way back (no UI bypass, R1-11 stands).** Fail-closed for a missing policy ends only on the host:
  - put a valid `guardrails.json` into the data directory (from a self-backup, a copy, or a repaired
    `.unreadable-*` file) and restart; or
  - the new host CLI `whiskers --reset-guardrails` (class E3 of contract 0009: a shell in the container, root-equivalent):
    it refuses while `guardrails.json` or `.bak` exists (repair those instead), writes `GuardrailConfig.SafeDefault()`
    **with `ReadOnlyMode = true`** in the active preset (Q2), ensures the marker (`reset-cli`), records one audit event
    `guardrails.reset_on_host`, and tells the operator to restart and switch the kill switch off on the Guardrails page
    (E1, Admin, audited).
  - Deleting the marker row by hand is not a supported way back and is not documented as one.
- **D7 — Messages.** The fail-closed reason names the case: "guardrails.json and guardrails.json.bak are missing, but this
  instance had a guardrail policy (recorded <RecordedAt>). Restore guardrails.json on the host or run
  `--reset-guardrails`, then restart." and "… the record of an earlier guardrail policy could not be read (<error type>)".
  The Guardrails page (`GuardrailsView_FailClosed_Alert`) and the readiness card show the reason (EN and DE resx).
  Every text that today names "remove it together with `guardrails.json.bak`" as a reset (store log line, resx 4944,
  5991, 5994, `LocalAccountRecoveryCli` line 178, Help, Mutations README) is replaced by D6's two ways. This
  supersedes the "repair or remove `guardrails.json`" wording of contract 0009 A1.2 (the contract itself is not edited).
- **D8 — Upgrade backfill.** Existing installs get the marker at their first start on this release through D3 (a): their
  file exists. In addition, when neither file exists and the marker is absent but `PolicySnapshots` holds any row, the
  instance demonstrably ran with a policy: phase 2 writes the marker (`snapshot-evidence`) and fails closed instead of
  creating the SafeDefault (Q3).

## Start and load behaviour

"Ensure" = `EnsureAsync` per D3; its failure never changes the row's outcome. "FC" = `GuardrailPolicy.FailClosed()`.

| `guardrails.json` | `.bak` | Marker | Outcome |
|---|---|---|---|
| valid | any | any | loaded and enforced; ensure |
| unreadable / invalid / a directory | any | any | FC as today (R1-02), copy kept; ensure |
| missing | present | any | FC as today (R1-35); ensure |
| missing | missing | absent, no snapshot row | first start: SafeDefault saved, then ensure (`created`) |
| missing | missing | absent, snapshot rows exist | FC, ensure (`snapshot-evidence`) (D8) |
| missing | missing | present | FC, reason "policy existed" (D7) |
| missing | missing | read fails (DB unreachable, table missing, query error) | FC, reason "record could not be read"; nothing saved |

`InspectAsync` (recovery CLI) follows the same table read-only: it reads the marker but never writes it or the file; a
failed read is fail-closed (the CLI then asks for `--force`, A1.2).

## Backup and restore

- **Restore onto a running instance (F3):** the restored database carries the marker, the archive carries
  `guardrails.json` → normal load. An archive without `guardrails.json` whose database holds the marker → the swap removes
  the live file and the next start fails closed (row "missing / missing / present"). That is intended: restoring an
  instance that had a policy never yields a weaker one. The restore validator is not changed (Q4).
- **Archive from a release before 0010:** its database lacks the table; the start migrates it, the restored file
  exists, phase 2 backfills.
- **Fresh install restored from a backup:** the fresh first start writes SafeDefault and a marker into the fresh
  database; the restore replaces both with the archive's. Same as above.
- **The marker never travels without the database:** the PostgreSQL dump and the SQLite file both include the table.

## Tests (pinning)

New class `GuardrailPolicyMarkerTests` (in-process, SQLite; the store's phase 1 and phase 2 driven directly):

- one test per row of the table above, asserting the enforced policy, `LoadError`, whether `guardrails.json` was written
  and whether the marker exists afterwards (`Every_combination_of_file_backup_and_marker_decides_as_the_contract`, theory);
- `Deleting_both_files_after_a_first_start_fails_closed_on_the_next_start` (the INV-25 gap itself);
- `A_crash_between_file_and_marker_is_backfilled_not_fail_closed` (file written, marker write throws, next start);
- `A_marker_write_failure_keeps_a_valid_policy_enforced_and_is_reported`;
- `Saves_never_clear_the_marker` (save, kill switch off, failed save);
- `Phase_one_alone_never_saves_the_safe_default`;
- `Inspect_reads_the_marker_and_writes_nothing`;
- `Reset_cli_refuses_while_a_file_exists` and `Reset_cli_writes_the_safe_default_with_the_kill_switch_on_and_audits`;
- `Snapshot_rows_count_as_evidence_of_an_earlier_policy` (D8).

Existing suites extended: `MigrationModelDriftTests` (both providers), the PostgreSQL Docker suite (the table exists after
migration and the marker round-trips), `SqliteToPostgresMigrator` tests (table copied; a source without it is accepted
as an older release), a restore test (archive with marker but without `guardrails.json` → fail-closed after the swap),
and `UiMutationGatewayCoverageTests` (`EnsureAsync` listed as a record write, not a mutation — contract 0009 A1.1).

## Invariant and gate impact

- **INV-25 → ✅.** The "Open:" sentence is replaced by: "Contract 0010: once a policy has existed (marker in the
  database, written at creation, at every start with a policy file and after every save, never cleared), missing both
  files is fail-closed too, as is an unreadable marker; the way back is a restored file or the host CLI
  `--reset-guardrails`." The test column adds `GuardrailPolicyMarkerTests`.
- **RELEASE_GATE.md:** the INV-25 row leaves *Open invariants*; SECURITY_INVARIANTS §11 counts are updated.
- **Required tests:** as a ✅ invariant, every test named in the row (`GuardrailEngineTests.Invalid_policy_regex_does_not_throw`,
  every method of `PolicyFailClosedTests` and `GuardrailPolicyMarkerTests`, with xUnit's case counts) enters the generated
  block of `.github/required-tests/in-process.txt`.

## Documentation

`Services/Guardrails/README.md` (store row, marker, two-phase start, way back), `Whiskers.Data/README.md` and both
migration READMEs (new table), `Services/Mutations/README.md`, `Services/Setup/README.md` (readiness line), Help
(EN/DE), `docs/v1/SECURITY_INVARIANTS.md` (INV-25 row), `docs/v1/RELEASE_GATE.md`. Operator guide: the D6 recovery steps.

## Plan triage

- [Claude-prüfbar] table, migrations, marker service, two-phase start, CLI, texts, tests, docs, gate.
- [braucht Nutzer/Hardware] upgrade of the live instance: check after the first start that the marker row exists
  (readiness card) before anything else is changed.

## Decisions on the open questions (maintainer, 10 November 2026)

All four questions were decided as recommended.

- **Q1 — Table shape: the generic `InstanceMarkers` key table** (reusable for later "has existed" facts, e.g. other
  stores). Not chosen: a dedicated single-row `GuardrailPolicyMarker` table.
- **Q2 — What `--reset-guardrails` writes: SafeDefault with the kill switch on**, so a person switches it off
  deliberately in the UI (audited). Not chosen: the plain SafeDefault, as on a first start.
- **Q3 — Snapshot evidence (D8): yes**, `PolicySnapshots` rows count as proof of an earlier policy. It only changes the
  outcome where both files were already gone before the upgrade. Not chosen: marker only.
- **Q4 — Restore validator: unchanged**; the start fails closed and says why. Not chosen: a validator that refuses a
  SQLite archive whose database holds the marker but which holds no `guardrails.json`.

## Not in scope

- Deleting the whole database together with both files (or the data volume): that needs host write access to the data
  directory, which is root-equivalent and outside the product contract (PRODUCT_CONTRACT §5.2); such an instance is a
  new instance and starts the setup wizard.
- Using `guardrails.json.bak` to relax a fail-closed policy (unchanged: never).
- Any UI or MCP path out of fail-closed (unchanged since R1-11).
