# CI and release checks

What every push, pull request and release of Whiskers has to pass, what each check proves, and which controls are not in
the repository but in its GitHub settings (R1-47; TA-25, TA-47, TA-50; audit A-18, F-09, F-13).

Every check **fails closed**: a check that cannot run (no Docker, no database, no vulnerability data, no SARIF, an image
that cannot be pulled) fails its job. No job runs longer than its `timeout-minutes`; every action is pinned to a full
commit SHA and Dependabot keeps the pins current. `ReleaseWorkflowTests` reads the workflow files and fails when one of the
properties below is removed or weakened (each property has negative tests that mutate the real file).

## Workflows

### `ci.yml` — every push and pull request against `main`, and inside every release

| Job / step | What it proves | Fails when |
|---|---|---|
| `build-test` → Test (in-process) | The whole in-process suite, including `EndpointAuthMatrixTests`, the boot matrix and the workflow policy tests | any test fails, fewer than the floor of tests passed (`check-test-results.sh`, the floor is in the step; raise it when the suite grows), or a test named in `.github/required-tests/in-process.txt` is missing or did not pass |
| `build-test` → Test (Docker integration, mandatory) | PostgreSQL metrics (`PostgresSmokeTests`), local accounts on PostgreSQL (`PostgresIdentityTests`), PostgreSQL backup and restore on 17 and 14 — against real servers (Testcontainers) | no Docker daemon, a server that does not start (the tests throw on CI, `DockerRequirement`), any failed **or skipped** test, fewer than the floor, or a test named in `.github/required-tests/docker.txt` is missing or did not pass |
| `build-test` → Release gate (compare with the base) | Pull requests and release runs only (R1-51, contract 0008, D16/R18): no invariant ID disappears without a retirement, the pinned highest ID never goes down, an accepted §6.1 release decision is never edited or deleted — against the pull request's base commit, or in a release run the previous release tag (`ReleaseGateBaseTests`) | a finding, an unreadable base, or the test skipped or missing from `gate-base.trx` |
| `build-test` → DI boot gate | The app boots in Development (`ValidateOnBuild`) until `/healthz` answers | the app exits or never becomes healthy |
| `artifact` | The image built from this commit (amd64): Trivy (CRITICAL fails, exceptions only in `.trivyignore`), the hardened compose profile, the upgrade from the previous published release and the way back | see [Artifact checks](#artifact-checks) |

**Required tests.** A floor of passed tests alone lets one mandatory test vanish — an attribute removed, a class renamed —
while the rest of the suite still clears it. The manifests in `.github/required-tests/` therefore name the tests each run
must contain and pass (`<minimum> <name>`: a test, every case of a theory, or a whole class); `check-test-results.sh
--require` fails on any entry that is missing, skipped or failed, whatever the floor says. `RequiredTestsManifestTests`
keeps the manifests in step with the test assembly: every entry must name tests of its run, every container test must be
required in the Docker run, and the endpoint matrix and the PostgreSQL Identity and metrics tests are named one by one.
A new mandatory test goes into the manifest in the same change. Each manifest ends in a block between
`# BEGIN release-gate (generated)` and `# END release-gate`: the tests the ✅ invariants name, rendered by
`ReleaseGateTests` (R1-51); do not edit it by hand — on a difference the test writes the fresh block to the manifest's
`.actual` file. The `image-runtime` job is checked the same way (`image-runtime.trx`, `--no-skips`,
`.github/required-tests/image-runtime.txt`).

**Release gate (R1-51, contract 0008).** `ReleaseGateTests` runs inside the in-process tests and checks
`docs/v1/SECURITY_INVARIANTS.md`, `RELEASE_GATE.md`, `PRODUCT_CONTRACT.md` (§6.1, §7), `PATH_CAPABILITIES.md` and the
generated `CAPABILITY_INVENTORY.md` against the test assembly and these workflows. Its mode follows the version (below
1.0.0: Development); only release.yml's `release-gate: release` input, turned into `WHISKERS_RELEASE_GATE` for the
in-process step alone, makes a run a release run. The rules and the records are described in `docs/v1/RELEASE_GATE.md`.

In a release, `ci.yml` runs on the tag commit (`workflow_call`) and the `artifact` job is skipped: the release checks the
staged digest it publishes instead (`artifact-test`, below).

### `security.yml` — every push and pull request against `main`, weekly, and inside every release

| Job | What it checks | Fails when |
|---|---|---|
| `dependency-vulnerabilities` | `dotnet list package --vulnerable --include-transitive` over every project of the solution | any known-vulnerable package (any severity), the check reports problems, or fewer than 5 projects were checked (no complete data is retried up to three times; a finding never — see [External downloads](#external-downloads-retries-no-nuget-cache-the-committed-pgdg-key)) |
| `dependency-review` (pull requests) | the dependency changes of the pull request (NuGet, Actions, Docker) | the pull request adds a dependency with a known vulnerability (`fail-on-severity: low`) |
| `secret-scan` | gitleaks 8.30.1 (pinned by digest) over the whole history and the working tree, default rules + `.gitleaks.toml` | any finding outside the exceptions in `.gitleaks.toml`. Every exception names its rule, its file and the exact value (two identifiers in vendored bundles, two names the rules mistake for credentials, the test fixtures); no directory and no line pattern is exempt, so a real credential in a test, in a vendored bundle or next to an excepted value, or an excepted value in another file, fails |
| `static-analysis` | the .NET security analyzers (`AnalysisModeSecurity=All`) over the shipped projects, as errors | any security analyzer warning; a reviewed false positive is suppressed at its method with a justification |
| `codeql` | CodeQL for C# (buildless, `security-extended`), results in code scanning | the analysis cannot run; findings block a merge through the repository's code-scanning rule (see below) |

### `release.yml` — a `v*` tag

`prepare` (the tag commit must be on `origin/main`) → `test` (the whole `ci.yml` on the tag commit, release gate in
release mode) and `security` (the whole `security.yml` on the tag commit; `dependency-review` runs on pull requests only) → `build` (amd64 and arm64, staged by digest) → `scan` (Trivy on
both digests) and **`artifact-test`** (the staged amd64 digest: hardened profile, N−1 upgrade and recovery) → `publish-image`
(only after `test`, `security`, `scan` and `artifact-test`) → chart, GitHub release, moving tags. Nothing is tagged before the image
that will be published passed the artifact checks. The arm64 image is scanned but not started (the runners are amd64).

### `chart-ci.yml`

Lint and render invariants of the Helm chart on chart changes; the kind install smoke stays manual (`workflow_dispatch`).

## External downloads: retries, no NuGet cache, the committed PGDG key

nuget.org and postgresql.org have bad minutes. A job must survive a reset connection, but it must never retry its way past
a real failure, and it must never trade a check for speed. `BuildReproducibilityTests` holds each point below, with
negative tests that mutate the real files.

| What | How | Why |
|---|---|---|
| NuGet restore in the workflows | Every workflow restores with `bash .github/scripts/restore-locked.sh`: the locked restore of `Whiskers.slnx`, up to three tries 20 s apart. Each warning names the exit code. | A connection reset passes on the next try. NU1004/NU1005 (the lock file does not match) and NU1403 (a content hash differs) would fail the same way every time, so they fail **at once**, without retries: a supply-chain signal is not hidden behind network-sounding warnings. NU1101/NU1102 (the package or the locked version is gone from nuget.org) also fail at once, unless the same log shows a source failure (NU1301, NU1801): nuget.org can report a package as missing when the request for it failed, and that case is retried. The third failure fails the step. A restore step may not use `continue-on-error`, `set +e` (also combined, `set +eu`) or `set +o errexit`. |
| NuGet restore in the image | The Dockerfile's restore `RUN` (stage `build`) has the same loop, in POSIX `sh`. | `image-runtime` builds without a layer cache. `artifact` keeps its BuildKit layer cache (`type=gha`) for the runtime stage, but with `no-cache-filters: build`: the restore stage never comes from the cache, so it restores from nuget.org on every build. `cache-to` uses `mode=min`, so only the final image's layers are exported, not the never-read build stage. The release builds (`release.yml`) have no layer cache at all; a release always builds fresh. The policy recognises a release workflow by its file name, its `name:` or a tag trigger (`on.push.tags`). A reusable workflow that only a release calls is not recognised as one. A `run` step may not pass `--cache-from`/`--cache-to` to `docker build`/`docker buildx build` in any workflow. |
| **No NuGet cache** | Not in `setup-dotnet` (`cache:`), not through `actions/cache`, not through `NUGET_PACKAGES`, not as a BuildKit cache mount, and not as a cached restore layer: a `docker/build-push-action` with `cache-from` must name the restore stage in `no-cache-filters`, and a release build may have no `cache-from`/`cache-to`. | NuGet hashes a package and checks its signature only when it downloads and extracts it. For a package that is already in the global-packages folder, it trusts the recorded hash and checks nothing. A cache hit would make the cache entry a trust root that nothing re-checks, and would keep a package nuget.org has removed. A cached restore layer is such a cache: it holds the packages and the assets file, and BuildKit reuses it without running the restore. |
| Vulnerability data (`security.yml`) | `dotnet list package --vulnerable` is retried (three tries) only while it has no complete data: a non-zero exit, problems, or fewer than 5 projects. | The data comes live from nuget.org. Each try computes `FOUND`, the project count and the problem count anew, all three before any break, so no value from an earlier try survives. A jq failure (`jq -e`: also no output at all) is incomplete data (`status=1`), never "no finding", and its error stays in the log. A finding (`FOUND > 0`) ends the loop at once and fails the job (`exit 1`), so a finding is never retried. With no finding, the data is asserted complete again after the loop (exit 0, at least 5 projects, no problems). The step has no `exit 0`. |
| PostgreSQL (PGDG) repository key | Committed as `deploy/keys/pgdg-ACCC4CF8.asc`. CI and the Dockerfile use this file. Before apt trusts it they check the whole file's SHA-256 (`sha256sum -c`, `0144068502a1eddd2a0280ede10ef607d1ec592ce819940991203941564e8e76`) and the key's fingerprint `B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8`. The `sha256sum -c -` command stands on its own (nothing like `|| true` after it) and runs before `gpg --dearmor`. Nothing downloads a key from postgresql.org. The test checks the file's SHA-256, computes the fingerprint itself, and refuses a second public key inside the armor. | No download that can fail. The file is pinned byte for byte: any changed byte, or a second key appended inside the same armor block, fails the tests and the `sha256sum` check in CI and the image. When PGDG rotates its key, replace the file, the SHA-256 and the fingerprint together, after checking the new key out of band. |

## Artifact checks

`.github/scripts/artifact-checks.sh` runs against an image that is already built — the deliverable, not the source tree.
Every wait is bounded (`WAIT_SECONDS`, default 300 s — the image's own health-check start period).

- **`hardened <image>`** starts the image with the shipped `docker-compose.hardened.yml` (only the image and a secrets
  volume are added) and checks: healthy and ready; the container runs as `10001:10001` with a read-only root filesystem,
  all capabilities dropped and `no-new-privileges`; `/app` is not writable and the database lives on the data volume; the
  anonymous surface (sign-in page served, pages and the Admin download challenged, MCP refused, `/metrics` disabled); a
  local sign-in of the seeded administrator, the Admin-only download and the audit export; no restart.
- **`upgrade <image> <previous>`** proves the upgrade and the way back on the stored records, not on a log line or a
  sign-in alone. `artifact-data.mjs` (a pinned Node image with `node:sqlite`) reads and writes the stopped data volume
  independently of the app and reports password hashes, security stamps and the vault value only as SHA-256
  fingerprints; a failed comparison names the field and whether it is absent or differs, never a value. The previous release creates the data with bootstrap credentials **once**; a second local
  account, its role, a metrics row and a legacy vault entry are seeded, and from then on no start gets bootstrap
  credentials, so a lost account cannot be recreated from configuration. The previous release starts again, signs both
  accounts in and encrypts the vault entry itself. The image then upgrades it: a pre-upgrade backup is reported and on the
  volume, the log names a genuine release change, both migration histories are the previous ones plus exactly the reported
  pending migrations, every record (both accounts with id, password hash and security stamp, the role, the metrics value,
  the decrypted vault value) is unchanged, both accounts sign in. A restart of the same release must be a clean no-op (no
  upgrade, no backup, nothing changed). Every record is then changed (the account deleted — and refused at sign-in — the
  role, the metrics value, the vault value) before the documented way back
  ([backup-and-recovery.md](backup-and-recovery.md#downgrade)): the image stages the pre-upgrade backup with
  `--restore-backup latest-pre-upgrade --for-previous-release` and the previous release applies it. The recovered data
  must have the previous release's migration histories and every original value, and none of the audit rows written under
  the image. Finally the image upgrades the recovered data once more (a new pre-upgrade backup, the same histories as the
  first upgrade).
- **`previous-release [<version>]`** picks N−1 from the **published** GitHub releases (no drafts or pre-releases): the
  highest one **below** the version under test — the release version, or the `<Version>` of `Whiskers.csproj` on a pull
  request. N−1 is never the release under test: the same release without pending migrations is rightly no upgrade, and
  the check fails if it ever were.
- **`prepare <image-ref> <local-tag>`** pulls the release's staged amd64 digest and tags it for the checks (release
  `artifact-test`), with Docker's output — registry, daemon, credential helper — kept private like every other command's,
  and requires both image ids to be read as `sha256:` ids and to be equal. No step of an artifact job runs `docker` or
  `docker-compose` itself (also not through a path or in quotes), and the disclosure self-test is the first step of the
  checks script in both jobs; `ReleaseWorkflowTests` treats anything else as a violation.
- **`self-test`** checks the output rule of these checks: the CI log gets **only text the script writes itself** — step
  names, fixed diagnostics, the arguments it was given, counts. Every command's own stdout and stderr (Docker, compose,
  curl, the containers, the data tool) goes to private files in a temporary directory removed on exit; no text from a
  container, a tool, an HTTP body or a log line reaches the CI log, filtered or not. When a check fails, the logs of its
  containers are summarised as counts per log level (`entries: info=… warn=… …; other lines=…`); a failed readiness
  check names the HTTP status, not the body; the upgrade line and the backup id in the log are compared with what the
  script expects (a release version other than N−1's, plain counts that must match the migration histories, a backup
  that is on the volume) and never reprinted. The data tool reports a failure only by the operation and file it hit
  (`state failed while reading vault.json`), never by an exception's message or stack, and exits non-zero. The self-test
  plants random values — hex, letters only, a fingerprint and its 8-character prefix, a 32-digit number — as log
  categories, event ids, messages, an HTTP body, tool errors (a container that does not start, a failing file command, a
  data-tool run Docker refuses, a pull, a tag and an inspect of the staged image that fail against a stub `docker`, an inspect that answers no
  image id or two different ids) and upgrade log
  lines, drives the real code paths with them in child processes, and fails
  if any value, its SHA-256 fingerprint or an 8-character prefix of either appears, or if a probe does not end with its
  expected exit status and fixed diagnostic. On malformed data (invalid `vault.json` or `roles.json`, an
  `EncryptedValue` that is the planted value itself or of the wrong type, a corrupt `metrics.db`) every command of the
  data tool — `state`, `seed-vault-legacy`, `seed`, `mutate`, `audit-since`, `audit-present` — runs on its own fixture,
  which is complete except for the part under test, and must exit non-zero with exactly one fixed error that names the
  operation that reached the malformed part. Both the `artifact` job and the release's `artifact-test` run it before the
  other checks. What it proves is bounded by these probes: the output rule holds for every path the script has, but a
  new path is covered only once a probe drives it.

Run them locally against an image you built (`docker build -t whiskers:local .`):

```bash
bash .github/scripts/artifact-checks.sh self-test
bash .github/scripts/artifact-checks.sh hardened whiskers:local
bash .github/scripts/artifact-checks.sh upgrade whiskers:local ghcr.io/lupusmalusdeviant/whiskers:0.14.1   # a release below <Version>
```

The hardened profile uses host networking (`network_mode: host`); on Docker Desktop that needs host networking enabled.

## Running the Docker-backed tests locally

`dotnet test --filter Category=RequiresDocker` with Docker running. Without Docker they are skipped locally; with `CI`
set they fail instead. The PostgreSQL backup tests also need the PostgreSQL 18 client tools (`WHISKERS_PG_BIN_DIR`).
A new test class that starts a container must carry `[Trait("Category", DockerRequirement.Category)]` and go through
`DockerRequirement.SkipUnlessStarted` — `DockerRequirementTests` enforces it.

## Endpoint authorization matrix

`EndpointAuthMatrixTests` boots the real app (Production, authentication on) and enumerates every endpoint of its endpoint
data sources. Each must be classified in `EndpointAuthMatrixTests.Expected` (static assets by their descriptor); a new
endpoint fails the test until someone decides who may call it. Endpoints are merged only when proven equivalent — the
content-encoding variants of one static asset; two other endpoints on the same route and method (told apart by host or
other selectors) are classified and probed each on its own, keyed `METHODS route [display name]`. A handler run counts
only for the endpoint that ran (route, methods, order, display name and host), never for another one on the same route.
No endpoint is exempt from that proof: the static-asset fallback of the build manifest (`{**path:file}`) is probed with a
file the fixture makes exist for it alone, so an allowed caller must see its handler run, and a second app with only that
endpoint opened to anonymous callers must fail the matrix (`An_anonymous_static_fallback_fails_the_matrix`). Each endpoint is requested as anonymous, Viewer, Operator
and Admin; the test records whether the handler actually ran, so a refused call must never reach the handler and an
allowed one must. Pages are reachable for every signed-in role on purpose — their role checks run inside the handlers
(INV-17). Two recorded deviations belong to R1-50: the Google/OIDC challenge of a provider that is not configured answers
500, and an anonymous MCP call gets the cookie sign-in redirect rather than a bare 401.

## Repository-hosted controls

These protect the checks above but live in the GitHub settings, not in the repository; nothing in CI can prove them.
Review them when the repository or its owner changes.

| Control | Setting | Why |
|---|---|---|
| Branch protection / ruleset on `main` | Require a pull request; require the status checks `build-test`, `artifact` (ci) and `dependency-vulnerabilities`, `dependency-review`, `secret-scan`, `static-analysis`, `codeql` (security) to pass; require branches to be up to date; block force pushes and deletion | Without required checks a red job does not stop a merge |
| Tag protection (ruleset on `v*`) | Only maintainers create, update or delete release tags | A tag starts a release with GHCR push rights and the cosign identity |
| Code-scanning merge protection | Ruleset rule "Require code scanning results" for CodeQL, alerts of severity *High* or higher (security) block | CodeQL results gate merges here, not in the workflow |
| Dependabot alerts and security updates | Enabled | Advisories for dependencies already on `main` (the weekly `security.yml` run also re-checks them) |
| Dependabot version updates | `.github/dependabot.yml` (NuGet, Docker, Actions) | Keeps pins and packages current |
| Dependency graph | Enabled | Required by `dependency-review` (the job fails without it) |
| Secret scanning and push protection | Enabled | GitHub's own detection, before a secret reaches the history; `secret-scan` is the in-repository check |
| Actions permissions | Default `GITHUB_TOKEN` read-only; "Allow GitHub Actions to create and approve pull requests" off; approval required for workflows from outside collaborators | The workflows declare least privilege per job; the default must not widen it |
| GHCR package | The `whiskers` package public and linked to the repository; only the release workflow pushes | N−1 checks and installs pull it anonymously |
