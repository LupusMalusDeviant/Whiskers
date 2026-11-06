# Contract 0008 — Mechanical release gate (R1-51)

Status: **Proposed** · 6 November 2026, revised after review round 1 (M1–M5, MINOR 1–3). Waits for the maintainer's
decisions D1–D14 at the end. Nothing of the gate is implemented before this contract is accepted; the documentation
part of R1-51 (operator guides, accepted limits, corrections) does not depend on it and is done on the same branch.

## Purpose

`SECURITY_INVARIANTS.md` states the release rule: *v1.0 ships only when every invariant is ✅ and its named test
exists and passes; a deliberate exception needs a recorded decision and a matching limitation in the product
documentation.* Today a person checks that by reading. R1-51 makes it mechanical:

1. **`ReleaseGateTests`** fails when an invariant is not ✅, a named test is missing or does not run and pass, a
   mandatory suite is not wired to block the release, or an exception is not documented and accepted (WORK_PACKAGES
   R1-51, criterion 1).
2. **A generated capability/path inventory** is checked against the code and against the path matrix of
   `PRODUCT_CONTRACT.md` §7, so the matrix cannot drift silently (criterion 2).
3. **`docs/v1/RELEASE_GATE.md`** is the one page a maintainer reads before tagging: what the gate checks, the open
   invariants, the accepted exceptions, the allowed platform skips and the mandatory suites — the tables in it are
   read by the gate.

The gate proves that the **records** are consistent and complete and that the named tests exist, run and pass. It
does not prove that an invariant holds; that is what the named tests are for (R1-51, out of scope: "treating
documentation as proof"). Every rule **fails closed**: input the gate cannot read, resolve or classify is a finding,
never a pass.

## Current state (verified at `e98446a2`, plus the R1-51 documentation commits)

- **Invariant table.** `docs/v1/SECURITY_INVARIANTS.md`, 61 rows `| INV-nn | statement | status | evidence | test | WP |`
  in sections 1–10; §11 (`:137`) repeats the counts by hand. Today: 50 ✅, 10 ⚠️ (INV-01, 13, 17, 19, 21, 25, 45, 52,
  56, 57), 1 ❌ (INV-18), 0 ❓. The release rule (`:14–18`) already promises a mechanical check by `RELEASE_GATE.md`
  and fixes the format: ID in the first column, the status symbol at the start of the third.
  - **One status cell carries a qualifier:** INV-58 (`:133`) reads `✅ per host after migration`. Hosts still on the
    shared mTLS client credential are not covered by it, so this is a partial ✅ in a cell that starts with ✅.
- **Test column.** The legend (`:23–24`) names an existing test as `` `Class.Method` `` and a planned one as
  `` `Class` *(planned, WPx)* ``; the bare `` `Class` `` form is not in the legend but is used in most rows (the R1-51
  documentation commit aligns the legend). Prose stands beside the names (INV-17: "R1-47: the HTTP layer …").
  - 163 backticked `…Tests` names. All of them resolve to a class (and, where given, a method) in
    `src/Whiskers.Tests` — checked by text search for this draft, not by reflection.
  - Two *(planned …)* entries:
    - INV-18: "the in-handler checks of these actions *(planned, WP10)*" — it names **no** class at all.
    - INV-21: "`KillSwitchTests` *(planned, WP6, wire test)*". A class `KillSwitchTests` exists
      (`src/Whiskers.Tests/KillSwitchTests.cs:20–25`), but it covers something else: unattended runs that must not
      auto-confirm (R1-01) and key revocation (INV-08). None of its tests touches the read-only kill switch. The entry
      therefore stands for a kill-switch wire test that is **still owed**; only its name collides with an existing
      class.
- **Runtime skips.** `Whiskers.Tests.csproj:20` references `Xunit.SkippableFact` 1.5.85; 15 test files call
  `Skip.If` / `Skip.IfNot` at run time. Classes named by ✅ rows that contain runtime skips: `HostShellDeadlineTests`
  (3 calls), `PerHostCredentialShellTests` (1), `PerHostCredentialTests` (2 — for example
  `On_windows_a_key_readable_by_everyone_is_exposed`, `:946`, which skips on every non-Windows run),
  `SecretEnvironmentTests` (1), `VpnSecretHandlingTests` (11). Every CI test job runs on Linux (`ci.yml:33`, `:118`,
  the `image-runtime` matrix on x64 and arm64 Linux runners), so a Windows-only test never runs unskipped in CI.
  `check-test-results.sh:103–105` counts a skipped result as "did not pass".
- **Exceptions.** There is no exception register. `PRODUCT_CONTRACT.md` §6 (`:112`) holds PC-D1…PC-D8; its header
  records them as accepted by the maintainer on 2026-10-26, but the table has no per-row status, date or decider.
  §5 lists the stated limits. Designed exemptions from the gateway (S-09/S-10/S-13/S-14, the CVE scanner caches) are
  pinned to their callers by `MutationGatewayCoverageTests` and are not invariant exceptions.
- **Path matrix.** `PRODUCT_CONTRACT.md` §7 has 105 rows today (M 47, A 4, S 15, T 3, U 36); §7.6 (`:320`) kept the
  WP11a total of 90 until R1-51 added the current counts. Only M rows name a key that the code also knows (the MCP tool
  name). U, S, T and A rows name handlers or paths in prose; no row names a capability id. §8 (`:354–360`) promises
  "§7 will be generated from the capability table and checked in CI" — not done.
- **Capability table.** `CapabilityCatalog` (`src/Whiskers/Services/Mutations/CapabilityCatalog.cs`): 110 capabilities,
  47 of them with an MCP tool. `VerificationContracts` gives each its verification kind, `MutationCompositions` the
  allowed nesting. `MutationGatewayCoverageTests.Inventory` (`src/Whiskers.Tests/MutationGatewayCoverageTests.cs:69`)
  lists `(surface, capability, boundary)` for the guarded writes; `Every_capability_has_an_execution_boundary_the_guard_checks`
  and `The_inventory_names_real_boundaries_for_every_surface` keep it honest against the code, but not against §7.
- **Generated-document precedent.** `McpToolCatalogSnapshotTests` renders `docs/mcp-tool-catalog.md` from the code,
  fails on any difference and writes `<file>.actual` beside it for review — it never repairs the file itself.
- **Required tests in CI (R1-47).** `.github/required-tests/in-process.txt` and `docker.txt` name tests that must be in
  the TRX file and pass; `check-test-results.sh --require` enforces them (`ci.yml:80`, `:91`), and
  `RequiredTestsManifestTests` keeps the manifests in step with the test assembly (`Resolve`, `IsDocker`). The
  in-process run (`--filter "Category!=RequiresDocker"`) fails on any failed test and on fewer passed tests than its
  floor; the Docker run (`Category=RequiresDocker`) also on a skip. The `image-runtime` job (`ci.yml:183–221`,
  `Category=ImageRuntime`) writes **no** TRX file and checks no manifest.
- **Release wiring.** `release.yml` runs on a pushed `v*` tag or on `workflow_dispatch` with a tag input (`:20–26`).
  `prepare` refuses anything that is not exactly `vX.Y.Z` (`:78`) and refuses a tag whose version differs from
  `<Version>` in `src/Whiskers/Whiskers.csproj`, the chart and the installer (`:100–107`); today `<Version>` is
  `0.15.0` (`Whiskers.csproj:8`). `test` calls `ci.yml` on the tag commit (`:141–147`); `publish-image` needs
  `prepare, test, build, scan, artifact-test` (`:332`). `security.yml` (dependency vulnerabilities, secret scan,
  static analysis, CodeQL) is **not** part of the release run. `ReleaseWorkflowTests` pins properties of the workflow
  files with negative tests that mutate the real file text. There is no 1.0 tag yet.
- **Test framework.** xUnit 2.9.3 (`Whiskers.Tests.csproj:18`) with `Xunit.SkippableFact` for runtime skips (above).

## Boundaries (fixed)

- The gate is **test code**. It ships nothing in the product image and adds no runtime dependency.
- It reads Markdown tables, the test assembly (reflection), the capability catalog, `Whiskers.csproj` and the workflow
  files. It never changes a document; a generated file that differs is written beside it as `<file>.actual`, as for
  the MCP catalog.
- It changes no invariant status, no row and no test. Fixing a finding is a human change in the same PR.
- Formats stay as they are, with three precise additions made in the implementation PR: the status cell holds one
  bare symbol (R3), planned entries name the owed test (R6), and §6 gets a release-exception table (R11).
- No protected area is touched (auth middleware, OAuth whitelist, mTLS/WireGuard, socket-proxy verbs).

## Mode and version (D2)

- **The version is `<Version>` in `src/Whiskers/Whiskers.csproj`.** `release.yml` already proves that a release tag
  equals it (`:100–107`), on a pushed tag and on `workflow_dispatch` alike, so the gate needs no tag or workflow
  input to know which version a run would release.
- **Release mode applies in every run whose `<Version>` is 1.0.0 or later** (semantic-version comparison of the three
  numbers; `prepare` admits no pre-release suffix). That includes the pull request that bumps the version to 1.0.0,
  every later push to `main` and every later release run, whatever the tag or how the workflow was started. Below
  1.0.0 the gate runs in development mode.
- **`WHISKERS_RELEASE_GATE` can only switch release mode on**, for a dry run before the version bump. Its only
  accepted values are unset, empty, or `release` (exact, lower case); any other value is a finding. It can never turn
  release mode off.
- **A version the gate cannot resolve fails**: no `Whiskers.csproj`, no `<Version>`, more than one, or a value that is
  not `X.Y.Z`.

## Documents

### `docs/v1/RELEASE_GATE.md` (new)

Human text plus four tables the gate reads. Each table starts after a fixed heading; rows are `| a | b | … |`, the
first column a key. A heading that is missing or a row that does not have the table's column count is a finding.

```markdown
## Open invariants
| Invariant | Status | Package | Why it is open |
| INV-18 | ❌ | WP10 | in-handler checks of approval, notification, scan and job actions |
…

## Accepted exceptions
| Invariant | Status shipped | Decision | Limitation | Review by |
| INV-xx | ⚠️ | RG-1 | docs/operations/production-checklist.md#accepted-limits-10 | 1.1.0 |

## Platform skips
| Test | Skips when | Runs unskipped in | Why |
| Whiskers.Tests.PerHostCredentialTests.On_windows_a_key_readable_by_everyone_is_exposed | not Windows | — (no Windows CI job) | Windows ACL check |

## Mandatory suites
| Workflow | Job | Step (optional) | Proves |
| ci.yml | build-test | Test (in-process) | the whole in-process suite, floor and required tests |
…
```

### `PRODUCT_CONTRACT.md` §6.1 "Release exceptions" (new table, D5)

`| ID | Invariant | Decision | Status | Date | Decided by |` with IDs `RG-1`, `RG-2`, … *Status* is `Proposed` or
`Accepted`; *Date* is the ISO date of acceptance; *Decided by* names the maintainer who accepted it.

### `docs/v1/CAPABILITY_INVENTORY.md` (new, generated)

One row per capability, sorted by id: `id`, level, target kind, risk, MCP tool (or —), verification kind
(read back / `NotVerifiable` + reason), surfaces from `MutationGatewayCoverageTests.Inventory`, allowed compositions,
and the §7 rows that name it (from D6). A header says "generated — do not edit", as in `docs/mcp-tool-catalog.md`.

### `docs/v1/PATH_CAPABILITIES.md` (new, hand-maintained — only with D6 option b)

`| Row | Capabilities |`, one line per §7 row that is not an M row: `U-22 | checks.pause, checks.resume`. A row that
maps to no capability says why in the second column (`— reads only`, `— designed exemption (R1-48)`).

## Types and where they live

All in `src/Whiskers.Tests/ReleaseGate/` (D1), namespace `Whiskers.Tests.ReleaseGate`:

```csharp
public enum InvariantStatus { Holds, Partial, Violated, Unverified }          // ✅ ⚠️ ❌ ❓

// A planned entry may name no class yet (INV-18 today): then Class is null and Raw holds the text.
public sealed record TestReference(string? Class, string? Method, bool Planned, string Raw);

public sealed record InvariantRow(string Id, InvariantStatus Status, string StatusCell,
                                  IReadOnlyList<TestReference> Tests, string Package, int Line);

public sealed record OpenInvariant(string Id, InvariantStatus Status, string Package, string Reason);
public sealed record AcceptedException(string Id, InvariantStatus Shipped, string DecisionId,
                                       string LimitationLink, string ReviewBy);
public sealed record ReleaseExceptionDecision(string Id, string Invariant, string Status, DateOnly? Date, string DecidedBy);
public sealed record PlatformSkip(string Test, string SkipsWhen, string? RunsUnskippedIn, string Why);
public sealed record MandatorySuite(string Workflow, string Job, string? Step, string Proves);

public sealed record PathRow(string Id, string Class /* M A S T U */, string? McpTool,
                             IReadOnlyList<string> Capabilities, int Line);

public sealed record GateFinding(string Rule, string Subject, string Message);   // Rule = R1…R16 below

public enum GateMode { Development, Release }
public sealed record GateVersion(int Major, int Minor, int Patch);

public static class MarkdownTables                 // one row reader for all documents; fails on a malformed row
{
    public static IReadOnlyList<(int Line, string[] Cells)> After(string markdown, string heading);
}

public static class InvariantTableParser  { public static IReadOnlyList<InvariantRow> Parse(string markdown); }
public static class ReleaseGateDocument   { public static ReleaseGateTables Parse(string markdown); }
public static class PathMatrixParser      { public static IReadOnlyList<PathRow> Parse(string contract, string? pathCapabilities); }
public static class TestCatalog           // reflection over the test assembly, shared with RequiredTestsManifestTests
{
    public static IReadOnlyList<(Type Type, MethodInfo Method)> Resolve(string className, string? method);
    public static int CaseCount(MethodInfo method);         // 1 for a Fact; the data rows of a Theory, MemberData evaluated
    public static bool IsStaticallySkipped(MethodInfo method);
    public static TestRun RunOf(Type type, MethodInfo method);   // InProcess | Docker | ImageRuntime | None
}
public static class CapabilityInventoryRenderer { public static string Render(ICapabilityCatalog catalog,
                                                  IReadOnlyList<PathRow> rows); }

public static class ReleaseGate
{
    public static GateVersion ReadVersion(string csproj);                       // throws on anything but one X.Y.Z
    public static GateMode Mode(GateVersion version, string? environmentValue); // ≥ 1.0.0 → Release; env only turns it on
    public static IReadOnlyList<GateFinding> Evaluate(GateInputs inputs, GateMode mode, GateVersion version); // pure
}
```

`GateInputs` holds the parsed documents, the test catalog, the capability catalog and the workflow files' text, so
every rule can be tested on mutated copies of the real files without touching disk.

## What `ReleaseGateTests` checks

The test reads the real files, determines version and mode (above), runs `ReleaseGate.Evaluate` and fails with
**every** finding listed (rule, subject, file:line, what to do), not only the first.

### Invariant status

- **R1 — Release mode:** every invariant's status cell is exactly `✅`, or the invariant is ⚠️ and has an accepted
  exception (R11). A ❌ or ❓ invariant fails release mode whatever the documents say (D11).
- **R2 — Development mode (ratchet, D3):** every non-✅ invariant has a row in *Open invariants* with the same status
  and package; a row there for an invariant that is ✅ now fails as stale (the list may only shrink honestly).
- **R3 — Format, both modes:** every `INV-nn` appears once, IDs are unique and consecutive with no gaps except a
  listed retired ID, and §11's counts equal the table's. **The status cell is one bare symbol** — ✅, ⚠️, ❌ or ❓ and
  nothing else. A qualifier ("✅ per host after migration") fails: the qualification belongs in the evidence column,
  and a partial ✅ is ⚠️ (with an exception if it is to ship). Today INV-58 fails this rule (D12).

### Named tests that must exist

- **R4 — Every backticked identifier in the test column resolves.** In the test column, backticks are reserved for test
  references: `` `Class` `` (the class must hold at least one test) or `` `Class.Method` `` (every case of that method).
  Each must resolve by reflection (`TestCatalog.Resolve`), on every row whatever its status, unless the entry is
  planned (R6). Anything else in backticks — a typo such as `PrincipalPropagationTest`, a type that is not a test
  class, a file name — is a finding; prose outside backticks is ignored. The legend in `SECURITY_INVARIANTS.md` is
  aligned to exactly these forms.
- **R5:** a ✅ row has at least one resolvable, non-planned test; a ✅ row with any planned entry fails.
- **R6 — Planned entries name the owed test (D10).** A planned entry is `` `Class.Method` `` *(planned, …)* or
  `` `Class` `` *(planned, …)* for a class that does not exist yet. It fails when the method, or the class named
  without a method, already exists — the marker then no longer says which test is still owed. A planned entry
  without any backticked name (INV-18 today) is allowed only on a ❌ or ❓ row and is listed as a finding in release
  mode. For INV-21 the implementation PR replaces `` `KillSwitchTests` *(planned …)* `` with the name of the owed
  wire test (for example `` `ReadOnlyKillSwitchWireTests` *(planned, WP6)* ``), so the debt stays visible.
- **R7 — Skips (D13).** No named test is statically skipped (`Skip =`). Runtime skips are handled by R8: a named
  method that skips in the run that requires it fails there, because a skip is not a pass. The only way to keep a
  platform-dependent method named is a *Platform skips* row:
  - the row names the method (`Namespace.Class.Method`), the condition and the CI job in which it runs unskipped;
  - a method with *Runs unskipped in* = "—" (no CI job can run it, such as the Windows-only ACL test) is **left out**
    of the generated manifest entries and does **not** count as evidence: a ✅ row needs at least one other named
    test that is proven in CI (R5 counts only proven tests);
  - a *Platform skips* row for a method that no invariant names, or that does not exist, is stale and fails.
  Subtracting skipped results from the counts is not an option: it would let any `Skip.If(...)` drop a mandatory
  test silently.

### "Passes": the named tests in the CI manifests (D4)

- **R8 — One entry per method, exact minimum.** For every test named by a ✅ invariant, or by an accepted exception,
  the gate renders one manifest entry **per method**: a `Class` reference expands to every test method of the class,
  each with its own entry. The minimum of an entry is `TestCatalog.CaseCount(method)`: 1 for a `[Fact]`/
  `[SkippableFact]`, the number of data rows for a `[Theory]` (`InlineData` counted, `MemberData`/`ClassData`
  evaluated by reflection; a data source that cannot be evaluated is a finding). The entries live in a generated block
  between `# BEGIN release-gate (generated)` and `# END release-gate` in the manifest of the method's run; the gate
  renders the block and compares it like a snapshot (`.actual` beside it on a difference). CI's existing
  `check-test-results.sh --require` then proves from the TRX file that each case ran and passed — a skip fails there.
- **R9 — Every named test belongs to a checked run.** `TestCatalog.RunOf` classifies by category: Docker
  (`RequiresDocker`), image runtime (`ImageRuntime` — the in-process filter `Category!=RequiresDocker` selects these
  methods too, but there they skip without a built image, so only the `image-runtime` job proves them) or in-process
  (everything else). A named
  test in the image-runtime run needs that job to write a TRX file and check its own manifest (D14); until then it is
  a finding. A named test that no CI filter selects (`RunOf` = None) is a finding.

### Mandatory suites

- **R10:** each *Mandatory suites* row names a job (and step) that exists in that workflow, has no
  `continue-on-error`, and is required by the release: a `ci.yml` job is reached through `release.yml`'s `test` job
  (`uses: ./.github/workflows/ci.yml`) and is not skipped on `workflow_call`; a `release.yml` job is in the `needs`
  closure of `publish-image`; a `security.yml` job only with D8. The in-process, Docker (and, with D14, image-runtime)
  test steps keep their floor and `--require`, and the Docker step keeps `--no-skips` (already pinned by
  `ReleaseWorkflowTests`; the gate names them so removing either fails here too). "Unsuccessful" is decided by
  GitHub: a release cannot publish when a needed job failed. The gate's part is that the wiring cannot be removed
  without failing a test.

### Exceptions (D5, D11)

- **R11 — An exception is accepted, scoped and documented.** Each *Accepted exceptions* row must:
  - name an invariant whose status is ⚠️ — an exception for ❌ or ❓ fails (D11), one for ✅ is stale and fails;
  - name a decision `RG-n` that exists in `PRODUCT_CONTRACT.md` §6.1 for **the same invariant**, with *Status*
    `Accepted`, an ISO *Date* not in the future and a non-empty *Decided by*. A `Proposed` decision, a missing date or
    a missing decider fails;
  - link a limitation section that resolves to a heading in one of the product documents (README.md, SECURITY.md,
    `docs/operations/*.md`, `PRODUCT_CONTRACT.md` §5) and whose text names the invariant ID;
  - carry a *Review by* version greater than the current `<Version>`; in release mode a review version that is not
    greater fails.
  The gate cannot prove who edited a file: that the *Decided by* entry is genuine rests on review and branch
  protection, like every other accepted contract. What the gate guarantees is that no exception ships without an
  explicit, dated acceptance on record.

### Capability/path inventory and drift

- **R12 — Snapshot:** `CapabilityInventoryRenderer.Render(...)` equals `docs/v1/CAPABILITY_INVENTORY.md`, else
  `.actual` is written and the test fails (D7).
- **R13 — Code → matrix:** every catalog capability is named by at least one §7 row (M rows through their tool name,
  others through D6), or is listed as composed-only (`MutationCompositions`) or as a designed exemption.
- **R14 — Matrix → code:** every capability id and every M-row tool name in §7 exists in the catalog, and every M row
  is a catalog tool or a read tool named as such (M-41, M-44).
- **R15 — Totals:** §7.6's current-row table equals the rows counted per class, so a row added without a count (or a
  count changed without a row) fails. The 90-row WP11a baseline stays as history and is not checked (D9).
- **R16 — Mode input:** the version is resolvable and `WHISKERS_RELEASE_GATE` holds an accepted value (see *Mode and
  version*).

R13–R15 replace the promise in §8 ("§7 will be generated") with a check: §7 stays hand-written, the inventory is
generated, and the two are joined by tests.

## How it runs in CI

- `ReleaseGateTests` is an in-process test: it runs on every push and pull request in `ci.yml` → *Test (in-process)*
  and inside every release run (`release.yml` → `test` calls `ci.yml`). It is named in
  `.github/required-tests/in-process.txt` so it cannot vanish.
- The mode follows from `<Version>` (above), so the same test is in development mode while the version is below 1.0.0
  and in release mode from the version bump onwards — on pull requests, on `main`, on a pushed tag and on
  `workflow_dispatch` alike. No workflow input or environment variable is needed for release mode; none can weaken it.
- Locally: `WHISKERS_RELEASE_GATE=release dotnet test --filter ReleaseGateTests` shows what a 1.0 release would refuse
  today.

## Tests

| Test | Proves |
|---|---|
| `ReleaseGateTests.The_repository_passes_the_gate_in_its_mode` | the real files pass every rule of the mode `<Version>` selects; every finding listed |
| `ReleaseGateTests.The_mode_follows_the_version_and_the_environment_can_only_switch_it_on` | 0.15.0 → development; 1.0.0, 1.2.3 and 2.0.0 → release; `release` turns release on below 1.0.0; `Release`, `true`, `off` and a typo are findings; nothing turns release off at ≥ 1.0.0 |
| `ReleaseGateTests.An_unresolvable_version_fails` | no `<Version>`, two of them, `1.0`, `1.0.0-rc.1` |
| `ReleaseGateTests.A_non_green_invariant_fails_the_release` | R1: ⚠️ without exception fails release mode; ❌ fails even with a complete exception row |
| `ReleaseGateTests.An_open_list_that_is_missing_or_stale_fails` | R2: an unlisted ⚠️ fails; a listed ✅ fails |
| `ReleaseGateTests.A_malformed_qualified_or_miscounted_table_fails` | R3: duplicate ID, unknown symbol, `✅ per host after migration`, §11 count off by one |
| `ReleaseGateTests.Every_backticked_name_in_the_test_column_resolves` | R4: renamed method, removed class, `PrincipalPropagationTest` typo, a non-test type in backticks |
| `ReleaseGateTests.A_green_row_needs_a_proven_test` | R5: ✅ with only a planned test; ✅ whose only test is a platform skip without a CI job |
| `ReleaseGateTests.A_planned_marker_must_name_the_owed_test` | R6: planned `Class` that exists (today's INV-21 form), planned `Class.Method` that exists, planned text without a name on a ⚠️ row |
| `ReleaseGateTests.Skips_never_count_as_passes` | R7: static `Skip`; a named method with a runtime skip and no *Platform skips* row is rendered into the manifest (and so fails CI when it skips); an allowed Windows-only method is left out; a stale *Platform skips* row |
| `ReleaseGateTests.Every_named_test_is_required_per_method_in_its_ci_run` | R8: per-method expansion of a `Class` reference, minimum = case count for Fact, InlineData and MemberData theories, the Docker/in-process split, the generated block as a snapshot |
| `ReleaseGateTests.A_named_test_outside_every_checked_run_fails` | R9: an ImageRuntime test without D14, a test no filter selects |
| `ReleaseGateTests.A_mandatory_suite_that_cannot_block_the_release_fails` | R10: job removed, `continue-on-error` added, dropped from `needs`, `--no-skips` removed |
| `ReleaseGateTests.An_exception_needs_an_accepted_dated_decision_and_a_limitation` | R11: `Proposed` decision, missing date or decider, decision for another invariant, dead link, section that does not name the invariant, exception for ❌ or ✅, review version not newer |
| `ReleaseGateTests.The_capability_inventory_matches_the_code` | R12 (snapshot, `.actual`) |
| `ReleaseGateTests.Matrix_and_catalog_name_each_other` | R13/R14: a new capability without a row, a row naming an unknown capability or tool |
| `ReleaseGateTests.The_totals_match_the_rows` | R15 |
| `InvariantTableParserTests`, `PathMatrixParserTests`, `ReleaseGateDocumentTests` | parsing of the real files and of edge cases (prose in the test column, escaped `\|`, planned entries with and without a name, a missing table heading) |
| `ReleaseWorkflowTests` (extended) | with D8, `publish-image` needs the called `security.yml`; with D14, the image-runtime step writes TRX and checks its manifest |
| `RequiredTestsManifestTests` (extended) | still green with the generated blocks; uses the shared `TestCatalog` |

Negative tests mutate the text of the real files in memory, as `ReleaseWorkflowTests` does; nothing is written to the
repository except a `.actual` file on a snapshot difference.

## Plan triage

- **[Claude-prüfbar]:** every test above; the first `RELEASE_GATE.md` with today's eleven open invariants and the
  platform skips of the named classes; the generated inventory; the D6 mapping for the 58 non-M rows; the manifest
  blocks; the INV-21 marker renamed to the owed test (R6); INV-58's status cell per D12.
- **[braucht Nutzer]:** the decisions below; whether any of today's ⚠️ invariants becomes an accepted exception for
  1.0 (each needs an `RG-n` decision accepted by the maintainer); the GitHub branch-protection setting that makes
  `build-test` required (repository settings, not in the repository).

## Decisions for the maintainer

1. **D1 — Where the gate code lives.**
   (a) `src/Whiskers.Tests/ReleaseGate/`, run as ordinary tests. (b) A console tool `tools/ReleaseGate` run as its own
   CI step. (c) In the product assembly. *Recommended: (a).* It needs the test assembly for reflection anyway, adds no
   project and ships nothing; (b) duplicates the required-test machinery, (c) ships audit tooling to users.
2. **D2 — When the gate enforces "every invariant ✅".**
   (a) Whenever `<Version>` in `Whiskers.csproj` is 1.0.0 or later, in every run (pull requests, `main`, tag and
   `workflow_dispatch` releases); the environment variable can only switch release mode on. (b) Only in release runs
   for tags `v1.0.0` and later, through a workflow input. (c) On every push already now. *Recommended: (a).* It needs
   no input that could be forgotten or mistyped, it catches the problem in the pull request that bumps the version
   rather than at the tag, and after 1.0 a regression of an invariant fails the pull request that causes it. The
   consequence is deliberate: from 1.0.0 on, a new or weakened invariant needs an accepted exception before it can
   merge. (b) fails open on a missing input and lets `main` drift between releases; (c) would block all work until the
   last invariant is green.
3. **D3 — What development mode does with non-✅ invariants.**
   (a) Ratchet: each must be listed under *Open invariants*; a listed ✅ fails. (b) Report only, never fail. (c) Nothing.
   *Recommended: (a).* Every status change then shows up as a change to `RELEASE_GATE.md` in the same PR, and the
   release list is visible long before the version bump.
4. **D4 — How "the named test passes" is proven.**
   (a) A generated block of per-method entries (minimum = reflected case count) in the existing manifests, enforced by
   `check-test-results.sh --require` from the TRX file. (b) A third manifest per run (the script then takes two
   `--require`). (c) Reflection only — exists and is not statically skipped — and rely on "any failed test fails CI".
   *Recommended: (a).* It reuses R1-47's mechanism and proves from the run that every case ran and passed; per-method
   entries keep one skipping method from turning a whole class red or from hiding behind a class count; (c) cannot
   see a runtime skip or a test a filter left out.
5. **D5 — Form of an accepted exception.**
   (a) A row in `RELEASE_GATE.md` *Accepted exceptions* + an `RG-n` decision in a new `PRODUCT_CONTRACT.md` §6.1 with
   *Status* `Accepted`, *Date* and *Decided by* + a limitation section in the product docs that names the invariant +
   a mandatory *Review by* version. (b) The same without *Review by*. (c) Exceptions as PC-D rows only, no separate
   table and no acceptance fields. *Recommended: (a).* The release rule asks for a *recorded decision*; (c) would accept
   any row that merely exists, and an exception without an end tends to become permanent.
6. **D6 — How a §7 row names its capabilities.**
   (a) A new column *Capabilities* in the S, T, U and A tables. (b) A separate hand-maintained
   `docs/v1/PATH_CAPABILITIES.md` keyed by row ID. (c) Attributes in code (`[PathRow("U-22")]` on handlers).
   *Recommended: (b).* The §7 tables are already very wide and accepted as written; (b) changes no accepted row, and
   the generated inventory shows the joined view. (a) is the better reading experience if the width is acceptable;
   (c) touches many pages for a documentation link.
7. **D7 — Generated inventory vs. generated §7.**
   (a) Generate `CAPABILITY_INVENTORY.md`, keep §7 hand-written, join them by R13–R15. (b) Generate §7 itself from the
   catalog (as §8 once promised). *Recommended: (a).* §7's chain cells (actor, policy, verification, gaps) are judgments
   the catalog does not hold; generating them would lose them. §8 is reworded accordingly in the implementation PR.
8. **D8 — `security.yml` in the release.**
   (a) `release.yml` calls `security.yml` (`workflow_call`) on the tag commit and `publish-image` needs it. (b) The
   gate only checks that `security.yml` exists and runs on `main`. *Recommended: (a).* A release should not depend on
   the last weekly run having been green; this is a workflow change and needs its own `ReleaseWorkflowTests` cases.
9. **D9 — The historical 90 rows.**
   (a) Keep the WP11a table as history and check only the current-row table (added in R1-51). (b) Replace the 90 with
   today's counts. *Recommended: (a).* The baseline explains the audit; the gate needs only today's numbers.
10. **D10 — Planned markers.**
    (a) A planned entry must name the owed test — a `Class.Method` that does not exist yet, or a class that does not
    exist yet; naming an existing method, or an existing class without a method, fails; INV-21's marker is renamed to
    the owed kill-switch wire test. (b) Judge staleness by class only (the first draft): deleting INV-21's marker would
    then "fix" the finding and the owed test would disappear. (c) Warn only. *Recommended: (a).* The marker exists to
    keep a debt visible; only a precise name lets the gate tell a paid debt from an open one.
11. **D11 — May a ❌ (or ❓) invariant ship as an exception?**
    (a) No: exceptions only for ⚠️; ❌ and ❓ always fail release mode. (b) Yes, with the same decision record.
    *Recommended: (a).* A ❌ is a known violation (today INV-18: a Viewer can change shared state); shipping it under
    a document row would turn the release rule into a formality. A ❌ that is to ship must first be narrowed to a ⚠️
    whose remaining gap is stated.
12. **D12 — INV-58's qualified ✅ ("✅ per host after migration").**
    (a) Set it to ⚠️, move "per host after migration" into the evidence column, and — if 1.0 is to ship with the
    shared credential still supported — accept an `RG-n` exception whose limitation says that a host still on the
    shared credential is reachable with a stolen shared key (the readiness card names such hosts). (b) Narrow the
    invariant's statement to "every host on its own client identity" and keep ✅, stating the shared-credential case
    as a limitation only. *Recommended: (a).* (b) changes an accepted invariant to fit the code; (a) keeps the
    statement and makes the gap visible where the release rule looks for it.
13. **D13 — Runtime skips in named tests.**
    (a) A named method must run unskipped in the CI run that requires it; the only allowance is a *Platform skips* row,
    and a method that no CI job runs unskipped is left out of the manifest and does not count as evidence (R7).
    (b) Forbid runtime skips in every class an invariant names (move platform-dependent cases into separate classes).
    (c) Add a Windows CI job so Windows-only methods are proven too. (d) Subtract skips from the counts.
    *Recommended: (a), with (c) as a later improvement.* (a) keeps today's tests and is fail-closed; (b) is a large
    test reorganisation; (d) is fail-open and rejected.
14. **D14 — Tests of the image-runtime job.**
    (a) The `image-runtime` job writes a TRX file and checks its own manifest `.github/required-tests/image-runtime.txt`
    with `--require`, so invariants may name `ImageRuntime` tests. (b) Invariants must not name `ImageRuntime` tests.
    *Recommended: (a).* The image-level proofs (R1-52) are the strongest evidence for several invariants; excluding
    them would push invariants toward weaker tests. It is a workflow change with its own `ReleaseWorkflowTests` cases.
