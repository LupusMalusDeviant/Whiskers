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
| `build-test` → Test (in-process) | The whole in-process suite, including `EndpointAuthMatrixTests`, the boot matrix and the workflow policy tests | any test fails, or fewer than the floor of tests passed (`check-test-results.sh`, the floor is in the step; raise it when the suite grows) |
| `build-test` → Test (Docker integration, mandatory) | PostgreSQL metrics (`PostgresSmokeTests`), local accounts on PostgreSQL (`PostgresIdentityTests`), PostgreSQL backup and restore on 17 and 14 — against real servers (Testcontainers) | no Docker daemon, a server that does not start (the tests throw on CI, `DockerRequirement`), any failed **or skipped** test, or fewer than the floor |
| `build-test` → DI boot gate | The app boots in Development (`ValidateOnBuild`) until `/healthz` answers | the app exits or never becomes healthy |
| `artifact` | The image built from this commit (amd64): Trivy (CRITICAL fails, exceptions only in `.trivyignore`), the hardened compose profile, the upgrade from the previous published release and the way back | see [Artifact checks](#artifact-checks) |

In a release, `ci.yml` runs on the tag commit (`workflow_call`) and the `artifact` job is skipped: the release checks the
staged digest it publishes instead (`artifact-test`, below).

### `security.yml` — every push and pull request against `main`, and weekly

| Job | What it checks | Fails when |
|---|---|---|
| `dependency-vulnerabilities` | `dotnet list package --vulnerable --include-transitive` over every project of the solution | any known-vulnerable package (any severity), the check reports problems, or fewer than 5 projects were checked |
| `dependency-review` (pull requests) | the dependency changes of the pull request (NuGet, Actions, Docker) | the pull request adds a dependency with a known vulnerability (`fail-on-severity: low`) |
| `secret-scan` | gitleaks 8.30.1 (pinned by digest) over the whole history and the working tree, default rules + `.gitleaks.toml` | any finding outside the narrow, reasoned exceptions in `.gitleaks.toml` |
| `static-analysis` | the .NET security analyzers (`AnalysisModeSecurity=All`) over the shipped projects, as errors | any security analyzer warning; a reviewed false positive is suppressed at its method with a justification |
| `codeql` | CodeQL for C# (buildless, `security-extended`), results in code scanning | the analysis cannot run; findings block a merge through the repository's code-scanning rule (see below) |

### `release.yml` — a `v*` tag

`prepare` → `test` (the whole `ci.yml` on the tag commit) → `build` (amd64 and arm64, staged by digest) → `scan` (Trivy on
both digests) and **`artifact-test`** (the staged amd64 digest: hardened profile, N−1 upgrade and recovery) → `publish-image`
(only after `test`, `scan` and `artifact-test`) → chart, GitHub release, moving tags. Nothing is tagged before the image
that will be published passed the artifact checks. The arm64 image is scanned but not started (the runners are amd64).

### `chart-ci.yml`

Lint and render invariants of the Helm chart on chart changes; the kind install smoke stays manual (`workflow_dispatch`).

## Artifact checks

`.github/scripts/artifact-checks.sh` runs against an image that is already built — the deliverable, not the source tree.
Every wait is bounded (`WAIT_SECONDS`, default 300 s — the image's own health-check start period).

- **`hardened <image>`** starts the image with the shipped `docker-compose.hardened.yml` (only the image and a secrets
  volume are added) and checks: healthy and ready; the container runs as `10001:10001` with a read-only root filesystem,
  all capabilities dropped and `no-new-privileges`; `/app` is not writable and the database lives on the data volume; the
  anonymous surface (sign-in page served, pages and the Admin download challenged, MCP refused, `/metrics` disabled); a
  local sign-in of the seeded administrator, the Admin-only download and the audit export; no restart.
- **`upgrade <image> <previous>`** creates data with the previous published release (seeded administrator, vault key,
  sign-in), starts the image on it (a pre-upgrade backup must be reported and present, `/readyz` Healthy, the same
  administrator signs in, Admin download and audit export work), then goes back the documented way
  ([backup-and-recovery.md](backup-and-recovery.md#downgrade)): the image stages the pre-upgrade backup with
  `--restore-backup latest-pre-upgrade --for-previous-release`, the previous release starts and applies it, the
  administrator signs in, and the image upgrades that data once more — which must take a new pre-upgrade backup, proving
  the data was back on the previous release's schema.
- **`previous-release [<version>]`** picks N−1 from the **published** GitHub releases (no drafts or pre-releases): the
  highest one below the version being released, or the highest one for a pull request.

Run them locally against an image you built (`docker build -t whiskers:local .`):

```bash
bash .github/scripts/artifact-checks.sh hardened whiskers:local
bash .github/scripts/artifact-checks.sh upgrade whiskers:local ghcr.io/lupusmalusdeviant/whiskers:0.15.0
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
endpoint fails the test until someone decides who may call it. Each endpoint is requested as anonymous, Viewer, Operator
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
