# Contract 0008 — Mechanical release gate (R1-51)

Status: **Accepted** · 6 November 2026 by the maintainer, with D1–D16 as recommended (drafted and revised after
review rounds 1 to 4 the same day). The documentation part of R1-51 (operator guides, accepted limits, corrections)
does not depend on it and is done on the same branch. **Amendment 1** (implementation) was accepted on 6 November 2026; **Amendment 2** (gate review rounds 1 to 4) is pending acceptance. Both are at the end.

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
  in sections 1–10; §11 (`:139`) repeats the counts by hand. No list of retired IDs exists, and nothing records the
  highest ID ever issued (today INV-61, `:49`). Today: 50 ✅, 10 ⚠️ (INV-01, 13, 17, 19, 21, 25, 45, 52,
  56, 57), 1 ❌ (INV-18), 0 ❓. The release rule (`:14–18`) already promises a mechanical check by `RELEASE_GATE.md`
  and fixes the format: ID in the first column, the status symbol at the start of the third.
  - **One status cell carries a qualifier:** INV-58 (`:135`) reads `✅ per host after migration`. Hosts still on the
    shared mTLS client credential are not covered by it, so this is a partial ✅ in a cell that starts with ✅.
- **Test column.** The legend (`:23–25`) names an existing test as `` `Class.Method` `` or `` `Class` `` and a
  planned one as `` `Class.Method` `` or a new `` `Class` ``, each *(planned, WPx)* (aligned by the R1-51
  documentation commits; before that it named only `Class.Method`, although most rows use the bare class). Prose stands beside the names (INV-17: "R1-47: the HTTP layer …").
  - 163 backticked `…Tests` names. All of them resolve to a class (and, where given, a method) in
    `src/Whiskers.Tests` — checked by text search for this draft, not by reflection.
  - Two *(planned …)* entries:
    - INV-18: "the in-handler checks of these actions *(planned, WP10)*" — it names **no** class at all.
    - INV-21: "`KillSwitchTests` *(planned, WP6, wire test)*". A class `KillSwitchTests` exists
      (`src/Whiskers.Tests/KillSwitchTests.cs:20–25`), but it covers something else: unattended runs that must not
      auto-confirm (R1-01) and key revocation (INV-08). None of its tests touches the read-only kill switch. The entry
      therefore stands for a kill-switch wire test that is **still owed**; only its name collides with an existing
      class.
- **Runtime skips.** `Whiskers.Tests.csproj:20` references `Xunit.SkippableFact` 1.5.85. Nine test classes call
  `Skip.If` / `Skip.IfNot` directly; five PostgreSQL classes skip through the helper
  `DockerRequirement.SkipUnless*` (`DockerRequirement.cs`; `DockerRequirementTests.cs:46` has the call only inside a
  string). xUnit 2 also reports a theory whose data cannot be serialized (or with pre-enumeration off) as **one**
  result named `Class.Method`, not one per data row, and an `InlineData` row can carry its own `Skip =`. Classes named by ✅ rows that contain runtime skips: `HostShellDeadlineTests`
  (3 calls), `PerHostCredentialShellTests` (1), `PerHostCredentialTests` (2 — for example
  `On_windows_a_key_readable_by_everyone_is_exposed`, `:946`, which skips on every non-Windows run),
  `SecretEnvironmentTests` (1), `VpnSecretHandlingTests` (11). Every CI test job runs on Linux (`build-test`, `ci.yml:33`, and
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
- Formats stay as they are, with four precise additions made in the implementation PR: the status cell holds one
  bare symbol (R3), planned entries name the owed test (R6), §6 gets a release-decision table (R11), and
  `RELEASE_GATE.md` pins the highest invariant ID and lists retired ones (R3).
- No protected area is touched (auth middleware, OAuth whitelist, mTLS/WireGuard, socket-proxy verbs).

## Mode and version (D2, D15)

There are three modes, each strictly stricter than the one before:

| Mode | When | Non-✅ invariants |
|---|---|---|
| **Development** | `<Version>` below 1.0.0 — pull requests, `main` and 0.x release runs | allowed when listed under *Open invariants* (R2) |
| **Line** | `<Version>` 1.0.0 or later: every pull request and every push to `main` | every non-✅ listed under *Open invariants* (R2), with an advisory or issue reference unless an accepted exception covers it (D15 (a), (c) or (d); with (b) the Release rules) |
| **Release** | `<Version>` 1.0.0 or later **and** the run is a release run (below); or a local `dry-run` | ✅, or ⚠️ with an accepted exception; ❌/❓ only through a known-violation record for exactly this patch release (R17) with D15 (b), (c) or (d), else never — never at X.Y.0 |

- **The version is `<Version>` in `src/Whiskers/Whiskers.csproj`.** `release.yml` already proves that a release tag
  equals it (`:100–107`), on a pushed tag and on `workflow_dispatch` alike, so the gate needs no tag input to know
  which version a run would release. Comparison is on the three numbers; `prepare` admits no pre-release suffix.
- **Line mode follows from the version alone.** From the pull request that bumps the version to 1.0.0 on, every pull
  request and every push to `main` runs at least in Line mode; nothing in a pull request can lower it.
- **Only `release.yml` makes a run a release run.** Its `test` job passes the input `release-gate: release` to
  `ci.yml` (`workflow_call`), and `ci.yml` sets `WHISKERS_RELEASE_GATE=release` for the in-process test step. The
  variable can only make a run **stricter**: `release` turns Line into Release and has no effect below 1.0.0 (0.x
  releases ship with open invariants, as today); `dry-run` applies the Release rules at any version, for a local look
  at what a 1.0 release would refuse. Absent or empty, it changes nothing. Any other value (`Release`, `true`, a typo)
  is a finding. `ci.yml` sets the variable only from that input, never from anything a pull request controls.
  Because it can only add strictness, setting it gains nothing; because `ReleaseWorkflowTests` pins
  the input in `release.yml` and the variable in `ci.yml` (and R10 names them), removing it from a release run fails a
  test.
- **A version the gate cannot resolve fails**: no `Whiskers.csproj`, no `<Version>`, more than one, or a value that is
  not `X.Y.Z`.

## Documents

### `docs/v1/RELEASE_GATE.md` (new)

Human text, one pinned line and six tables the gate reads. Each table starts after a fixed heading; rows are
`| a | b | … |`, the first column a key. A heading that is missing, a second pin line, or a row that does not have the
table's column count is a finding.

```markdown
Highest invariant ID: INV-61

## Open invariants
| Invariant | Status | Package | Why it is open | Reference |
| INV-18 | ❌ | WP10 | in-handler checks of approval, notification, scan and job actions | — |
…

## Retired invariants
| Invariant | Decision | Date |
| INV-nn | RG-n | 2027-01-15 |

## Known violations
| Invariant | Status | Decision | Advisory | Limitation | Release | Review by |
| INV-nn | ❌ | RG-n | GHSA-xxxx-xxxx-xxxx | docs/operations/production-checklist.md#known-violations | 1.0.1 | 1.0.2 |

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

*Reference* is `—` in Development mode and must name an advisory or an issue of this repository for a ❌/❓ row in
Line mode (D15). The gate checks the **format only** — `GHSA-xxxx-xxxx-xxxx` or
`https://github.com/LupusMalusDeviant/Whiskers/issues/<n>` — and makes no network call, so offline and fork builds
behave the same; whether the advisory is published (for an unfixed vulnerability it is often still a private draft)
is left to review. The same holds for *Advisory* under *Known violations*. *Known violations* exists only with D15
(b), (c) or (d).

### `PRODUCT_CONTRACT.md` §6.1 "Release decisions" (new table, D5)

`| ID | Kind | Invariant | Decision | Release | Status | Date | Decided by |` with IDs `RG-1`, `RG-2`, … *Kind* is
`exception`, `retirement` or `known violation`; *Release* is the exact version a `known violation` covers and `—`
for the other kinds; *Status* is `Proposed` or `Accepted`; *Date* is the ISO date of acceptance; *Decided by* names
the maintainer who accepted it. Every rule that relies on a decision (R3 retirements, R11 exceptions, R17 known
violations) requires the matching *Kind*, the same invariant, *Status* `Accepted`, a date not in the future and a
non-empty *Decided by*. **An `Accepted` row is immutable** (R18): it is never edited or deleted, and an ID is never
reused; a changed decision is a new row with a new ID. A retirement that an earlier PC-D decision already settled is recorded
as an `RG-n` of kind `retirement` that cites it: the PC-D rows carry no per-row status, date or decider, so they
cannot be checked themselves.

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

public sealed record OpenInvariant(string Id, InvariantStatus Status, string Package, string Reason, string? Reference);
public sealed record RetiredInvariant(string Id, string DecisionId, DateOnly? Date);
public sealed record KnownViolation(string Id, InvariantStatus Status, string DecisionId, string Advisory,
                                    string LimitationLink, string Release, string ReviewBy);
public sealed record AcceptedException(string Id, InvariantStatus Shipped, string DecisionId,
                                       string LimitationLink, string ReviewBy);
public sealed record ReleaseDecision(string Id, string Kind, string Invariant, string? Release, string Status,
                                     DateOnly? Date, string DecidedBy);
public sealed record PlatformSkip(string Test, string SkipsWhen, string? RunsUnskippedIn, string Why);
public sealed record MandatorySuite(string Workflow, string Job, string? Step, string Proves);

public sealed record PathRow(string Id, string Class /* M A S T U */, string? McpTool,
                             IReadOnlyList<string> Capabilities, int Line);

public sealed record GateFinding(string Rule, string Subject, string Message);   // Rule = R1…R18 below

public enum GateMode { Development, Line, Release }                              // each strictly stricter
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
    public static int CaseCount(MethodInfo method);         // the test cases xUnit's own discovery reports (= TRX results)
    public static bool IsStaticallySkipped(MethodInfo method); // method-level Skip = or any data row's Skip =
    public static TestRun RunOf(Type type, MethodInfo method);   // InProcess | Docker | ImageRuntime | None
}
public static class CapabilityInventoryRenderer { public static string Render(ICapabilityCatalog catalog,
                                                  IReadOnlyList<PathRow> rows); }

public static class ReleaseGate
{
    public static GateVersion ReadVersion(string csproj);                       // throws on anything but one X.Y.Z
    // < 1.0.0 → Development; ≥ 1.0.0 → Line; "release" at ≥ 1.0.0 or "dry-run" at any version → Release.
    // Never lower than the version alone gives; any other non-empty value throws (a finding, R16).
    public static GateMode Mode(GateVersion version, string? environmentValue);
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
  exception (R11), or — only with D15 (b), (c) or (d) and only in a patch release — it is ❌/❓ and has a valid *Known violations* record for exactly this version (R17). Nothing
  else passes; in particular no exception is ever accepted for ❌ or ❓ (D11).
- **R2 — Development and Line mode (ratchet, D3):** every non-✅ invariant has a row in *Open invariants* with the
  same status and package; a row there for an invariant that is ✅ now fails as stale (the list may only shrink
  honestly). In **Line** mode additionally (D15 (a), (c) or (d); with D15 (b) Line mode applies the Release rules of R1), every *Open invariants* row of a non-✅ invariant that has no accepted
  exception must carry a *Reference* (`GHSA-…` or an issue URL of this repository, format check only, D15). A newly
  found gap can therefore be recorded honestly — ❌, ❓ or ⚠️, whichever is true — in the pull request that finds it
  and stays mergeable; there is never a reason to write a better status than the true one. It cannot be released:
  Release mode (R1) still needs ✅, an accepted exception for ⚠️, or — only with D15 (b), (c) or (d), in a patch release — a known-violation record.
- **R3 — Format and completeness, all modes:**
  - every `INV-nn` in the table appears once and §11's counts equal the table's;
  - `RELEASE_GATE.md` pins `Highest invariant ID: INV-nn`. Every ID from INV-01 up to the pinned one is either a
    row of the table or a row of *Retired invariants*, never both; an ID above the pin is a finding (raise the pin in
    the same pull request that adds the row);
  - a *Retired invariants* row names an `RG-n` decision of kind `retirement` for that invariant (Accepted, dated,
    decided by, §6.1). Retired IDs stay listed for good and are never reused; the pin may not fall below the highest
    retired ID;
  - consequence: deleting the last row and lowering §11 is a finding (the pinned ID is missing and not retired). To
    hide it a pull request would have to change the pin as well — three conspicuous edits in two files, which D16
    makes a CI finding too;
  - **the status cell is one bare symbol** — ✅, ⚠️, ❌ or ❓ and nothing else. A qualifier ("✅ per host after migration") fails: the qualification belongs in the evidence column,
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
  without any backticked name (INV-18 today) is allowed only on a ❌ or ❓ row. For INV-21 the implementation PR replaces `` `KillSwitchTests` *(planned …)* `` with the name of the owed
  wire test (for example `` `ReadOnlyKillSwitchWireTests` *(planned, WP6)* ``), so the debt stays visible.
- **R7 — Skips (D13).** No named test is statically skipped: neither a method-level `Skip =` nor a `Skip =` on any
  of its data rows (`InlineData(…, Skip = …)` and the like count as static skips of that method). Runtime skips are
  handled by R8: a named method that skips in the run that requires it fails there, because a skip is not a pass.
  The only way to keep a platform-dependent method named is a *Platform skips* row:
  - the row names the method (`Namespace.Class.Method`), the condition and the CI job in which it runs unskipped;
  - a named job must be a *Mandatory suites* row (R10) whose step writes a TRX file and checks its own manifest with
    `--require`. The method is rendered into **that** job's manifest (not into the manifest of the run where it
    skips) and counts as proven only there. A job that does not exist, is not a mandatory suite, or checks no
    manifest is a finding;
  - a method with *Runs unskipped in* = "—" (no CI job can run it, such as the Windows-only ACL test) is **left out**
    of the generated manifest entries and does **not** count as evidence: a ✅ row needs at least one other named
    test that is proven in CI (R5 counts only proven tests);
  - a *Platform skips* row for a method that no invariant names, or that does not exist, is stale and fails.
  Subtracting skipped results from the counts is not an option: it would let any `Skip.If(...)` drop a mandatory
  test silently.

### "Passes": the named tests in the CI manifests (D4)

- **R8 — One entry per method, exact minimum.** For every test named by a ✅ invariant, or by an accepted exception,
  the gate renders one manifest entry **per method**: a `Class` reference expands to every test method of the class,
  each with its own entry. The minimum of an entry is `TestCatalog.CaseCount(method)`: the number of test cases
  **xUnit's own discovery** reports for the method (run through xUnit's discovery API on the test assembly, with the
  project's settings) — exactly the number of results the TRX file will hold. That is 1 for a `[Fact]` or
  `[SkippableFact]`, one per data row for a theory whose data xUnit pre-enumerates, and 1 for a theory whose data it
  cannot serialize (or with pre-enumeration off), which xUnit reports as a single `Class.Method` result. A method the
  discovery does not report is a finding. The entries live in a generated block
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

### Exceptions and known violations (D5, D11, D15)

- **R11 — An exception is accepted, scoped and documented.** Each *Accepted exceptions* row must:
  - name an invariant whose status is ⚠️ — an exception for ❌ or ❓ fails (D11), one for ✅ is stale and fails;
  - name a decision `RG-n` that exists in `PRODUCT_CONTRACT.md` §6.1 for **the same invariant**, with *Status*
    `Accepted`, an ISO *Date* not in the future and a non-empty *Decided by*. A `Proposed` decision, a missing date or
    a missing decider fails;
  - link a limitation section that resolves to a heading in one of the product documents (README.md, SECURITY.md,
    `docs/operations/*.md`, `PRODUCT_CONTRACT.md` §5) and whose text names the invariant ID;
  - carry a *Review by* version greater than the current `<Version>`, in **all modes**: an exception that is due
    fails the next pull request, not only the next release.
  The gate cannot prove who edited a file: that the *Decided by* entry is genuine rests on review and branch
  protection, like every other accepted contract. What the gate guarantees is that no exception ships without an
  explicit, dated acceptance on record.
- **R17 — Known violations (only with D15 (b), (c) or (d)).** A *Known violations* row lets **one patch release** ship
  while a ❌/❓ invariant is still open. Each row must:
  - name an invariant whose status is ❌ or ❓, with a *Status* equal to the table's;
  - name an `RG-n` of kind `known violation` for that invariant (Accepted, dated, decided by, §6.1) whose *Release*
    equals the row's *Release*;
  - have *Release* equal to the current `<Version>`, and that version must be a **patch release** (patch number ≥ 1):
    a record at `X.Y.0` — 1.0.0 itself, or any minor or major release — fails, so no X.Y.0 ships with a ❌/❓;
  - have *Review by* equal to the next patch after *Release*. The field adds no constraint of its own (R17 already
    fixes *Release* = `<Version>`); it is kept so a reader of the table sees when the record lapses, and the gate
    only checks that it matches;
  - carry an *Advisory* in `GHSA-xxxx-xxxx-xxxx` form (format check only; that the release notes link it is left to
    review, or to `release.yml` if it is later taught to check);
  - link a limitation section in the product documents that names the invariant ID, exactly as R11 requires for an
    exception, so the accepted release rule (`SECURITY_INVARIANTS.md:14–16`: every deliberate gap is documented) and
    the gate agree;
  - with D15 (d) only: name an invariant that was ✅ or ⚠️ in the previous release tag (as defined in R18: the
    highest `vX.Y.Z` tag strictly below `<Version>`). The check runs in `ReleaseGateTests` in `build-test`, in release
    runs only, reading `git show <previous tag>:docs/v1/SECURITY_INVARIANTS.md` after the tag fetch of R18; a
    previous tag that is missing or unreadable is a finding, never "first occurrence";

  In every mode a row whose *Release* is not the current `<Version>` fails. Consequences: the record must be written
  in the pull request that bumps `<Version>` to the patch it covers (a record written while `main` is still at the
  previous version fails at once, and one left behind fails the next bump); and renewing it by editing only
  *Review by* or *Release* fails, because *Release* must equal both `<Version>` and the decision's *Release*, and the decision itself cannot
  be edited (R18). A second release with the same violation needs a new decision with a new ID. Without D15 (b), (c)
  or (d) the table must be absent or empty.
- **R18 — Accepted decisions are immutable (with D16 (a)).** The base-diff step of D16 also compares §6.1 with a
  base: an `Accepted` row that changes in any cell or disappears fails, an ID that reappears with different content
  fails, and a new row must use an ID above every ID in the base. The base is the pull request's base commit on pull
  requests, and the **previous release tag** in release runs. *Previous release tag* means the highest `vX.Y.Z` tag
  strictly below `<Version>` (compared on the three numbers, not by date, and regardless of whether a GitHub release
  was published for it). In release runs `build-test` fetches the tags (`git fetch --tags`, only when `release-gate`
  is `release`); for 1.0.0 that is the highest 0.x tag. A previous tag that is missing (no such tag found, for example
  because the fetch failed) or whose files cannot be read is a finding, never a pass.
  **Bootstrap rule.** A base whose `<Version>` is below 1.0.0 (a 0.x tag, or a pull request's base commit before the
  1.0 bump) and whose `SECURITY_INVARIANTS.md` and `PRODUCT_CONTRACT.md` exist but lack §6.1, the pin or *Retired
  invariants* counts as an **empty** base for the missing parts: no decisions, no retired IDs, no pin. A base file
  that is missing, or a base at 1.0.0 or above without these tables, is a finding.
  Together with the `origin/main` reachability check in `prepare` (D16), a tag cannot carry an edited decision past
  the gate, assuming release tags are not moved (tag protection or immutable releases recommended). Within one commit the gate can only check that IDs are unique; without D16 (a) immutability rests on
  review.

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
  version*); `release.yml` passes `release-gate: release` and `ci.yml` turns it into the variable for the in-process
  step (both also pinned by `ReleaseWorkflowTests`).

R13–R15 replace the promise in §8 ("§7 will be generated") with a check: §7 stays hand-written, the inventory is
generated, and the two are joined by tests.

## How it runs in CI

- `ReleaseGateTests` is an in-process test: it runs on every push and pull request in `ci.yml` → *Test (in-process)*
  and inside every release run (`release.yml` → `test` calls `ci.yml`). It is named in
  `.github/required-tests/in-process.txt` so it cannot vanish.
- The mode follows from `<Version>` and the run (above): Development below 1.0.0; Line on every pull request and
  `main` push from the version bump onwards; Release only in runs of `release.yml` (pushed tag or
  `workflow_dispatch`), which pass `release-gate: release` to `ci.yml`. A release run is therefore always at least as
  strict as the pull request and `main` runs of the same commit, and only `release.yml` can make it stricter.
- Locally: `WHISKERS_RELEASE_GATE=dry-run dotnet test --filter ReleaseGateTests` shows what a 1.0 release would refuse
  today.

## Tests

| Test | Proves |
|---|---|
| `ReleaseGateTests.The_repository_passes_the_gate_in_its_mode` | the real files pass every rule of the mode `<Version>` selects; every finding listed |
| `ReleaseGateTests.The_mode_follows_the_version_and_the_run_and_can_only_get_stricter` | 0.15.0 → Development, also with `release`; 1.0.0, 1.2.3 and 2.0.0 → Line; `release` at ≥ 1.0.0 → Release; `dry-run` → Release at any version; `Release`, `true`, `off` and a typo are findings; no value yields a mode below the one the version gives |
| `ReleaseGateTests.An_unresolvable_version_fails` | no `<Version>`, two of them, `1.0`, `1.0.0-rc.1` |
| `ReleaseGateTests.A_non_green_invariant_fails_the_release` | R1: ⚠️ without exception fails Release; ❌ fails Release even with a complete exception row; ❌ with a valid known-violation record passes only with D15 (b), (c) or (d) and only in a patch release |
| `ReleaseGateTests.An_open_list_that_is_missing_or_stale_fails` | R2: an unlisted ⚠️ fails, also when an accepted exception covers it (the exception only waives the *Reference*); a listed ✅ fails |
| `ReleaseGateTests.A_new_violation_is_mergeable_after_1_0_only_with_a_reference` | R2 in Line mode: a ❌ with an *Open invariants* row and a `GHSA-…` or issue reference passes the pull request; without a reference, or with a reference in neither format, it fails (no network call); the same files fail in Release mode |
| `ReleaseGateTests.A_malformed_qualified_or_miscounted_table_fails` | R3: duplicate ID, unknown symbol, `✅ per host after migration`, §11 count off by one |
| `ReleaseGateTests.A_deleted_invariant_is_caught_even_with_the_count_adjusted` | R3: delete the INV-61 row and change §11 to match — fails (pinned ID missing, not retired); a row above the pin fails; a *Retired invariants* row without an accepted `retirement` decision fails; a retired ID that is also a row fails; a pin below the highest retired ID fails |
| `ReleaseGateTests.Every_backticked_name_in_the_test_column_resolves` | R4: renamed method, removed class, `PrincipalPropagationTest` typo, a non-test type in backticks |
| `ReleaseGateTests.A_green_row_needs_a_proven_test` | R5: ✅ with only a planned test; ✅ whose only test is a platform skip without a CI job |
| `ReleaseGateTests.A_planned_marker_must_name_the_owed_test` | R6: planned `Class` that exists (today's INV-21 form), planned `Class.Method` that exists, planned text without a name on a ⚠️ row |
| `ReleaseGateTests.Skips_never_count_as_passes` | R7: method-level `Skip`; a skipped `InlineData` row; a named method with a runtime skip and no *Platform skips* row is rendered into the manifest (and so fails CI when it skips); an allowed Windows-only method is left out; a *Platform skips* row naming a real job renders into that job's manifest; one naming a job that is not a mandatory suite with `--require` fails; a stale row |
| `ReleaseGateTests.Every_named_test_is_required_per_method_in_its_ci_run` | R8: per-method expansion of a `Class` reference, minimum = discovered case count for a Fact, an `InlineData` theory, a `MemberData` theory and a theory with non-serializable data (one case), the Docker/in-process split, the generated block as a snapshot |
| `ReleaseGateTests.A_named_test_outside_every_checked_run_fails` | R9: an ImageRuntime test without D14, a test no filter selects |
| `ReleaseGateTests.A_mandatory_suite_that_cannot_block_the_release_fails` | R10: job removed, `continue-on-error` added, dropped from `needs`, `--no-skips` removed |
| `ReleaseGateTests.An_exception_needs_an_accepted_dated_decision_and_a_limitation` | R11: `Proposed` decision, missing date or decider, decision of another kind or for another invariant, dead link, section that does not name the invariant, exception for ❌ or ✅, review version not newer (in Development mode too) |
| `ReleaseGateTests.A_known_violation_covers_exactly_one_release` | R17 (D15 (b), (c) or (d)): an accepted record at 1.0.1 with *Release* 1.0.1 lets Release pass; *Release* other than `<Version>` fails; *Release* differing from the decision's *Release* fails; *Review by* other than the next patch fails; a renewal at 1.0.2 that changes only *Review by* fails; a record at 1.1.0 (X.Y.0) fails; a record without a limitation section naming the invariant fails; an advisory not in `GHSA-` form fails; a record for a ⚠️ or ✅ fails; with D15 (d), an invariant already ❌ in the previous tag fails; without D15 (b), (c) or (d) any row fails |
| `ReleaseGateTests.An_accepted_decision_cannot_change` | R18 (D16 (a)), against an in-memory base: an `Accepted` §6.1 row edited in one cell fails; a deleted one fails; a reused ID fails; a new ID at or below the base's highest fails; a new row passes; the same checks against a previous-tag base in a release run; the previous tag is the highest `vX.Y.Z` strictly below `<Version>` (1.0.2 → v1.0.1 even if v1.1.0 exists; 1.0.0 → highest 0.x); no previous tag or an unreadable file is a finding; bootstrap: a base below 1.0.0 whose files exist but lack §6.1, the pin or *Retired invariants* is an empty base and passes, a missing base file fails, a base at 1.0.0 or above without the tables fails |
| `ReleaseGateTests.The_capability_inventory_matches_the_code` | R12 (snapshot, `.actual`) |
| `ReleaseGateTests.Matrix_and_catalog_name_each_other` | R13/R14: a new capability without a row, a row naming an unknown capability or tool |
| `ReleaseGateTests.The_totals_match_the_rows` | R15 |
| `InvariantTableParserTests`, `PathMatrixParserTests`, `ReleaseGateDocumentTests` | parsing of the real files and of edge cases (prose in the test column, escaped `\|`, planned entries with and without a name, a missing table heading) |
| `ReleaseWorkflowTests` (extended) | `release.yml` passes `release-gate: release` and `ci.yml` sets the variable only from that input; with D8, `publish-image` needs the called `security.yml`; with D14, the image-runtime step writes TRX and checks its manifest; with D16, the base-diff step exists on pull requests and its checkout has `fetch-depth: 0` (or fetches the base SHA); with D16 (a), `prepare` refuses a tag commit not reachable from `origin/main` and the release run repeats the comparison against the previous release tag; with D16 (a) or D15 (d), `build-test` fetches tags only when `release-gate` is `release`, and the gate's previous-tag lookup (highest `vX.Y.Z` strictly below `<Version>`) turns a missing or unreadable tag into a finding |
| `RequiredTestsManifestTests` (extended) | still green with the generated blocks; uses the shared `TestCatalog` |

Negative tests mutate the text of the real files in memory, as `ReleaseWorkflowTests` does; nothing is written to the
repository except a `.actual` file on a snapshot difference.

## Plan triage

- **[Claude-prüfbar]:** every test above; the first `RELEASE_GATE.md` with the pin `INV-61`, today's eleven open invariants and the
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
2. **D2 — When the gate tightens.**
   (a) Three modes: Development below 1.0.0; **Line** for every pull request and `main` push once `<Version>` is
   1.0.0 or later (follows from the version alone); **Release** only in `release.yml` runs, which pass
   `release-gate: release`. The variable can only make a run stricter. (b) Release rules only in release runs for
   tags `v1.0.0` and later, nothing in between. (c) The full Release rules on every pull request and `main` push from
   1.0.0 on (the first draft). (d) Release rules on every push already now. *Recommended: (a).* Line mode catches an
   unrecorded or misreported change in the pull request that causes it — every non-✅ invariant must be listed with a
   reference, and a ⚠️ meant to ship needs an accepted exception — while still letting a newly found ❌/❓ be recorded
   honestly and merged (D15). The release run stays strictly stricter and only `release.yml` can make it so; nothing a
   pull request sets can lower a mode. (b) lets `main` drift unrecorded between releases; (c) makes an honest ❌ after
   1.0 unmergeable and so rewards writing ⚠️ or ✅ instead (the first draft's rationale promised an exception path
   that D11 rules out); (d) would block all work until the last invariant is green.
3. **D3 — What development mode does with non-✅ invariants.**
   (a) Ratchet: each must be listed under *Open invariants*; a listed ✅ fails. (b) Report only, never fail. (c) Nothing.
   *Recommended: (a).* Every status change then shows up as a change to `RELEASE_GATE.md` in the same PR, and the
   release list is visible long before the version bump.
4. **D4 — How "the named test passes" is proven.**
   (a) A generated block of per-method entries (minimum = the case count xUnit's discovery reports) in the existing
   manifests, enforced by
   `check-test-results.sh --require` from the TRX file. (b) A third manifest per run (the script then takes two
   `--require`). (c) Reflection only — exists and is not statically skipped — and rely on "any failed test fails CI".
   *Recommended: (a).* It reuses R1-47's mechanism and proves from the run that every case ran and passed; per-method
   entries keep one skipping method from turning a whole class red or from hiding behind a class count; (c) cannot
   see a runtime skip or a test a filter left out.
5. **D5 — Form of an accepted exception (and of every other release decision).**
   (a) A row in `RELEASE_GATE.md` *Accepted exceptions* + an `RG-n` decision of kind `exception` in a new
   `PRODUCT_CONTRACT.md` §6.1 "Release decisions" with *Status* `Accepted`, *Date* and *Decided by* + a limitation
   section in the product docs that names the invariant + a mandatory *Review by* version, checked in all modes. The
   same §6.1 table carries retirements (R3) and known violations (R17, with their *Release*); accepted rows are immutable (R18). (b) The same without *Review by*. (c) Exceptions as PC-D rows only, no separate
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
11. **D11 — May a ❌ (or ❓) invariant ship as an *exception*?**
    (a) No: exceptions (open-ended until their *Review by*, documented as a product limitation) only for ⚠️. (b) Yes,
    with the same decision record. *Recommended: (a).* A ❌ is a known violation (today INV-18: a Viewer can change
    shared state); making it a standing product limitation would turn the release rule into a formality. Whether a
    release may still go out while a ❌/❓ is open, for one patch release under an advisory, is a separate and much
    narrower question: D15. R17 enforces that narrowness: a known-violation record is tied to one exact patch version
    (never X.Y.0, so 1.0.0 cannot ship with today's INV-18 ❌) and needs a documented limitation. With D16 (a) it
    also cannot be renewed without a new decision (R18, on pull requests and in release runs); without D16 (a) that
    rests on review.
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
15. **D15 — A ❌/❓ found after 1.0.**
    Example: after 1.0.0 a report shows that a Viewer can start a scan through a new path.
    (a) **Record and merge, never release.** In Line mode a ❌/❓ (or a new ⚠️) passes when it has an *Open
    invariants* row with an advisory or issue reference (format check only); Release mode refuses it.
    The fix, or an honest narrowing to a stated ⚠️ with an accepted exception, has to land before the next release —
    also a patch release that fixes something unrelated waits.
    (b) **A known-violation record for one patch release, without (a).** A *Known violations* row (R17) lets exactly
    one patch release (patch ≥ 1, never X.Y.0) ship with the ❌/❓: *Release* = that version = the decision's
    *Release*, an accepted `RG-n`, a `GHSA-…` advisory and a documented limitation naming the invariant. Pull requests
    and `main` stay as strict as Release mode otherwise, so the honest ❌ cannot be merged until the record exists.
    (c) **(a) and (b) together.** Line mode records and merges as in (a); a release run passes only with a valid R17
    record for exactly its version.
    (d) **(c) plus a first-occurrence check.** As (c), but a release run also requires that the invariant was ✅ or ⚠️
    in the previous release tag, so a given violation can ship under a record at most once; a second patch with the
    same ❌ must wait for the fix.
    *Recommended: (c).* (a) alone blocks an urgent unrelated fix — for instance a second security fix — behind the
    first one, which helps nobody: the shipped release has the violation either way, and the advisory tells users.
    (b) alone leaves the honest ❌ unmergeable until the maintainer has written a decision, which invites writing ⚠️ in
    the meantime. What (c) guarantees is only what R17 and R18 check: no X.Y.0 ships with a ❌/❓; each patch release
    that does names its exact version in an accepted decision with an advisory and a documented limitation; the record
    cannot be stretched to a later version by editing a cell, because *Release* must equal the current version and the
    decision's *Release*, and an accepted decision cannot change (R18, with D16 (a); without it, review). It does
    **not** stop the maintainer from accepting a new decision for the next patch: consecutive patches may ship the
    same violation, each under its own visible ID. (d) closes that too, at the cost of a check against the previous
    tag in release runs and of blocking a second urgent patch while a hard fix is still in progress; take (d) if a
    violation should never ship twice.
16. **D16 — Removed invariant IDs, beyond the pin.**
    The pinned highest ID (R3) catches a deleted row unless the same pull request also lowers the pin. (a) Add a CI
    step on pull requests that compares the invariant IDs of the table plus *Retired invariants* with those of the
    pull request's base commit (`git show <base>:docs/v1/SECURITY_INVARIANTS.md`) and fails when an ID disappears
    without a new *Retired invariants* row or when the pin goes down. The same step compares §6.1 of
    `PRODUCT_CONTRACT.md` with the base and enforces R18 (accepted decisions immutable, IDs never reused). (b) Rely on
    the pin and on review (with CODEOWNERS on `docs/v1/` recommended). Either way the release-run comparison assumes
    release tags are not moved (tag protection or immutable releases recommended). *Recommended: (a).* It turns the remaining
    three-file edit and any silent edit of an accepted decision into a CI failure. The step runs on pull requests in
    `ci.yml`, whose checkout then needs `fetch-depth: 0` or an explicit fetch of the base SHA.
    A release tag can be pushed on a commit that never went through a pull request, so with (a) the comparison also
    runs in **release runs**, against the *previous release tag* (the highest `vX.Y.Z` tag strictly below
    `<Version>`, see R18) instead of the pull request's base: §6.1 immutability, IDs that disappear and a pin that goes
    down are checked there too. In addition, `prepare` in `release.yml` requires the tag commit to be reachable from
    `origin/main` (`git merge-base --is-ancestor <tag commit> origin/main`); a tag on a side branch fails before
    anything is built. Both are `ReleaseWorkflowTests` cases.


## Amendment 1 — implementation (6 November 2026)

This amendment takes precedence over the text above. It was accepted on 6 November 2026.

- **Bootstrap for 0.x bases (R18).** A base tag below 1.0.0 that lacks `docs/v1/SECURITY_INVARIANTS.md` or
  `docs/v1/PRODUCT_CONTRACT.md` counts as an empty base, like a 0.x base without the tables. A missing file stays a
  finding when the base is 1.0.0 or above. (v0.14.1, the base of the next 0.x release, has neither file.)
- **Base comparison reads IDs line by line.** The comparison with the base extracts invariant IDs, the pin and §6.1
  rows line by line rather than with the strict table parser, because an older base may not satisfy today's format.
  The format of the current files stays enforced by R3.
- **`PATH_CAPABILITIES.md`** has an extra table "Capabilities without a path" for capabilities no §7 row uses
  (`autoupdate.policy_set`), so every catalog capability is accounted for.
- **§7 rows added.** Five paths that existed in code but in no §7 row (M-48, M-49, S-16, U-37, U-38) are added; §7 now
  counts 110 rows.
- **`ReleaseGateBaseTests`** passes without comparing when `WHISKERS_GATE_BASE` is unset; the comparison runs in the CI
  step that sets it.

## Amendment 2 — gate review rounds 1 to 4 (6 November 2026)

This amendment takes precedence over the text above, Amendment 1 included. It is **pending acceptance** by the
maintainer.

- **Bootstrap order and missing files (refines Amendment 1).** The base's csproj is read first and decides the
  bootstrap. A base without a csproj, or with an unreadable version, is always a finding. A base below 1.0.0 that lacks
  `SECURITY_INVARIANTS.md`, `PRODUCT_CONTRACT.md` or `RELEASE_GATE.md` counts as empty for that file. Correction to
  Amendment 1's example: neither v0.14.1 nor v0.15.0 has `docs/v1/`; v0.15.0 is the latest published release, so it is
  the base of the next 0.x release after 0.15.0.
- **Strict base tables (refines Amendment 1's "line by line").** Only the invariant IDs of the base's
  `SECURITY_INVARIANTS.md` are read line by line, because main's INV-24 row had an unescaped `|`. The base's
  `RELEASE_GATE.md` (pin and retired IDs) and its §6.1 table are read with the strict parsers. A problem in them is a
  finding, because a malformed table would hide retired IDs or accepted decisions. Only a file or table that is absent
  from a base below 1.0.0 counts as empty.
- **The version never goes down against the base (D2).** A current `<Version>` below the base's is a finding. This
  applies to the pull request base and to the previous release.
- **The previous release is a published release in the release commit's history (R18, D16).** In a release run the
  base is the highest tag strictly below `<Version>` that is a published GitHub release, not a draft and not a
  pre-release. The base step fails when any of these holds:
  - the list of published releases is missing, or no such release exists;
  - the base release's commit is not an ancestor of the release commit;
  - the base release's commit does not carry the tag's version in its csproj;
  - a `vX.Y.Z` tag between the base and `<Version>` is an ancestor of the release commit, carries its own version in
    its csproj and has no published release (the base would otherwise fall back past it, for example after a release
    was deleted or demoted). A failed release's tag must therefore be finished or deleted before the next release.

  The guarantee is the ancestry check together with the version match. The published list only screens out tags whose
  release run failed: anyone who can push a tag can also publish a release by hand. A tag on a commit of another
  version is refused instead of being read as a 0.x bootstrap base. Only in a release run, the CI step fetches the whole
  history and the tags (`--unshallow`) and lists the published releases (`gh release list`). The run's own token is
  passed to `gh` alone and unset before the tests run, so it is not in the tests' environment. This does not put it out
  of their reach: the checkout's persisted credentials hold the same read-only (`contents: read`) token. In that step no
  other environment value and no line of the script names `github.token` or a secret (`secrets.`).
- **Reading the base fails closed.** A file counts as absent from the base only when `git ls-tree` lists no such path at
  the base commit; any failure of `git ls-tree` or `git show` (a missing object after a shallow fetch, a corrupt object,
  a bad reference) is a finding, never an empty base. A current commit without a csproj is a finding too, and so is a
  tag in the fallback range above whose csproj exists but whose version cannot be read.
- **A comparing step that lost its base fails.** The CI step exports `WHISKERS_GATE_BASE_REQUIRED=true`. With it set
  and `WHISKERS_GATE_BASE` empty, `ReleaseGateBaseTests` fails instead of passing without a comparison.
- **R10 covers step conditions.**
  - A mandatory step with an `if:` is a finding.
  - The one exception is the base-comparison step with exactly `github.event_name == 'pull_request' ||
    inputs.release-gate == 'release'`.
  - For a mandatory-suite row that names only a job, any step `if:` in that job is a finding.
- **The gate is a required test.** These entries are in `.github/required-tests/in-process.txt`, each class with its
  discovered case count, and `RequiredTestsManifestTests` pins them:
  - `ReleaseGateTests.The_repository_passes_the_gate_in_its_mode`;
  - the classes `ReleaseGateTests`, `InvariantTableParserTests`, `PathMatrixParserTests` and `ReleaseGateDocumentTests`;
  - `ReleaseGateBaseTests.The_records_keep_every_id_and_decision_of_the_base`.
- **Table hygiene.** A table ends at its first line that does not start with `|`. Every `|` line after that, up to the
  next heading (`#` to `######` followed by a space; lines inside code fences are neither headings nor rows), is a parse
  problem rather than a dropped row. The one exception is a further table with the same header
  (header row plus separator) where the reader takes every such table (the invariant tables); under a heading that
  holds one table, a second one is a problem too. A §11 count row given twice is an R3 finding.
  - Code fences follow CommonMark: a fence opens with three or more backticks or tildes and closes only with the same
    character, at least as many, and nothing else on the line. Fenced lines are skipped everywhere a table is looked
    for, including the search for the first table under a heading, so a fenced example table is never the table.
  - R3 also reads `SECURITY_INVARIANTS.md` line by line: every line outside a fence that starts like an invariant row
    (`| INV-nn |`) but is not a parsed row is a finding ("an invariant row the gate cannot read"), so a table whose
    header the parser does not recognise cannot hide its rows.
- **Patch releases come from `main`.** The tag-on-main check (D16) refuses a tag cut on a `release/*` branch. Land the
  fix on `main` and tag it there.
