# Contract 0008 — Mechanical release gate (R1-51)

Status: **Proposed** · 6 November 2026. Waits for the maintainer's decisions D1–D10 at the end. Nothing of the gate is
implemented before this contract is accepted; the documentation part of R1-51 (operator guides, accepted limits,
corrections) does not depend on it and is done on the same branch.

## Purpose

`SECURITY_INVARIANTS.md` states the release rule: *v1.0 ships only when every invariant is ✅ and its named test
exists and passes; a deliberate exception needs a recorded decision and a matching limitation in the product
documentation.* Today a person checks that by reading. R1-51 makes it mechanical:

1. **`ReleaseGateTests`** fails when an invariant is not ✅, a named test is missing, a mandatory suite is not wired to
   block the release, or an exception is not documented (WORK_PACKAGES R1-51, criterion 1).
2. **A generated capability/path inventory** is checked against the code and against the path matrix of
   `PRODUCT_CONTRACT.md` §7, so the matrix cannot drift silently (criterion 2).
3. **`docs/v1/RELEASE_GATE.md`** is the one page a maintainer reads before tagging: what the gate checks, the open
   invariants, the accepted exceptions and the mandatory suites — the parts of it that are data are read by the gate.

The gate proves that the **records** are consistent and complete and that the named tests exist and run. It does not
prove that an invariant holds; that is what the named tests are for (R1-51, out of scope: "treating documentation as
proof").

## Current state (verified at `e98446a2`)

- **Invariant table.** `docs/v1/SECURITY_INVARIANTS.md`, 61 rows `| INV-nn | statement | status | evidence | test | WP |`
  in sections 1–10; §11 (`:137`) repeats the counts by hand. Today: 50 ✅, 10 ⚠️ (INV-01, 13, 17, 19, 21, 25, 45, 52,
  56, 57), 1 ❌ (INV-18), 0 ❓. The release rule (`:14–18`) already promises a mechanical check by `RELEASE_GATE.md`
  and fixes the format: ID in the first column, the status symbol at the start of the third.
- **Test column format** (`:23–24`): an existing test as `` `Class.Method` `` or `` `Class` ``, a planned one as
  `` `Class` *(planned, WPx)* ``, plus free prose (for example INV-18: "covers the HTTP layer only (R1-47)").
  - 163 backticked `…Tests` names. All of them resolve to a class (and, where given, a method) in
    `src/Whiskers.Tests` (checked by text search for this draft, not by reflection).
  - Two *(planned …)* markers: INV-18 (`WP10`) and INV-21 (`KillSwitchTests` *(planned, WP6, wire test)*) — but
    `KillSwitchTests` exists (`src/Whiskers.Tests/KillSwitchTests.cs:25`), so that marker is stale.
- **Exceptions.** There is no exception register. `PRODUCT_CONTRACT.md` §6 (`:112`) holds PC-D1…PC-D8; §5 lists the
  stated limits. Designed exemptions from the gateway (S-09/S-10/S-13/S-14, the CVE scanner caches) are pinned to their
  callers by `MutationGatewayCoverageTests` and are not invariant exceptions.
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
  in-process run fails on any failed test and on fewer passed tests than its floor; the Docker run also on a skip.
- **Release wiring.** `release.yml`: `test` calls `ci.yml` on the tag commit (`:141–147`); `publish-image` needs
  `prepare, test, build, scan, artifact-test` (`:332`). `security.yml` (dependency vulnerabilities, secret scan,
  static analysis, CodeQL) is **not** part of the release run. `ReleaseWorkflowTests` pins properties of the workflow
  files with negative tests that mutate the real file text. Every `v*` tag is a release; there is no 1.0 tag yet.
- **Test framework.** xUnit 2.9.3 (`Whiskers.Tests.csproj:18`): a skip is static (`Skip =`); there is no dynamic skip
  in xUnit 2.

## Boundaries (fixed)

- The gate is **test code**. It ships nothing in the product image and adds no runtime dependency.
- It reads Markdown tables, the test assembly (reflection), the capability catalog and the workflow files. It never
  changes a document; a generated file that differs is written beside it as `<file>.actual`, as for the MCP catalog.
- It changes no invariant status, no row and no test. Fixing a finding is a human change in the same PR.
- Formats stay as they are: the invariant table keeps its columns (the release rule promises that), the §7 tables keep
  theirs (see D6 for the one addition).
- No protected area is touched (auth middleware, OAuth whitelist, mTLS/WireGuard, socket-proxy verbs).

## Documents

### `docs/v1/RELEASE_GATE.md` (new)

Human text plus three tables the gate reads. Each table starts after a fixed heading; rows are
`| a | b | … |`, the first column a key.

```markdown
## Open invariants
| Invariant | Status | Package | Why it is open |
| INV-18 | ❌ | WP10 | in-handler checks of approval, notification, scan and job actions |
…

## Accepted exceptions
| Invariant | Status shipped | Decision | Limitation | Review by |
| INV-xx | ⚠️ | PC-D9 | docs/operations/production-checklist.md#accepted-limits-10 | 1.1.0 |

## Mandatory suites
| Workflow | Job | Step (optional) | Proves |
| ci.yml | build-test | Test (in-process) | the whole in-process suite, floor and required tests |
…
```

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

public sealed record TestReference(string Class, string? Method, bool Planned, string Raw);

public sealed record InvariantRow(string Id, InvariantStatus Status, IReadOnlyList<TestReference> Tests,
                                  string Package, int Line);

public sealed record OpenInvariant(string Id, InvariantStatus Status, string Package, string Reason);
public sealed record AcceptedException(string Id, InvariantStatus Shipped, string DecisionId,
                                       string LimitationLink, string ReviewBy);
public sealed record MandatorySuite(string Workflow, string Job, string? Step, string Proves);

public sealed record PathRow(string Id, string Class /* M A S T U */, string? McpTool,
                             IReadOnlyList<string> Capabilities, int Line);

public sealed record GateFinding(string Rule, string Subject, string Message);   // Rule = R1…R15 below

public enum GateMode { Development, Release }

public static class MarkdownTables                 // one row reader for all three documents; fails on a malformed row
{
    public static IReadOnlyList<(int Line, string[] Cells)> After(string markdown, string heading);
}

public static class InvariantTableParser  { public static IReadOnlyList<InvariantRow> Parse(string markdown); }
public static class ReleaseGateDocument   { public static (IReadOnlyList<OpenInvariant>, IReadOnlyList<AcceptedException>,
                                                           IReadOnlyList<MandatorySuite>) Parse(string markdown); }
public static class PathMatrixParser      { public static IReadOnlyList<PathRow> Parse(string contract, string? pathCapabilities); }
public static class TestCatalog           // reflection over the test assembly, shared with RequiredTestsManifestTests
{
    public static IReadOnlyList<(Type Type, MethodInfo Method)> Resolve(string className, string? method);
    public static bool IsSkipped(MethodInfo method);
    public static bool IsDocker(Type type, MethodInfo method);
}
public static class CapabilityInventoryRenderer { public static string Render(ICapabilityCatalog catalog,
                                                  IReadOnlyList<PathRow> rows); }

public static class ReleaseGate
{
    public static GateMode ModeFromEnvironment();  // WHISKERS_RELEASE_GATE=release → Release, else Development (D2)
    public static IReadOnlyList<GateFinding> Evaluate(GateInputs inputs, GateMode mode);   // pure; the tests feed it
}
```

`GateInputs` holds the parsed documents, the test catalog, the capability catalog and the workflow files' text, so
every rule can be tested on mutated copies of the real files without touching disk.

## What `ReleaseGateTests` checks

The test reads the real files, runs `ReleaseGate.Evaluate` and fails with **every** finding listed (rule, subject,
file:line, what to do), not only the first.

### Non-✅ invariants

- **R1 — Release mode:** every invariant is ✅, or it has a row in *Accepted exceptions* whose *Status shipped* equals
  its status. Anything else fails.
- **R2 — Development mode (ratchet, D3):** every non-✅ invariant has a row in *Open invariants* with the same status
  and package; a row there for an invariant that is ✅ now fails as stale (the list may only shrink honestly).
- **R3 — Format:** every `INV-nn` appears once, IDs are unique and consecutive with no gaps except a listed retired ID,
  the status cell starts with one of ✅ ⚠️ ❌ ❓, and §11's counts equal the table's.

### Named tests that must exist

- **R4:** every backticked `Class` or `Class.Method` in the test column of a **✅** row resolves to at least one test
  in the test assembly (by reflection, `TestCatalog.Resolve`); a `Class` must hold at least one test.
- **R5:** a ✅ row has at least one named, non-planned test; a ✅ row with a *(planned …)* entry fails.
- **R6:** a *(planned …)* entry that names an existing class fails as stale on every row (D10) — today that is INV-21.
- **R7:** no named test is statically skipped (`Skip =`).
- Non-✅ rows: their existing names must resolve too (R4 applies), planned ones may be missing.

### "Passes": the named tests in the CI manifests (D4)

- **R8:** every test named by a ✅ invariant, or by an accepted exception, is covered by an entry of the matching
  manifest (`in-process.txt` or `docker.txt`, by `TestCatalog.IsDocker`), with a minimum that cannot let a case vanish.
  The entries live in a generated block between `# BEGIN release-gate (generated)` and `# END release-gate` in each
  manifest; the gate renders the block and compares it like a snapshot (`.actual` beside it on a difference). CI's
  existing `check-test-results.sh --require` then proves from the TRX file that each of them ran and passed.

### Mandatory suites

- **R9:** each *Mandatory suites* row names a job (and step) that exists in that workflow, has no
  `continue-on-error`, and is required by the release: a `ci.yml` job is reached through `release.yml`'s `test` job
  (`uses: ./.github/workflows/ci.yml`) and is not skipped on `workflow_call`; a `release.yml` job is in the `needs`
  closure of `publish-image`; a `security.yml` job only with D8.
- **R10:** the in-process and Docker test steps keep their floor and `--require`, the Docker step keeps
  `--no-skips` (already pinned by `ReleaseWorkflowTests`; the gate names them so removing either fails here too).
- "Unsuccessful" is decided by GitHub: a release cannot publish when a needed job failed. The gate's part is that the
  wiring cannot be removed without failing a test.

### Undocumented exceptions

- **R11:** each *Accepted exceptions* row names a non-✅ invariant (an exception for a ✅ one is stale); its decision
  ID exists in `PRODUCT_CONTRACT.md` §6 (`PC-Dn` or a new `RG-n`, D5); its limitation link resolves to a heading in
  the named document and that section mentions the invariant ID or the decision ID; *Review by* is a version newer
  than the one being released (release mode).

### Capability/path inventory and drift

- **R12 — Snapshot:** `CapabilityInventoryRenderer.Render(...)` equals `docs/v1/CAPABILITY_INVENTORY.md`, else
  `.actual` is written and the test fails (D7).
- **R13 — Code → matrix:** every catalog capability is named by at least one §7 row (M rows through their tool name,
  others through D6), or is listed as composed-only (`MutationCompositions`) or as a designed exemption.
- **R14 — Matrix → code:** every capability id and every M-row tool name in §7 exists in the catalog, and every M row
  is a catalog tool or a read tool named as such (M-41, M-44).
- **R15 — Totals:** §7.6's current-row table equals the rows counted per class, so a row added without a count (or a
  count changed without a row) fails. The 90-row WP11a baseline stays as history and is not checked (D9).

R13–R15 replace the promise in §8 ("§7 will be generated") with a check: §7 stays hand-written, the inventory is
generated, and the two are joined by tests.

## How it runs in CI

- `ReleaseGateTests` is an in-process test: it runs on every push and pull request in `ci.yml` → *Test (in-process)*,
  in **development mode**, and is named in `.github/required-tests/in-process.txt` so it cannot vanish.
- In a release, `release.yml` passes `release-gate: release` to `ci.yml` (`workflow_call` input) for tags
  `v1.0.0` and later (D2); `ci.yml` sets `WHISKERS_RELEASE_GATE=release` for the in-process test step. The same test
  then applies R1 and R11's version check. `ReleaseWorkflowTests` pins the input and the environment variable with a
  negative test each.
- Locally: `WHISKERS_RELEASE_GATE=release dotnet test --filter ReleaseGateTests` shows what a 1.0 tag would refuse
  today.

## Tests

| Test | Proves |
|---|---|
| `ReleaseGateTests.The_repository_passes_the_gate_in_its_mode` | the real files pass R2–R15 (development) or R1–R15 (release); every finding listed |
| `ReleaseGateTests.A_non_green_invariant_fails_the_release` | R1: one ⚠️ row without an exception fails release mode, passes development mode when listed as open |
| `ReleaseGateTests.An_open_list_that_is_missing_or_stale_fails` | R2: an unlisted ⚠️ fails; a listed ✅ fails |
| `ReleaseGateTests.A_malformed_or_miscounted_table_fails` | R3: duplicate ID, unknown symbol, §11 count off by one |
| `ReleaseGateTests.A_named_test_that_does_not_exist_fails` | R4: renamed method, removed class |
| `ReleaseGateTests.A_green_row_needs_a_real_test` | R5/R6: ✅ with only a planned test; a planned marker naming an existing class |
| `ReleaseGateTests.A_skipped_named_test_fails` | R7 (fixture test type with `Skip`) |
| `ReleaseGateTests.Every_named_test_is_required_in_its_ci_run` | R8: the generated manifest block, a missing entry, a too-low minimum, the Docker/in-process split |
| `ReleaseGateTests.A_mandatory_suite_that_cannot_block_the_release_fails` | R9/R10: job removed, `continue-on-error` added, dropped from `needs`, `--no-skips` removed |
| `ReleaseGateTests.An_exception_without_decision_or_limitation_fails` | R11: unknown decision ID, dead link, section that does not mention the invariant, stale exception, past review version |
| `ReleaseGateTests.The_capability_inventory_matches_the_code` | R12 (snapshot, `.actual`) |
| `ReleaseGateTests.Matrix_and_catalog_name_each_other` | R13/R14: a new capability without a row, a row naming an unknown capability or tool |
| `ReleaseGateTests.The_totals_match_the_rows` | R15 |
| `InvariantTableParserTests`, `PathMatrixParserTests`, `ReleaseGateDocumentTests` | parsing of the real files and of edge cases (prose in the test column, escaped `\|`, *(planned …)* forms) |
| `ReleaseWorkflowTests` (extended) | the release passes `release-gate: release` for `v1.*` and `ci.yml` turns it into `WHISKERS_RELEASE_GATE` |
| `RequiredTestsManifestTests` (extended) | still green with the generated blocks; uses the shared `TestCatalog` |

Negative tests mutate the text of the real files in memory, as `ReleaseWorkflowTests` does; nothing is written to the
repository except a `.actual` file on a snapshot difference.

## Plan triage

- **[Claude-prüfbar]:** every test above; the first `RELEASE_GATE.md` with today's eleven open invariants; the
  generated inventory; the D6 mapping for the 58 non-M rows; the manifest blocks.
- **[braucht Nutzer]:** the decisions below; whether any of today's open invariants becomes an accepted exception for
  1.0 (each needs a PC-D/RG decision of its own); the GitHub branch-protection setting that makes `build-test`
  required (repository settings, not in the repository).

## Decisions for the maintainer

1. **D1 — Where the gate code lives.**
   (a) `src/Whiskers.Tests/ReleaseGate/`, run as ordinary tests. (b) A console tool `tools/ReleaseGate` run as its own
   CI step. (c) In the product assembly. *Recommended: (a).* It needs the test assembly for reflection anyway, adds no
   project and ships nothing; (b) duplicates the required-test machinery, (c) ships audit tooling to users.
2. **D2 — When the gate enforces "every invariant ✅".**
   (a) Only for release tags `v1.0.0` and later (`WHISKERS_RELEASE_GATE=release`). (b) For every `v*` tag. (c) On
   every push. *Recommended: (a).* 0.x releases can still ship with open invariants, as now; (c) would block all work
   until the last invariant is green.
3. **D3 — What development mode does with non-✅ invariants.**
   (a) Ratchet: each must be listed under *Open invariants*; a listed ✅ fails. (b) Report only, never fail. (c) Nothing.
   *Recommended: (a).* Every status change then shows up as a change to `RELEASE_GATE.md` in the same PR, and the
   release list is visible long before the tag.
4. **D4 — How "the named test passes" is proven.**
   (a) A generated block in the existing manifests, enforced by `check-test-results.sh --require` from the TRX file.
   (b) A third manifest per run (the script then takes two `--require`). (c) Reflection only — exists and is not
   skipped — and rely on "any failed test fails CI". *Recommended: (a).* It reuses R1-47's mechanism and proves from
   the run that the test ran and passed; (c) cannot tell a test that a filter left out.
5. **D5 — Form of an accepted exception.**
   (a) A row in `RELEASE_GATE.md` *Accepted exceptions* + a decision in `PRODUCT_CONTRACT.md` §6 (new IDs `RG-n`) + a
   limitation section in the product docs that names the invariant, with a mandatory *Review by* version.
   (b) The same without *Review by*. (c) Exceptions only as PC-D rows, no separate table. *Recommended: (a).* An
   exception without an end date tends to become permanent; a version is easier to enforce than a date.
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
10. **D10 — Stale *(planned …)* markers.**
    (a) Fail when a planned marker names a class that exists (today INV-21's `KillSwitchTests`). (b) Warn only.
    *Recommended: (a).* A planned marker that is true already hides whether the invariant's evidence was updated; the
    fix is one edit in the same PR that introduces the gate.
