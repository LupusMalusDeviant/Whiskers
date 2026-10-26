# Whiskers audit — Operations, Supply Chain, CI/CD, Tests, Reliability, Observability, Install

Scope: read-only review of repository @ `498e70d` (release 0.15.0). Audited areas: Dockerfile, entrypoint.sh, both compose profiles, deploy/** (install.sh, Helm chart, telemetry, postgres overlay, tailnet-guard), `.github/workflows/*`, dependabot, `src/Whiskers.Tests/**`, every BackgroundService/IHostedService, health checks, /metrics, logging, notifications, AI providers, HttpClient usage, Trivy, webhooks/GitDeploy, scheduler, autopatch, and README/docs.
All evidence was checked by reading the files. Where a claim depends on something outside the repo (an external incident, a GitHub setting), it says so.

## Summary of findings

| ID | Title | Sev | Category | v1-blocker |
|---|---|---|---|---|
| F-01 | Fleet-wide Trivy runs `aquasec/trivy:latest` (unpinned) with docker.sock mounted on every managed host | High | Security (supply chain) | yes |
| F-02 | First-run setup wizard can be claimed by anyone (no one-time setup token); installer/Helm make it reachable | High | Security / Operations | yes |
| F-03 | GitHub Actions pinned by mutable tags (incl. `trivy-action`, `sbom-action@v0`, `cosign-installer@v3`), not SHAs | High | Security (supply chain) | yes |
| F-04 | Release `workflow_dispatch` builds the dispatched ref (not the tag), pushes `X.Y.Z`/`X.Y` + Helm chart; release not gated on CI | High | Operations / Supply chain | yes |
| F-05 | Postgres deployments: self-backup excludes the database; backups stay on the same volume | High | Reliability (DR) | yes |
| F-06 | Webhook / git-deploy: no per-target serialization, no replay protection, long work done synchronously in the HTTP request | Medium | Reliability / Security | no (recommended) |
| F-07 | Unattended AI-trigger runs have no deadline; LLM streams have no body timeout; slots can leak permanently | Medium | Reliability | no |
| F-08 | Notification retry is mostly dead code (providers swallow their own errors); SMTP send has no effective timeout; no durable outbox | Medium | Reliability | no |
| F-09 | CI has no SAST/CodeQL, dependency-vuln or secret scanning, no image build/scan on PRs, no job timeouts | Medium | Security / Testing | no (strongly recommended) |
| F-10 | Runtime image: base tags not digest-pinned; ships full `docker.io` (dockerd/containerd/runc) + sshpass; root by default | Medium | Security (supply chain) | no |
| F-11 | Installer's defaults: `:latest`, privileged full profile, no VAULT_KEY, no signature check, outdated "next steps" (recommends Auth__Disabled) | Medium | Operations / UX | yes (text fix only) |
| F-12 | Schema migrations run before the app listens, with no automatic pre-migration backup; Helm startupProbe allows about 60 s | Medium | Reliability | no |
| F-13 | Test gaps: no migration-drift test, Postgres tests skip silently, only 3 UI tests, no E2E/image/upgrade/hardened-boot tests | Medium | Testing | no |
| F-14 | Hardened profile: socket-proxy with `POST=1` published on host loopback `127.0.0.1:2375` (unauthenticated, root-equivalent for any local user) | Medium | Security | no |
| F-15 | Background loops: non-shutdown `OperationCanceledException` escapes the catch filters; `HostOptions.BackgroundServiceExceptionBehavior` stays at StopHost; some intervals are not validated | Medium | Reliability | no |
| F-16 | AutoUpdate abandons an in-flight recreate on shutdown; scheduler tasks run fire-and-forget with `CancellationToken.None` | Low | Reliability | no |
| F-17 | Observability: no structured/JSON logs, no request correlation IDs, no OpenTelemetry/tracing | Low | Operations | no |
| F-18 | Docs gaps: no upgrade/rollback guide, troubleshooting, production checklist, or Postgres backup guidance | Medium | Documentation | yes (minimum upgrade + backup docs) |
| F-19 | Dependabot misses 3 of 5 projects and the compose/Helm images; no NuGet lock files; no global.json SDK pin | Low | Supply chain | no |
| F-20 | Helm: tailscale sidecar `:latest` + emptyDir state; VPN_PROVIDER unset (legacy tailscaled attempted as non-root); NOTES only in German | Low | Operations | no |
| F-21 | Tailscale auth key passed on the `tailscale up` command line under `pid: host` (visible to host `ps`) | Low | Security | no |
| F-22 | AiChat legacy client: no CancellationToken, 100 s default timeout, typed client captured in a singleton | Low | Reliability | no |
| F-23 | Release Trivy gate scans amd64 only, CRITICAL only, `ignore-unfixed` | Low | Supply chain | no |

---

## Findings (detail)

### F-01 — Unpinned `aquasec/trivy:latest` executed with docker.sock on every managed host — High, Security/Supply chain, v1-blocker: yes
**Evidence:** `src/Whiskers/Configuration/CveMonitorSettings.cs:23` (`TrivyImage = "aquasec/trivy:latest"`). `src/Whiskers/Services/Cve/TrivyScanner.cs:79-85` runs `docker run --rm -v <cache> -v /var/run/docker.sock:/var/run/docker.sock {trivyImage} image ...` through the host executor, which is nsenter on the local host and SSH on remote hosts.
**Impact:** every CVE cycle pulls whatever `latest` resolves to and runs it with root-equivalent Docker access on every fleet host. A compromised or hijacked upstream tag becomes fleet-wide root. Trivy's distribution was the subject of a publicly reported tag/image compromise in March 2026; I could not verify the details from inside the repo, so verify them, but the risk class is real either way. This is a security tool, so it carries a higher bar.
**Recommendation:** pin the default to `aquasec/trivy:<version>@sha256:<digest>`, refuse `latest` unless an explicit override is set, let Dependabot/Renovate bump the pin, and document how to verify it. Optionally offer `--image-src remote` without the socket where possible.

### F-02 — First-run admin claim has no setup token — High, Security/Operations, v1-blocker: yes
**Evidence:** `src/Whiskers/Services/Setup/SetupStateService.cs:56-66`. The first caller of `CompleteSetupAsync` becomes Admin, with nothing but a race lock. No one-time token is printed to the logs or required. Exposure paths: `deploy/install.sh:37-38` (`--bind 0.0.0.0` allowed), Helm `ingress.enabled` (`deploy/helm/whiskers/values.yaml:54-65`), and README `kubectl port-forward` → wizard.
**Impact:** on the full profile (privileged, `pid: host`, docker.sock) whoever reaches a freshly started instance first gets root on the host and the whole fleet. This is a realistic risk when the installer is run on a VPS with a public bind or an ingress is enabled before setup.
**Recommendation:** generate a one-time setup token at first boot, log it and write it to `/app/data/setup-token`, and require it in the wizard (like Jenkins/Grafana/Portainer). Alternatively restrict the wizard to loopback/trusted CIDRs until it is completed. Test the gate in `SetupWizardBootTests`.

### F-03 — Actions pinned by tag, not commit SHA — High, Supply chain, v1-blocker: yes
**Evidence:** `.github/workflows/release.yml:30,50,52,55,63,74,86,104,112,140` (`actions/checkout@v4`, `docker/*@v4/v7`, `aquasecurity/trivy-action@v0.36.0`, `sigstore/cosign-installer@v3`, `anchore/sbom-action@v0`, `softprops/action-gh-release@v2`). `ci.yml:20,23` and `chart-ci.yml:203,249,252` are also tag-pinned. The release job holds `contents: write`, `packages: write` and `id-token: write` at workflow scope (`release.yml:16-19`), so the helm job gets them too.
**Impact:** a moved tag runs attacker code with GHCR push rights and an OIDC identity that cosign signs with. The resulting image would be validly signed, which defeats the README's verification story. `trivy-action` specifically had its tags force-pushed in a publicly reported 2026 incident (external fact, verify).
**Recommendation:** pin every action to a full commit SHA with a version comment (Dependabot keeps SHA pins updated). Move permissions to job level (helm job: `packages: write` only). Add `step-security/harden-runner` or similar if desired.

### F-04 — Release pipeline integrity — High, Operations/Supply chain, v1-blocker: yes
**Evidence:** `.github/workflows/release.yml:10-14,29-30`. A `workflow_dispatch` takes a free-text `tag`, but `actions/checkout@v4` checks out the dispatched ref (default branch HEAD), not that tag. The run then pushes `${ver}` and `${minor}` (`:91-93`, only `latest` is suppressed), signs, and the `helm` job (`:156-182`) pushes the chart with no `if: github.event_name == 'push'` guard. There is no `needs:` on a test job, so a tag push publishes without running the test suite. `${{ github.event.inputs.tag }}` is interpolated straight into a shell script (`:38`); the injection risk is limited to users with write access.
**Impact:** a manual run can overwrite an already-released version tag (e.g. `0.15.0`) and its chart with different contents built from `main`, and the result is cosign-signed. Untested commits can be released.
**Recommendation:** check out `ref: ${{ inputs.tag }}` and assert it equals an existing tag. Refuse to overwrite existing version tags (check the registry first) or push dispatch builds to `:dev-<sha>` only. Gate the helm push on tag push. Add a test job (or reuse ci.yml via `workflow_call`) as `needs:`. Pass inputs via `env:`.

### F-05 — Self-backup omits the Postgres database; backups are co-located — High, Reliability/DR, v1-blocker: yes
**Evidence:** `src/Whiskers/Services/Backup/BackupService.cs:76-96`. The DB is only captured for SQLite (`VACUUM INTO`). For Postgres the manifest records provider and migrations but no data. Backups go to `DataPathOptions.SelfBackupsDir` = `/app/data/backups/self` (`Configuration/DataPathOptions.cs:101`), the same volume as the data. Postgres backup is not documented anywhere (`grep pg_dump` hits only borg-fleet scripts).
**Impact:** Postgres users (the recommended path for bigger fleets) who rely on "Self-backup & restore" lose users/Identity, audit log, webhooks, schedules, update policies and history on DB loss. A restore brings back files that reference a DB state that no longer exists. Losing the volume also loses every backup.
**Recommendation:** for Postgres, either run `pg_dump` into the archive (or refuse and warn loudly in the UI and in the backup manifest), or document the operator's responsibility clearly. Add an off-volume target (S3/SFTP/borg) or at least a documented `docker cp`/volume-export recipe. Add a restore drill test for both providers.

### F-06 — Webhook deploy concurrency, replay, synchronous execution — Medium, Reliability/Security, v1-blocker: no
**Evidence:** `src/Whiskers/Startup/WhiskersPipelineExtensions.cs:293-300` reads the full body unbounded (Kestrel 30 MB default) and awaits `TriggerAsync` without `RequestAborted`. `src/Whiskers/Services/Webhooks/WebhookService.cs:87-153`: the HMAC covers only the body (no timestamp/nonce or delivery-ID dedupe). Actions run inline, e.g. `DeployCompose` takes up to 5 min (`:182-195`). `TriggerCount++` is a read-modify-write (lost updates). `src/Whiskers/Services/GitDeploy/GitDeployService.cs:132-157`: `_lock` guards only the JSON store; `DeployAsync` has no per-app lock. There is no ASP.NET rate limiter (`grep AddRateLimiter` finds nothing).
**Impact:** two pushes in quick succession, or a CI retry, run concurrent `git fetch/reset` + `compose up` in the same directory, with possible corrupted checkouts or interleaved deploys. A captured signed request can be replayed forever to keep restarting or redeploying. Callers time out (GitHub after 10 s) while the deploy keeps running, which invites retries.
**Recommendation:** use a per-target `SemaphoreSlim` or a queue with coalescing ("latest wins"). Return `202 Accepted` with a run ID and execute in the background. Dedupe on `X-GitHub-Delivery` / a timestamp header with a tolerance window. Add a body size limit and a per-webhook rate limit.

### F-07 — AI trigger runs and agent streams have no deadline — Medium, Reliability, v1-blocker: no
**Evidence:** `src/Whiskers/Services/Agent/Triggers/AiTriggerDispatcher.cs:91` (`_concurrency.WaitAsync(0)`, so runs are skipped when the gate is full) and `:115` (`session.SendAsync(...)` with no token/timeout). `Services/Agent/Providers/AgentProviderFactory.cs:29` uses named client `"agent"`, which is never configured (`Startup/WhiskersHostingExtensions.cs:283` is a plain `AddHttpClient()`), so the timeout is the 100 s default. `AnthropicProvider.cs:39` uses `ResponseHeadersRead`, so after the headers arrive, a stalled stream body has no timeout at all.
**Impact:** a hung provider stream holds a trigger slot forever. Once `MaxConcurrentRuns` slots leak, every later AI trigger is silently skipped with a warning, so the autonomous remediation feature stops until restart.
**Recommendation:** wrap each trigger run in `CancellationTokenSource.CancelAfter` (e.g. 5 min) and thread the token through `SendAsync`/provider calls. Configure the `"agent"` client (timeout + idle-read timeout per chunk). Emit a self-metric for trigger runs and timeouts.

### F-08 — Notification failure handling — Medium, Reliability, v1-blocker: no
**Evidence:** `Services/Notifications/CompositeNotificationService.cs:113-120` retries via `NotificationRetry.TrySendAsync` (`NotificationRetry.cs:10-26`). But the providers catch and log their own exceptions (`DiscordNotificationService.cs:35`, Slack `:35`, Ntfy `:35`, Telegram `:34`, Webhook `:38`, Email `:34`, Mattermost `:52`, Matrix `:46`), so the retry never triggers for them. `EmailNotificationService.cs:55-60` uses `SmtpClient.SendMailAsync` with no token, and `SmtpClient.Timeout` does not apply to async sends. `ContainerHealthMonitor` awaits `_notifications.SendAsync` inline (`ContainerHealthMonitor.cs` ~l.128).
**Impact:** transient provider failures drop alerts with no retry (the retry exists only on paper). A blackholed SMTP server can stall the health-monitor cycle indefinitely; ScanSupervisor would flag the stall but alerts stop. Nothing is persisted for redelivery, so alerts are lost across restarts and outages.
**Recommendation:** have providers rethrow (or return a result) so the composite retries with backoff. Use a timeout token for SMTP (or MailKit). Dispatch external channels via a bounded `Channel<T>` worker so monitoring loops never block on delivery. Consider a small persisted outbox for critical events. Add a test where a provider throws once and then succeeds.

### F-09 — CI lacks security scanning and image validation — Medium, Security/Testing, v1-blocker: no (strongly recommended)
**Evidence:** `.github/workflows/` contains only `ci.yml`, `chart-ci.yml`, `release.yml`. There is no CodeQL/Roslyn security analyzers (no `TreatWarningsAsErrors`/`AnalysisLevel` in any csproj), no `dotnet list package --vulnerable`, no dependency-review, no gitleaks/trufflehog, and no Docker build or Trivy on PRs (the image is first built at tag time). No `timeout-minutes` (default 6 h) and no `concurrency:` group. Whether GitHub-native secret scanning/push protection is enabled could not be determined from the repo.
**Impact:** Dockerfile breakage and new Critical CVEs are found only at release (and fail it). Vulnerable NuGet versions and committed secrets are not caught at PR time.
**Recommendation:** add CodeQL (csharp), `dotnet list package --vulnerable --include-transitive` as a failing step, `actions/dependency-review-action`, and gitleaks. Add a PR job that builds the image, runs Trivy (non-blocking HIGH, blocking CRITICAL), and boots it with the hardened compose. Set job timeouts.

### F-10 — Runtime image composition — Medium, Supply chain, v1-blocker: no
**Evidence:** `Dockerfile:1,18` (`sdk:10.0`, `aspnet:10.0` with no digest). `:28` installs `docker.io`, the full Ubuntu engine package (dockerd, containerd, runc), where only the CLI is needed. `:30` sshpass. Tailscale comes from an unpinned apt repo. `:52-60` keeps the default user as root.
**Impact:** larger CVE surface (container runtimes are frequent Trivy hits) and non-reproducible builds. A root default for anyone running `docker run` per the README.
**Recommendation:** pin base images by digest (Dependabot updates digests). Install `docker-ce-cli` (or a static docker CLI binary) instead of `docker.io`. Consider separate `-full` and `-hardened` image variants with `USER 10001` in the hardened one.

### F-11 — Installer defaults and stale guidance — Medium, Operations/UX, v1-blocker: yes (text) / no (rest)
**Evidence:** `deploy/install.sh:15` uses image `:latest` (re-run = `pull` of whatever is latest, no version pinning or rollback). `:134-154` writes the privileged full profile with no VAULT_KEY (vault and encrypted backups off), no healthcheck override, no `.env`. `:84` pipes `get.docker.com` into sh. There is no `cosign verify` of the image even though the README promotes signing. `:188-191` says "First sign-in needs an auth provider until the setup wizard ships ... add `Auth__Disabled=true`". That contradicts README §First sign-in (the wizard has shipped) and pushes users toward disabling auth. README `:103` installs from `main` (unpinned script).
**Impact:** a poor and riskier first-run experience: users are told to disable auth, upgrades are uncontrolled, and the vault is off by default.
**Recommendation:** fix the next-steps text (point to the wizard and the setup token per F-02). Default to the released version (`WHISKERS_VERSION`), and offer `--channel latest`. Generate a VAULT_KEY into `.env` (chmod 600) on first install and tell the user to back it up. Optionally run `cosign verify` when cosign is present. Offer `--profile hardened`. Serve install.sh from the release assets (checksummed) in the README.

### F-12 — Migrations at boot without a safety net — Medium, Reliability, v1-blocker: no
**Evidence:** `src/Whiskers/Startup/WhiskersPipelineExtensions.cs:447,457` (metrics + Identity `MigrateAsync` inside `RunWhiskersStartupAsync`, before Kestrel listens). There is no pre-migration snapshot in `Services/Persistence/DatabaseInitializer.cs`. Helm `startupProbe` allows 30×2 s = 60 s (`deploy/helm/whiskers/templates/deployment.yaml:204-207`). Docker HEALTHCHECK `start-period=40s` (`Dockerfile:67`).
**Impact:** a slow migration on a large metrics DB (or Postgres lock wait) can exceed 60 s. The kubelet then kills the pod mid-migration and loops (CrashLoopBackOff). On SQLite, killing mid-migration is mostly transactional but not guaranteed for every operation. There is no automatic way back after a bad migration.
**Recommendation:** take an automatic `VACUUM INTO`/pg_dump snapshot when pending migrations exist. Make the startup probe budget configurable (default about 5 min). Log migration progress. Document a rollback (restore the previous image plus the pre-migration snapshot).

### F-13 — Test architecture gaps — Medium, Testing, v1-blocker: no
**Evidence:** see the inventory below. There is no `HasPendingModelChanges` test (`grep` finds none in tests), so EF models can drift from the SQLite/Postgres migration snapshots. `PostgresSmokeTests.cs:20-25` swallows container start failure and `Skip`s, and CI (`ci.yml:36-39`) only asserts that ≥1 test passed, so on a Docker Hub rate limit the Postgres suite silently disappears. Identity on Postgres is not tested. Only 2 bUnit files with 3 tests for a large Blazor UI. No browser E2E, no image-level or hardened (non-root/read-only) boot test, no upgrade test from release N-1 data, and Helm kind-smoke is manual only (`chart-ci.yml:245-247`). Coverlet is referenced but no coverage is collected or gated.
**Recommendation:** add a per-provider `Assert.False(db.Database.HasPendingModelChanges())`. Make RequiresDocker tests fail in CI (env `CI=true` → no skip) and/or assert a skipped count of 0. Add an upgrade test that boots the current build against a checked-in N-1 SQLite fixture. Add a hardened-compose boot in CI, Playwright smoke for login/setup/dashboard, and enable kind-smoke on tags.

### F-14 — Hardened socket-proxy exposed on host loopback — Medium, Security, v1-blocker: no
**Evidence:** `docker-compose.hardened.yml:148-167`: `POST: 1`, `CONTAINERS: 1`, `IMAGES: 1`, `VOLUMES: 1`, published at `127.0.0.1:2375`. The app is host-networked. The compose comment acknowledges "container-create stays on" as a residual risk.
**Impact:** any local process or unprivileged user on the host can create a privileged container with `/` bind-mounted through the proxy, which is root-equivalent with no authentication. That weakens the "hardened" claim on multi-user hosts.
**Recommendation:** share a unix socket via a named volume (socket-proxy supports a listen socket), or put the app and proxy on a private bridge network instead of host networking where possible. At minimum, document this residual clearly in container-hardening.md.

### F-15 — Background-loop exception policy — Medium, Reliability, v1-blocker: no
**Evidence:** there is no `HostOptions`/`BackgroundServiceExceptionBehavior` configuration anywhere (`grep` finds none), so the .NET default `StopHost` applies. Loops use `catch (Exception ex) when (ex is not OperationCanceledException)`: `ContainerHealthMonitor.cs:70`, `SchedulerService.cs:46`, `AutoUpdateService.cs:56`, `CertificateMonitor.cs:67`, `LogMonitorService.cs:133`, `LogHygieneMonitor.cs:65`, `ScanSupervisor.cs:75`, `SelfMetricsRecorder.cs:67`, `ActionOutcomeSweeper.cs:51`. A *non-shutdown* `TaskCanceledException` (HttpClient timeout, a CTS inside a dependency) therefore escapes the loop and stops the whole app. `MetricsCollectorService` and `CveMonitorService` use the correct pattern (`when (ct.IsCancellationRequested)`). Unvalidated intervals: `ContainerHealthMonitor.cs:75` (`CheckIntervalSeconds`, 0 = hot loop, negative = `ArgumentOutOfRangeException` → StopHost) and `ImageUpdateChecker.cs:54` (`CheckIntervalHours`, same). There is no `ValidateOnStart` for options. `ImageUpdateChecker.RunAsync` (`:51-55`) has no loop-level try/catch; it relies on the inner catch.
**Likelihood:** low. The inner layers convert most timeouts, e.g. `ContainerOperations` per-server 8 s CTS, `LogMonitorService.cs:385` → `TimeoutException`. But the blast radius is the whole control plane, which then gets restarted by `restart: unless-stopped`.
**Recommendation:** use `catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception ex) { log }` uniformly, or put it in `FleetBackgroundService`. Set `BackgroundServiceExceptionBehavior.Ignore` together with a self-metric/alert on loop death. Floor/validate intervals via `IValidateOptions` + `ValidateOnStart`.

### F-16 — Shutdown semantics of mutating background work — Low, Reliability, v1-blocker: no
**Evidence:** `AutoUpdateService.cs:54`: `CheckAndUpdateAsync(...).WaitAsync(stoppingToken)` abandons `RecreateContainerAsync` (pull/stop/remove/create) at shutdown, and the process exit can leave a container removed but not recreated. A rollback snapshot exists (UpdatePathRollbackTests), but there is no resume or reconciliation on boot. `SchedulerService.cs:111` uses `Task.Run(() => RunTaskAsync(taskId), CancellationToken.None)`: backups and tasks are killed mid-run on shutdown, leaving `LastRun` set and no history row. Cron is evaluated in UTC (`SchedulerService.cs` `GetNextOccurrence(DateTime.UtcNow)`) while the compose sets `TZ=Europe/Berlin`. This is likely surprising, but unverified whether the UI states UTC. `AutoPatchService.cs:35` `_lastNightlyRun` is in memory only, so a restart inside the window hour re-runs the nightly cycle (the cycle is idempotent-ish, so low impact).
**Recommendation:** track in-flight mutating operations and have `StopAsync` wait for them up to a bounded grace period (raise `HostOptions.ShutdownTimeout`, Helm `terminationGracePeriodSeconds` 30 → 120). Reconcile a "recreate in progress" marker at boot. Persist the autopatch last-run. Show the cron timezone in the UI.

### F-17 — Logging/observability basics — Low, Operations, v1-blocker: no
**Evidence:** `src/Whiskers/appsettings.json:2-9` uses the default console text formatter. There is no `AddJsonConsole`, Serilog or OpenTelemetry (`grep` finds none). There is no request correlation middleware: `CorrelationId` exists only on agent/notification models. Positive: `/metrics` includes `whiskers_self_*` series computed without Docker/DB and is token-gated.
**Recommendation:** offer `Logging__Console__FormatterName=json`, add an `X-Request-Id`/`traceparent` middleware with `BeginScope`, and optional OTLP export. Document the log locations and the key metrics/alerts (stalled loop, circuit open, budget saturation).

### F-18 — Documentation gaps — Medium, Documentation, v1-blocker: yes (upgrade + backup minimum)
**Evidence:** README sections (`README.md:96-192,418-448`) cover install, profiles, Helm and config. There is no upgrade guide (version policy, migration irreversibility, rollback procedure, `latest` vs pinned), no troubleshooting (healthz vs readyz, circuit/budget, VPN/tailnet issues — deploy/README has tailnet-guard only), no production checklist (bind/TLS/proxy, VAULT_KEY backup, setup token, profile choice, backups off-volume, metrics token), and no backup/restore operator guide beyond one feature bullet (`README.md:243`) and the Helm README §Backup. Postgres backup is undocumented (F-05). Helm NOTES.txt is German only (`deploy/helm/whiskers/templates/NOTES.txt`).
**Recommendation:** add `docs/operations/{upgrade,backup-restore,troubleshooting,production-checklist}.md` and link them from the README and install.sh output.

### F-19 — Dependency update coverage — Low, Supply chain, v1-blocker: no
**Evidence:** `.github/dependabot.yml:4-17` covers only `/src/Whiskers` and `/src/Whiskers.Tests`, missing `src/Whiskers.Data` (EF Core, Identity), `Whiskers.Migrations.Postgres` (Npgsql) and `Whiskers.Migrations.Sqlite`. The docker ecosystem covers only `/` (Dockerfile). Not covered: compose images (`postgres:17-alpine`, socket-proxy digest), the telemetry stacks, or the Helm chart. There are no `packages.lock.json` files, no `Directory.Packages.props`, no `global.json` (CI uses `10.0.x`).
**Recommendation:** add the missing directories (or move to Central Package Management + one entry), add `docker-compose` ecosystem entries, enable lock files with `--locked-mode` in CI/Dockerfile, and pin the SDK in global.json.

### F-20 — Helm chart details — Low, Operations, v1-blocker: no
**Evidence:** `values.yaml:71` `tailscale/tailscale:latest`. `deployment.yaml:265-267` tailscale state is `emptyDir`, so each restart registers a new tailnet node (node churn, auth key reuse needed). There is no `VPN_PROVIDER`/`Vpn__Provider` env in the chart, so `entrypoint.sh:93-114` takes the legacy path and starts `tailscaled` as uid 10001 on a read-only rootfs, which fails in the background, costs `sleep 2`, and adds misleading log lines. Postgres with neither `existingSecret` nor `connectionString` references a non-existent secret (`deployment.yaml:160-164`), giving `CreateContainerConfigError` instead of a `required` template error. No NetworkPolicy template.
**Recommendation:** pin the sidecar, use a Secret-backed or PVC state for tailscale (`TS_KUBE_SECRET`), set `VPN_PROVIDER=none` in the chart, add `required` checks, and add an optional NetworkPolicy.

### F-21 — Tailscale auth key in argv under `pid: host` — Low, Security, v1-blocker: no
**Evidence:** `entrypoint.sh:104-106` (`tailscale up --authkey="$TAILSCALE_AUTHKEY"`) with `docker-compose.yml:22` (`pid: host`). The key is visible in `/proc/*/cmdline` to host users while the command runs. Also, default hostname `serverwatch` (`:106`).
**Recommendation:** use `--authkey=file:` / `TS_AUTHKEY` env with `tailscale up --auth-key` reading from a file, or rely on the in-app provider.

### F-22 — Legacy AiChat client — Low, Reliability, v1-blocker: no
**Evidence:** `Modules/Agent/AgentModule.cs:55-56` registers a typed client and then `AddSingleton<AiChatService>()`, so the singleton captures one HttpClient forever (no handler rotation). `Services/AiChat/AiChatService.cs:209,243` call `SendAsync(request)` with no CancellationToken, the 100 s default timeout, and do not dispose the response.
**Recommendation:** inject `IHttpClientFactory`, set the timeout to 60 s, pass the circuit token, and use `using var response`.

### F-23 — Release scan gate scope — Low, Supply chain, v1-blocker: no
**Evidence:** `release.yml:61-81` scans only the amd64 build, `severity: CRITICAL`, `ignore-unfixed: true`. arm64 is pushed unscanned (`:84-98`).
**Recommendation:** scan both arches (or the pushed index), report HIGH as SARIF to code scanning, and keep CRITICAL blocking. Publish the scan report as a release asset.

---

## Background services

All `FleetBackgroundService` loops get a background budget lane and a deterministic 0–30 s startup offset (`Services/FleetBackgroundService.cs:34-51`). No `HostOptions` are configured, so the exception behavior is StopHost and the shutdown timeout is 30 s by default.

| Service (file:line) | Interval | Per-op timeout | Cancellation | Failure handling / notes |
|---|---|---|---|---|
| MetricsCollectorService (`Services/Metrics/MetricsCollectorService.cs:51`) | `Metrics:CollectionIntervalSeconds` (30 s, floored 5 s) | Prometheus 10 s; Docker via budget; stats bounded by semaphore | ct to delay/semaphore; stats calls take no ct | Correct: `OCE when ct` → break; catch-all logs; self-metrics per server |
| ContainerHealthMonitor (`Services/HealthMonitor/ContainerHealthMonitor.cs:57`) | `HealthMonitor:CheckIntervalSeconds` (30 s, **no floor**) | per-server listing 8 s (ContainerOperations) | `WaitAsync(ct)` abandons on stop | `when not OCE` filter (F-15); awaits notifications inline (F-08) |
| ImageUpdateChecker (`Services/ImageUpdate/ImageUpdateChecker.cs:36`) | `CheckIntervalHours` (6 h, **no floor**) | registry 15 s; 5 parallel | `WaitAsync(ct)` | catch-all inside `CheckAllImagesAsync`; no loop-level catch |
| AutoUpdateService (`Services/AutoUpdate/AutoUpdateService.cs:41`) | 5 min (per-policy interval) | none on recreate | `WaitAsync` abandons a **mutating** recreate (F-16) | `when not OCE`; notify failures isolated; self-metrics |
| CveMonitorService (`Services/Cve/CveMonitorService.cs:69`) | 12 h (min 1 h), 15 min retry after failure | Trivy 10 min/process, kill tree; Trivy `--timeout 8m` | ct honored | Correct pattern; atomic scan gate; due-based scheduling survives restarts |
| SchedulerService (`Services/Scheduler/SchedulerService.cs:31`) | 30 s tick | per task type (executor) | tasks `Task.Run(..., None)` (F-16) | invalid cron → disable; NextRun persisted before run (at-most-once); in-flight guard in memory |
| AutoPatchService (`Services/AutoPatch/AutoPatchService.cs:74`) | 10 min tick, once/day in window hour | runner-specific | ct checked per candidate | catch-all; CAS gate; dry-run support; last-run in memory |
| CertificateMonitor (`Services/Certificates/CertificateMonitor.cs:50`) | 12 h (+5 min startup) | TLS handshake 10 s | ct | `when not OCE`; per-cert errors isolated |
| LogMonitorService (`Services/LogMonitor/LogMonitorService.cs:119`) | 60 s | 15 s per log fetch, cancelled server-side | ct reaches Docker calls | `when not OCE`; timeout → backoff/suspension (good incident learnings) |
| LogHygieneMonitor (`Services/LogMonitor/Hygiene/LogHygieneMonitor.cs:55`) | 24 h (+5 min) | — | `WaitAsync(ct)` | `when not OCE` |
| SelfMetricsRecorder (`Services/Observability/SelfMetrics/SelfMetricsRecorder.cs:44`) | 1 min | — | ct | restore guarded; `when not OCE` |
| ScanSupervisor (`Services/Observability/ScanSupervisor.cs:63`) | 1 min (+2 min) | — | ct | detects stalled loops (3 intervals); `when not OCE` |
| SuspensionReminder (`Services/Observability/SuspensionReminder.cs:48`) | 1 h | — (sync) | ct on delay | catch-all |
| ActionOutcomeSweeper (`Services/Observability/Outcomes/ActionOutcomeSweeper.cs:32`) | 1 min (+2 min) | — | ct | `when not OCE`; self-metrics |
| VpnBootstrapHostedService (`Services/Vpn/VpnBootstrapHostedService.cs:19`) | once at start (blocks StartAsync) | VpnProcessRunner timeout | ct | catch-all, never crashes startup |
| (not hosted) AiTriggerDispatcher (`Services/Agent/Triggers/AiTriggerDispatcher.cs:63,91`) | event-driven, fire-and-forget | **none** | **none** (F-07) | catch-all; gate `WaitAsync(0)` skips |

## Test inventory (src/Whiskers.Tests)

- 150 test files with 931 test methods: 874 `[Fact]`, 55 `[Theory]` (more cases at runtime), 2 `[SkippableFact]`. Frameworks: xUnit 2.9, bUnit, `Microsoft.AspNetCore.Mvc.Testing`, Testcontainers.PostgreSql, coverlet (not used in CI).
- Approximate counts by type (grouped by file name, overlapping):
  - Unit/domain logic: the large majority (about 600+).
  - Security regression: about 100. Examples: SecretHygiene, WebhookSignature/Secret, McpInputValidation, ServerLogsToolInjection, DestructiveOpTargeting, DirectMcpCallGuard, MetricsScrapeAuth, EnvMasking, ForwardedHeaders, MarkdownSanitizer, AuthBootstrap, McpPermission/ToolLevel, GuardrailEngine, DockerMtlsHostname, RegistryChallenge, SetupRedirectPaths.
  - Reliability/load: about 100. Examples: BackgroundServiceLane, CircuitBreaker, BudgetSaturation, ServerBudget, ResumeWithoutStorm, LoopSuspension*, ScanSupervisor, NotificationRetry/Throttler, HostLoadReplay, LogMonitorLoadInvariant, DockerConnectionFailure, TrivyOutputTruncation.
  - In-process app boot/integration via `WebApplicationFactory`: 9 files, about 37 tests. Examples: BootMatrix, MaintenanceBoot, SetupWizardBoot, ModuleMatrix, McpServedSurface, McpPromptsAndResources, SelfMetricsEndpoint, DirectMcpCallGuard, LocalAuthIdentity.
  - Provider wire-format: about 34 (Anthropic/OpenAI/Gemini/Multimodal wire, ProviderError).
  - DB/migration: about 33 (DbMigrationBaseline, PostgresSmoke (2, skippable), SqliteToPostgresMigrator, store persistence/prune, UtcDateTimeConverter).
  - DR/backup: about 30 (BackupArchiveCipher, BackupArchiver, BackupService, RestoreBootHandler, UpdatePathRollback, VolumeBackupsModule).
  - UI component (bUnit): 3 tests in 2 files.
  - E2E/browser: 0. Image/container boot: 0 in tests (CI has a `dotnet run` DI boot gate in `ci.yml:43-58`). Helm: render invariants only (kind smoke manual). Upgrade-from-N-1: 0. Migration drift (`HasPendingModelChanges`): 0. Postgres Identity: 0.
- CI guards: the "no tests ran" guard exists (`ci.yml:36-39`, grep for `Passed: [1-9]`). There is no guard against skipped RequiresDocker tests, and no coverage threshold.

## Positive controls (brief)
- Release: Trivy gate before push, SLSA provenance + SBOM attestations, a standalone SPDX SBOM, keyless cosign signing by digest, checksums, and an image-pinned compose (`release.yml`).
- CI: least-privilege `contents: read`, an empty-test-run guard, and a real DI boot gate. Chart CI asserts security invariants (non-root, RO rootfs, probes, no docker.sock by default).
- Hardened compose: non-root, cap_drop ALL, no-new-privileges, read-only rootfs, digest-pinned socket-proxy with a verb whitelist. Helm: non-root, seccomp RuntimeDefault, RO rootfs, Recreate + single replica, fsGroup, a startup probe, and no socket by default.
- `/healthz` (dependency-free liveness) is separate from `/readyz` (DB, server config, maintenance), and both return only a status word. `/metrics` is opt-in, token-gated and culture-pinned, and exports `whiskers_self_*` independent of Docker.
- Background loops have a shared base class (budget lane, staggered start), self-metrics per loop, and ScanSupervisor stall detection. The host command executor has hard timeouts with process-tree kill and output caps. Log fetches are cancelled server-side (incident-driven).
- Notifications: per-client 15 s HttpClient timeouts, throttling, and channel isolation via `Task.WhenAll` + `SafeSend`. Webhooks have mandatory secrets and a constant-time HMAC.
- The installer is idempotent, binds to localhost by default, never installs Docker without opt-in, and waits for healthz.
- Self-backup uses consistent SQLite `VACUUM INTO`, optional AES-GCM, and a crash-safe deferred restore.
