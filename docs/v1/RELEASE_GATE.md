# Release gate

The page a maintainer reads before tagging a release. The tables below are read by `ReleaseGateTests`
(`src/Whiskers.Tests/ReleaseGate/`, [contract 0008](../contracts/0008-release-gate.md)), which fails the in-process test
run in CI when the records disagree with the code, the tests or each other. The gate proves that the records are
consistent and complete and that every named test exists, runs and passes; it does not prove that an invariant holds —
that is what the named tests are for.

## What the gate checks

- **Invariant status** ([`SECURITY_INVARIANTS.md`](SECURITY_INVARIANTS.md)). Below 1.0.0 (*Development* mode) every
  invariant that is not ✅ is listed under *Open invariants* with its true status and package, and a listed ✅ fails as
  stale (R2). From 1.0.0 on (*Line* mode, every pull request and `main` push) each such row also needs an advisory or
  issue reference unless an accepted exception covers it. A release run of 1.0.0 or later (*Release* mode, only
  `release.yml` passes `release-gate: release`) needs every invariant ✅, a ⚠️ with an accepted exception, or a ❌/❓ with
  a known-violation record for exactly that patch release (R1, R17). The status cell is one bare symbol, §11's counts
  equal the table's, every ID up to the pin below is a row or retired (R3).
- **Named tests** (R4–R9). Every backticked name in the test column resolves to a test; a planned entry names the test
  that is still owed; no named test is statically skipped; every test named by a ✅ invariant or an accepted exception
  is required method by method, with the case count xUnit discovers, in the generated block of its CI run's manifest
  under `.github/required-tests/`, which `check-test-results.sh --require` proves from the TRX file — a skip fails.
- **Mandatory suites** (R10, R16). The jobs below exist, cannot fail silently and are needed by `publish-image`.
- **Decisions** (R11, R17, R18). Exceptions, retirements and known violations rest on an accepted, dated decision in
  [`PRODUCT_CONTRACT.md` §6.1](PRODUCT_CONTRACT.md#61-release-decisions); an accepted decision never changes (checked
  against the pull request's base and, in a release run, against the previous release tag).
- **Capabilities and paths** (R12–R15). [`CAPABILITY_INVENTORY.md`](CAPABILITY_INVENTORY.md) is generated from the
  capability table; every capability is named by a §7 row ([`PATH_CAPABILITIES.md`](PATH_CAPABILITIES.md)), every row
  names real tools and capabilities, and §7.6 counts the rows.

Locally, `WHISKERS_RELEASE_GATE=dry-run dotnet test --filter ReleaseGateTests` shows what a 1.0 release would refuse
today.

Highest invariant ID: INV-62

## Open invariants

| Invariant | Status | Package | Why it is open | Reference |
|---|---|---|---|---|
| INV-01 | ⚠️ | R1-03 | webhook and system principals come with their packages; humans are keyed by the account handle, not the credential | — |
| INV-25 | ⚠️ | R1-02 | with neither policy file present the restrictive default is created, so deleting both looks like a first start | — |
| INV-52 | ⚠️ | WP7 | SSH private keys and mTLS client keys are stored in plaintext; no in-place `VAULT_KEY` rotation | — |
| INV-56 | ⚠️ | WP7 | readiness lines are German only, and no test compares README/SECURITY.md claims with the code | — |
| INV-57 | ⚠️ | WP2 | UI and automation delegates, webhooks, workloads, volumes and log alerts do not act on the resolved target object | — |
| INV-58 | ⚠️ | R1-44 | holds per host only after the migration; hosts on the shared client credential are exposed (D12) | — |
| INV-62 | ⚠️ | 0006 | the Deploy page's compose mode and `deploy_app` are out of scope; a re-tag before `up`, Traefik `exposedByDefault`, Tailscale sidecar `serve` and cloudflared are caught only by the re-check or not at all | — |

## Retired invariants

| Invariant | Decision | Date |
|---|---|---|

## Known violations

| Invariant | Status | Decision | Advisory | Limitation | Release | Review by |
|---|---|---|---|---|---|---|

## Accepted exceptions

| Invariant | Status shipped | Decision | Limitation | Review by |
|---|---|---|---|---|

## Platform skips

A named test method that skips on a platform. *Runs unskipped in* names the mandatory suite that proves it
(`<workflow> <job>[ / <step>]`), or — when no CI job can run it: then it is not evidence, and its invariant needs another
proven test.

| Test | Skips when | Runs unskipped in | Why |
|---|---|---|---|
| Whiskers.Tests.PerHostCredentialTests.On_windows_a_key_readable_by_everyone_is_exposed | not Windows | — (no Windows CI job) | Windows ACL check |

## Mandatory suites

| Workflow | Job | Step (optional) | Proves |
|---|---|---|---|
| ci.yml | build-test | Test (in-process) | the in-process suite: no failure, a floor of passed tests, the required tests of `in-process.txt` (generated block included) |
| ci.yml | build-test | Test (Docker integration, mandatory) | the PostgreSQL and other Docker-backed suites: a floor, no skip, the required tests of `docker.txt` |
| ci.yml | build-test | Release gate (compare with the base) | no invariant ID or accepted decision lost against the pull request's base or the previous release tag (D16, R18) |
| ci.yml | build-test | DI boot gate | the application boots with its dependency graph validated |
| ci.yml | image-runtime | Image runtime tests | the built image's runtime tools, on amd64 and arm64: a floor, no skip, the required tests of `image-runtime.txt` (D14) |
| release.yml | scan | | Trivy gate on both staged digests |
| release.yml | artifact-test | | the staged image: hardened profile, N-1 upgrade and recovery |
| security.yml | dependency-vulnerabilities | | no known-vulnerable package (D8) |
| security.yml | secret-scan | | no committed secret in the history or the tree (D8) |
| security.yml | static-analysis | | the security analyzers, warnings as errors (D8) |
| security.yml | codeql | | CodeQL security queries run (D8) |
