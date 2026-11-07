# Changelog

All notable changes to Whiskers are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow SemVer
(0.x = pre-1.0, minor bumps may contain breaking changes — noted explicitly).

## [Unreleased]

- **One audit event per mutation attempt, proved per path (INV-45).** An attempt begins when the handler asks the
  gateway for the capability; a page returning before that changes nothing (user decision 2026-11-07). A path-matrix
  test reads the rows of `PRODUCT_CONTRACT.md` §7 and proves each against the real audit log for every outcome it can
  have. Refusals that were made before the gateway and left no event — an AutoPatch night without an owner or with an
  unreadable owner entry, an AutoUpdate with a blocked owner entry, an endpoint-check run the monitor refused, a
  webhook delivery that ended before its decision or was given up after its start, a scheduled task refused before
  the gateway (no target or command, a missing database container, a protected stopped container) — are now recorded
  as denied events. An endpoint check whose suspension cannot be saved is refused again at its interval, not every
  15 seconds.
- **Docs: the README is now facts and commands only.** Install, first sign-in, MCP client, image verification, key
  settings, accepted limits and the compose remaining risks stay in it; the rest moved unchanged in substance to
  `docs/governance.md`, `features.md`, `installation.md`, `configuration.md`, `mcp.md`, `agent.md`,
  `security-architecture.md`, `screenshots.md`, `roadmap.md` and `project-structure.md` (indexed in `docs/README.md`).
- **Exposure changes in compose deployments are decided as such (contract 0006, INV-62).** A compose deployment that
  publishes a port, uses the host network, runs privileged, adds capabilities, shares host namespaces, mounts devices,
  weakens a security option, shares a namespace with another container or carries a route label (Tailscale
  Funnel/serve via DockTail, Traefik, Caddy) is decided on each of these on every compose path (Compose editor, app
  templates, `deploy_compose`, `set_container_env`, the `.env` save, compose webhooks, Git deploy), and what starts is
  checked against the decision. New policy fields `ExposureLabelPrefixes` and `ExposureConfirmClasses`; new
  capability `container.disable_restart` (only composed); new notifications `exposure_change` (suppressible) and
  `exposure_violation` (never suppressed); a readiness line for Whiskers' own container published with sign-in off.
  **Behaviour changes:**
  - An agent's or API key's deployment with Funnel (or another `public` route label) or the host network (class
    `host`) now waits for a confirmation **even under a policy that lets Admin act autonomously**; an unattended run
    owned by a key is refused. An empty `ExposureConfirmClasses` turns this off.
  - The new defaults change every policy's `VersionHash`: open approvals and the policy snapshots of pinned runs
    expire at the upgrade.
  - Unresolved variables are now refused: a label key, a label value under an exposure prefix, a port, network or
    privilege field, a relative `label_file` or `COMPOSE_PROFILES` from a variable the `.env` does not set refuses the
    plan (compose would also read the host's environment).
  - Redeploys of projects with `build:` services now rebuild with `--pull` (compose deploys, app templates, compose
    webhooks); the `.env` paths build only missing images, without `--pull`.
  - Git deploys now pull images (`pull --ignore-buildable`) before building.
  - Every compose run starts with `up -d --pull never --no-build` after the images were inspected.
  - `docker compose pull --ignore-buildable` needs **Docker Compose v2.15 or newer** on every managed host; an older
    Compose fails the run.
  - Under the agent rule set a container a run created with an exposure nobody decided is stopped and its restart
    policy set to `no`. A host cgroup namespace the compose file did not ask for counts only when it is not the
    daemon's default (read from `daemon.json` / `docker info`; unreadable → it counts; on cgroup v1 a privileged
    container's default is `host` whatever `daemon.json` says).
  - A `compose.deploy` call that brings the nested decisions' own arguments (`exposure…`, `related…`, `mounts`) is
    refused on every surface except UI and automation.

- **Every managed host gets its own mTLS client identity (R1-44, INV-58): action needed after the upgrade.**
  - **Before:** one client certificate (`serverwatch-client`) opened every host. Its key, if stolen, opened the whole
    fleet.
  - **New hosts:** onboarding issues the host its own identity `whiskers-client-{slug}-g1`. The key is made in Whiskers'
    process and stored in `{MtlsDir}/hosts/{id}/`. The host's ghostunnel admits only that name.
  - **Existing hosts** keep working on the shared credential until you migrate them, one at a time, from the server page
    (key icon) or with the Admin MCP tool `migrate_mtls_client_credential`.
    - Each host step changes only the `--allow-cn` items of ghostunnel's compose file.
    - Each step is guarded: the host restores the previous file by itself unless the step is confirmed through the new
      credential.
    - A host counts as migrated only once the shared credential was refused there with a TLS alert.
  - **Renewal and rotation:** `renew_mtls_client_credential` renews a migrated host's credential, or rotates it to the
    next generation with `rotate=true`.
  - **Retiring:** once every host is migrated, the shared key can be retired from the server page (UI only).
  - **Readiness checklist:** it lists every host still on the shared credential, every unfinished migration, every host
    whose ghostunnel is not running or cannot be read, and every migrated host whose running allow list differs.
  - **Order:** Bifröst first, then the whiskers.site host, then host by host. Guide:
    `docs/operations/per-host-mtls-migration.md`.
- **Tailscale Serve, Funnel and DockTail in front of Whiskers: documented.**
  - In TCP mode tailscaled adds no forwarded headers. This covers `tailscale serve --tcp`/`--tls-terminated-tcp` and
    DockTail with `protocol=tcp`.
  - What the client sends passes through unchanged, `Host` and `X-Forwarded-*` included. Every visitor arrives from
    tailscaled's private address.
  - So `AUTH_DISABLED=true` behind such a forward makes every visitor Admin; with Funnel, that is anyone on the
    internet.
  - With sign-in enabled, a forward from a trusted proxy address lets the client choose its own address. That defeats
    the per-client login rate limit and the audit.
  - The README ("Name your proxy") covers both modes, which address to trust, what trusting the Docker gateway also
    trusts, and the sidecar layout that needs no entry.
  - The production-readiness line for disabled authentication warns against TCP forwards and Funnel.
  - No behaviour change.

- **An approved call executes only on the approved target.** An approval was bound to the exact call the gateway
  checked, but the code that then ran was the caller's own delegate, and the scope guard matched only its capability:
  inside a decision for container `web-1` a delegate could have restarted `db-1`, or `web-1` on another server. Marked
  operations now declare the parameter that names their target (`[MutationTarget]`: container start/stop/restart/
  remove, `db.query`, systemd units, nginx sites), and the scope guard refuses such a call unless an open decision
  covered that identity on the server the call acts on. No behaviour change for calls that act on what was decided.

- **PostgreSQL 18 for the database overlay (`deploy/docker-compose.postgres.yml`) — action needed when you come from
  17.**
  - **What changes:** the overlay runs `postgres:18-alpine` on a new volume `whiskers-db18-data`, mounted at
    `/var/lib/postgresql`. The 18 image refuses the old mount at `/var/lib/postgresql/data`, even for an empty volume,
    and PostgreSQL does not read a 17 data directory.
  - **The guard:** the new one-shot service `db-upgrade-guard` decides whether PostgreSQL 18 may start. It passes when
    the old `whiskers-db-data` volume is empty (a fresh install, or the 17 volume removed), or when it holds a 17
    cluster that the guard itself restored: a fingerprint of that cluster's `global/pg_control` in the 18 volume.
    It refuses everything else, among it a blind pull, a failed or skipped restore, a 17 data directory it does not
    recognise (a volume holding only `lost+found` included) and a volume it cannot read. Whiskers then stays down with
    a message that names the volumes and points to the guide; a pulled overlay never starts as an empty,
    fresh-looking install.
  - **Dump bound to the 17 state:** the guide's dump command refuses while other clients (Whiskers) are connected to
    the 17 database; a refused or cut-off dump has no closing line, so the restore refuses it. After the dump, with
    17 stopped,
    `docker compose … run --rm --no-deps -T -e WHISKERS_FINGERPRINT=1 db-upgrade-guard >> whiskers-pg17.sql` appends
    the 17 cluster's fingerprint as one SQL comment line; it refuses while 17 still runs. Any later 17 start changes
    the fingerprint, so a stale dump is refused.
  - **Restore through the guard:** with the 18 database running,
    `docker compose … run --rm --no-deps -T -e WHISKERS_RESTORE=1 db-upgrade-guard < whiskers-pg17.sql` checks the dump
    (complete, fingerprint of the current 17 data) and the server (PostgreSQL 18, no Whiskers tables yet, so an 18
    database Whiskers initialised on its own is refused), restores in one transaction, checks the migration history
    and only then writes the marker.
  - **Upgrade:** `docs/operations/postgres-18-upgrade.md` covers it: dump on 17 (user and database from the container's
    own environment, the file created private) and fingerprint, restore through the guard, start. The database's
    health check now asks over TCP, so the image's temporary init server never counts as ready.
  - **Rollback:** the new overlay mounts the 17 volume read-only. The dump and a rollback run PostgreSQL 17 on it
    read-write, so the way back is the data as of the dump. After going back, the 17 data has changed and the guard
    refuses to start 18 on the older copy; to upgrade again, remove the 18 volume first (the guide shows how to dump
    the 18-period data before that) and take a new dump. `down -v` now removes the 17 volume kept for the way back as
    well.
  - **Tested with Docker,** in the test suite and once by hand with compose:
    - 17 → 18 with Whiskers' schema, data, migration history and sequences carried over;
    - the guard refusing a blind pull and, after a rollback, the older 18 copy and the stale dump;
    - the restore refusing an 18 database Whiskers had initialised;
    - a second upgrade and a fresh install starting directly on 18.
  - **Not affected:** SQLite installs and Helm installs with an external database.
  - **Tests:**
    - `PostgresUpgradeGuardTests`: the guard's script, taken from the compose file, runs against prepared volumes,
      including the fingerprint and restore modes' dump checks.
    - `PostgresUpgradeTests`: the guide's flow from 17 into 18 with the guide's own dump command, the restore mode's
      server checks, the rollback of a dump that fails halfway and a marker that cannot be written.
    - `PostgresPgControlTests`: a real 17 start changes the fingerprint.
    - `PostgresOverlayComposeTests`: `docker compose config` over the overlay.
    - The PostgreSQL suites run on 18; the self-backup suite also runs on 17 and 14.

- **New: endpoint checks from the managed host (R1-57).** Under **Checks** (`/checks`) an Admin defines HTTP
  (status and latency), TCP-connect and TLS-expiry checks that run **on the host** they are bound to, from its side of
  the network, through the existing host command executor; Whiskers never connects to the target. A name is resolved
  on the host and every answered address must be public or allowed by an Admin-only exception on the check; at most 4
  addresses are tried, each run is bounded (1 + 4 calls, timeout × 5 + 10 s, 16 KiB output) and passes only on
  positive evidence. Every run is one audited `endpointcheck.run` event as the check's owner; a check whose owner is
  no longer Admin is suspended. A check failing for 5 minutes raises `endpoint_check_failed`, 5 minutes of passing
  clear it. Results are kept 7 days. All roles can read the page (an HTTP target's query string only for Admins; below
  Admin, internal addresses in a run's detail and a suspension reason are not shown); the read-only MCP tool
  `endpoint_checks_list` shows the checks and their last outcome and numbers, never internal addresses or a suspension
  reason. Alerts show internal addresses as `[internal address]` and never name a suspended check's former owner, in
  every channel and for every role. "Run now" runs the check as it is stored at that moment, shares the run slots
  with the background loop, is not offered for a check that is off, and neither moves the loop's schedule nor feeds
  the alert. **Hosts need `getent`, `curl`, `openssl` and OpenBSD `nc`** — see
  [docs/operations/endpoint-checks.md](docs/operations/endpoint-checks.md). New tables `EndpointChecks` and
  `EndpointCheckResults` (migrated automatically on SQLite and PostgreSQL).
- **New: optional disk-health readings — SMART, md RAID, ZFS (R1-58).** Whiskers can show whether each host's disks
  and arrays are healthy, read only from the host's existing metrics endpoint (node_exporter through VictoriaMetrics).
  It never runs anything on the host. Off by default; an Admin switches it on in Settings → *Disk health (SMART, RAID,
  ZFS)* (a gated, audited `config.save`). Each server's expanded dashboard panel then shows a *Disks* block: the overall
  state and one line per disk, array and pool, readable by every role. States: Healthy, Degraded / failing, Unknown and
  Not supported. Whiskers shows Unknown, never Healthy, when it cannot vouch for a reading: a failed or malformed
  query, something missing since the last reading, or SMART data older than 2 hours. Freshness is judged by the
  smartctl run time and the textfile's modification time, never by the query time. New alerts `disk_failing`,
  `array_degraded` and `disk_health_unknown` are raised after 10 minutes and cleared after 10 minutes of good readings.
  Quiet hours never hold them; Unknown never clears them. SMART needs a host timer writing `smartmon.prom`:
  `docs/operations/disk-health.md` has the shell script and systemd units. md and ZFS need nothing. **Upgrade note:**
  the node_exporter template (`deploy/telemetry/host/docker-compose.yml`, and the onboarding wizard) now enables the
  textfile collector (`--collector.textfile.directory=/host/var/lib/node_exporter/textfile_collector`, through the
  existing read-only mount). Hosts set up earlier need the flag added and node_exporter recreated before they can
  deliver SMART data. The block shows when the reading was made; a reading older than two cycles plus a
  minute is shown Unknown. SMART counts as failing only on a disk with SMART available and enabled; disagreeing
  duplicate series are Unknown, and one device reported twice under the same name is one line (failing if a copy
  fails, Unknown otherwise).
  `disk_health_unknown` is armed per disk, array and pool, and an Unknown reading restarts the 10 minutes. Switching
  the feature off forgets the readings at once (also the way to drop a renamed or removed device; disabling one server
  for a cycle does it for that host); a value set through
  the environment (`DiskHealth__Enabled`) is shown read-only in Settings.
- **Security: dependencies on the latest patches of their supported lines.** .NET SDK 10.0.403 (`global.json`, build
  image) with the 10.0.14 runtime; Entity Framework Core, Identity and `System.Security.Cryptography.Pkcs` 10.0.14,
  MailKit 4.18.2, Markdig 1.6.0, MudBlazor 9.12.0; lock files refreshed (`dotnet list package --vulnerable
  --include-transitive`: none). Test-only: Microsoft.NET.Test.Sdk 18.11.0, Testcontainers 4.16.0 (it now brings the
  fixed SSH.NET 2026.0.0 itself, so the separate pin is gone), Xunit.SkippableFact 1.5.85; bUnit still pulls the
  10.0.13 Blazor component packages into the test project (test-only: the Components packages come from the shared
  framework at run time, the WebAssembly ones are not used by the tests).
- **docker-socket-proxy v0.5.0** (HAProxy 3.x; 0.3.0 shipped the end-of-life HAProxy 2.2). The verb whitelist is
  unchanged — a test now pins it exactly, in the hardened profile and in the onboarding template — and, measured by
  hand against a real Docker engine, 24 endpoints answered with the same status as under 0.3.0. Whiskers does not use
  the new pause/unpause switches and does not set them (with `POST: 1` they would not narrow anything).
  **Fix:** the hardened profile's socket proxy did not start on its read-only root — 0.3.0 exited with "can't create
  /usr/local/etc/haproxy/haproxy.cfg", v0.5.0 with "can't create /tmp/haproxy.cfg"; it now has a `/tmp` tmpfs.
  **Upgrade note:** if you keep your own copy of `docker-compose.hardened.yml`, add `- /tmp` under the socket proxy's
  `tmpfs:` together with the new image.
- **Onboarding** rolls out the socket proxy pinned by digest (it used `:latest`) and **ghostunnel v1.11.5** (was v1.8.2)
  for **new** hosts; hosts already onboarded keep their running ghostunnel until they are updated. Tested end to end
  with a step-ca-shaped chain (root + intermediate, leaf bundle, root-owned 0600 key): the `serverwatch-client`
  certificate reaches the Docker API (`/version` 200, `/secrets` 403 by the whitelist), another CN and a client without
  a certificate are refused in the handshake — the same as v1.8.2; v1.11.5 additionally negotiates hybrid
  post-quantum key exchange (X25519MLKEM768). A test keeps the templates' socket-proxy and node_exporter images equal to
  the compose files Dependabot updates.
- **Telemetry:** node_exporter v1.12.1, VictoriaMetrics v1.155.0. **Upgrade note (VictoriaMetrics):** since v1.133 the
  storage uses a per-partition index; the first start after the upgrade registers every active series once (slower
  ingestion and queries for a while). Take a snapshot of `vm-storage` before upgrading: going back to a release
  before v1.133 means restoring that snapshot.
- **Release workflow:** cosign-installer v3.10.1 with cosign pinned to v2.6.1 explicitly (cosign v3 changes the
  signature format and is its own change), sbom-action v0.24.3; chart CI: kind-action v1.15.1.

- **Security: proxy trust defaults to loopback only — check your reverse proxy (R1-50).** Whiskers used to believe
  `X-Forwarded-For` from every private network (RFC 1918 and Tailscale's range), so any client in the LAN or tailnet
  could claim another address and slip past the per-client sign-in limit. Now only loopback is trusted unless
  `ForwardedHeaders__TrustedNetworks__0` (… `__1`, …) names your proxy as a CIDR or address. **Upgrade note:** behind a
  proxy in another container or on another host, add its address before you upgrade — until then Whiskers sees the
  proxy's address for every visitor and, behind a TLS-terminating proxy, plain HTTP: the session cookie loses its
  `Secure` flag, Google/OIDC sign-in breaks (the callback is built with `http://`), and with `AUTH_DISABLED=true`
  every proxied request is refused (`403`, naming `ForwardedHeaders:TrustedNetworks`) instead of being let in under the
  proxy's private address. Settings → production readiness names the ignored peers. An entry that is not a canonical
  CIDR or address (`172.18.0` or `10` no longer pass) stops the start; a single value without index is read as a
  comma-separated list. Helm: set `trustedProxyCidrs` to your ingress controller's pods (`NOTES.txt` warns when an
  ingress is enabled and the list is empty). **Do not** set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` as a shortcut:
  it makes ASP.NET Core trust forwarded headers from every peer, and Whiskers now refuses to start with it.
- **Security: HTTP hardening (R1-50).** Every answer forbids framing by another site and MIME sniffing and keeps the
  referrer on the site. Signing out is a POST with an antiforgery token (another site can no longer sign you out with a
  link or image; opening `/logout` shows a sign-out button). The session cookie's flags are stated (HttpOnly,
  SameSite=Lax, Secure over HTTPS); OIDC no longer stores the identity provider's tokens in it. MCP clients without
  credentials get `401` with a Bearer challenge instead of a sign-in redirect; a signed-in user without the role gets
  `403`; an unconfigured Google/OIDC sign-in answers `404` instead of `500`; the language switch ignores a redirect that
  is not a local path. A locked account costs the same password-hash work as an unknown one.

- **Fix: a host command over mTLS can no longer hang past its timeout.** The SSH-free host shell (a one-shot helper
  container over the mTLS Docker connection) started its timeout only once the helper was created; checking or pulling
  the helper image, creating it, reading its output and removing it ran without a limit, so a stalled Docker step could
  hold the caller — and with it a background loop's slot — indefinitely. The timeout now covers every step from the
  first Docker call to the output, also a call that ignores its cancellation, and killing and removing the helper hold
  the caller for at most 5 seconds after it. A clean-up that takes longer goes on in the background with retries (up to
  2 minutes, each retry with the server's current Docker client) instead of being dropped. Every helper now carries its
  latest end as a label (`serverwatch.hostshell.deadline`), and the sweep judges a labelled helper by it — also one
  still **running** a minute past it, which before was never swept. When the background clean-up gives up, or a create
  never answers, a sweep of that server is scheduled for when the helper is past its label, and it keeps trying for up
  to an hour while the server cannot be reached; otherwise the sweep runs with a later host-shell call on the server (at
  most every 5 minutes), so a helper left behind by a Whiskers restart waits for that. The label is Whiskers' clock: a
  forward step of it between a run and the sweep could remove a helper early. The error says where the run stood: "did
  not start" before the start request, "effect unknown" after it (also for a Docker error there, which used to read
  like a plain failure), "exit code N, output not read" once it had finished. Local and SSH commands: a timeout reads
  "effect unknown" as well, and a command that exited while a process it left running kept its output open is reported
  with its exit code at the deadline instead of as a timeout; their deadline is set before the process starts, and an
  error after the start (a failed read) stops the process and reads "effect unknown" or "exit code N, output not read"
  instead of the bare exception text that looked like a start failure. A timeout that is not finite and positive (zero,
  negative, infinite) is refused on every transport before anything starts — before, an infinite one gave a local or
  SSH command no deadline at all. Timeout texts are formatted the same on every host ("0.5s", not "0,5s").
  Prerequisite of R1-57; the mTLS transport itself (certificates, allowed client name, proxy verbs) is unchanged.
- **Fix: an unset `GOOGLE_ADMIN_EMAIL` no longer switches on a whitelist that admits nobody.** Compose passes it as
  `GoogleAuth__AllowedEmails__0=`, and a fresh instance seeded an enabled whitelist holding one empty entry, which turned
  away every Google and OIDC sign-in. The seed now drops blank entries (and trims and de-duplicates the rest); with none
  left the whitelist stays off, so the C5 rule applies: only accounts with a role are admitted once a role exists, and
  since R1-27 no Google/OIDC sign-in is admitted at all until an Admin exists — set `WHISKERS_ADMIN_EMAIL` or finish the
  setup. The artifact
  check now starts the hardened image with `GOOGLE_ADMIN_EMAIL` empty and signs the administrator in. An existing
  `whitelist.json` is not changed. An instance that already holds the empty entry: stop it, delete `whitelist.json` and
  `whitelist.json.bak` in the data directory and start it again (or remove the entry in Settings, if an administrator
  can still sign in).
- **Fix: a role change takes effect only once it is saved.** `SetRoleAsync` and `RemoveRoleAsync` published the new roles
  before writing `roles.json`; a failed save left a change that was in force although the caller saw an error, and that
  was gone after the next start (a revoked role came back). They now persist first and publish after, like the setup
  wizard's first-admin write.
- R1-47: **CI and the release check the endpoints, the PostgreSQL suites and the image itself, and fail closed.**
  `EndpointAuthMatrixTests` boots the real app (Production, authentication on), enumerates every endpoint of its endpoint
  data sources (minimal APIs, pages, hubs, Blazor framework endpoints, MCP, probes, static assets) and requests each as
  anonymous, Viewer, Operator and Admin, recording whether the handler actually ran: a refused call never reaches it, an
  allowed one must, and a run counts only for the endpoint that ran, never for another one on the same route; endpoints are
  merged only as the content-encoding variants of one static asset, the static-asset fallback is probed with a file only it
  can serve (a second app with that endpoint opened to anonymous callers must fail the matrix), and an endpoint without a classification fails the
  test until someone decides who may call it. The
  PostgreSQL suites are mandatory on CI: a server that does not start throws there (`DockerRequirement`, enforced for every
  container test by `DockerRequirementTests`), CI runs them as their own step after a Docker probe and refuses any skipped
  test (`.github/scripts/check-test-results.sh`, which also replaces "some test passed" with floors read from the TRX
  files, and from manifests of required tests in `.github/required-tests/` — each named test must be in the run and pass,
  so a mandatory test cannot vanish behind a floor; `RequiredTestsManifestTests` keeps the manifests in step with the test
  assembly), and local accounts are now tested on PostgreSQL through the whole pipeline (`PostgresIdentityTests`: migrations
  of both schemas, seeded administrator, sign-in, lockout, security stamp and deletion ending the session). The image is
  checked as delivered (`.github/scripts/artifact-checks.sh`): on every push and pull request CI builds it and gates it on
  Trivy, starts it with the hardened compose profile (uid 10001, read-only root, no capabilities, health, readiness, the
  anonymous surface, a real sign-in) and upgrades the data of the previous published release below its version with it
  and back the documented way, proven on the stored records (`.github/scripts/artifact-data.mjs` reads the stopped data
  volume and reports sensitive values only as SHA-256 fingerprints, so a failed comparison never logs them): accounts, a role, a metrics row and a vault entry the previous release encrypted itself survive the upgrade
  unchanged with exactly the pending migrations added, bootstrap credentials are given to the first start only, a restart
  of the same release is a no-op, every record changed after the upgrade is back to its old value after
  `--restore-backup latest-pre-upgrade --for-previous-release` with the old migration histories and without the audit
  rows written meanwhile, and the image upgrades the recovered data again; a release runs the same checks against the
  staged amd64 digest before anything is published (`artifact-test`, which pulls that digest through
  `artifact-checks.sh prepare` — no artifact-job step runs `docker` itself). The checks print only text they write
  themselves: every command's output stays in private files, failure logs are summarised as counts per level, HTTP
  bodies and upgrade log values are compared, never shown, and the data tool reports failures by operation and file
  only. A disclosure self-test (`artifact-checks.sh self-test`, the first check in both jobs) plants random values in
  container logs, streamed output, tool errors, an HTTP answer, upgrade log lines and malformed data for each data-tool
  command, and fails if any value, fingerprint or 8-character prefix comes through. New `security.yml`:
  known-vulnerable packages (any severity), dependency review on pull
  requests, gitleaks over history and tree (`.gitleaks.toml`: no directory or line pattern is exempt, each exception — test fixtures,
  vendored-bundle identifiers, names — only as its exact value in its exact file under its rule), the .NET security analyzers as errors, CodeQL. Every job of
  every workflow is time-limited; `ReleaseWorkflowTests` pins all of it with negative tests. The repository-hosted
  controls (required checks, tag rules, code-scanning merge protection, Dependabot, secret scanning) are documented in
  `docs/operations/ci-and-release-checks.md`. Recorded for R1-50 by the matrix: the Google/OIDC challenge of an
  unconfigured provider answers 500, and an anonymous MCP call gets the sign-in redirect, not a bare 401.

- R1-52: **Reproducible builds and a smaller image without the Docker engine.** Every project now has a
  `packages.lock.json` (`Directory.Build.props`); CI and the Dockerfile restore with `--locked-mode`, so a package
  resolved differently from the lock file fails the build. `global.json` pins the SDK exactly (`10.0.402`, no roll
  forward); CI installs it from there and the Dockerfile's SDK image is the same version. Every base image is pinned as
  `image:tag@sha256:…`; Dependabot now covers all five projects (one nuget entry), the SDK (`dotnet-sdk`), the base
  images (`docker`), every compose file's images (`docker-compose`), the Helm chart's images (`helm`) and the actions.
  The image no longer installs `docker.io` (dockerd, containerd, runc): it takes the static docker CLI from a pinned
  `docker:<version>-cli` stage — inside the container Whiskers only runs `docker exec` for the local web terminal;
  everything else talks to the Docker API or runs `docker compose` on the managed host — and names `iptables`
  (tailscaled's netfilter) explicitly (local build 1.34 GB → 1.03 GB). New CI job `image-runtime`, on x64 and arm64
  runners, builds the image and runs `ImageRuntimeToolsTests` against it (tools present and starting, no engine, local
  `docker exec` terminal, `nsenter` host shell, a compose deployment through the production deploy script and host
  transport with cleanup, VPN runtime smoke without credentials, multi-arch base digests, boot to `/healthz`);
  `build-test` gets a 45-minute limit. `BuildReproducibilityTests` guards lock files, locked restores, digest pins and
  Dependabot coverage. The full profile stays root and privileged by design. Packages from the apt repositories
  (Ubuntu, Tailscale, PostgreSQL) are still installed at their current version. **Upgrade notes:** no application, configuration or database
  change. (1) Developers need exactly .NET SDK `10.0.402` (`global.json`); a newer SDK is refused until `global.json`
  is moved. (2) After changing a `PackageReference`, run `dotnet restore Whiskers.slnx --force-evaluate` and commit
  the changed lock files (also `Whiskers.Tests`'s). (3) Moving the SDK means moving `global.json`, the Dockerfile's
  SDK image and the lock files together (`deploy/README.md`, "Pinned build inputs"). (4) An image derived from
  Whiskers' that relied on `dockerd`/`containerd`/`runc` being present must install them itself; the `docker` CLI is
  still at `/usr/local/bin/docker`, now without the `docker` group the package created.

- R1-48: **A change that no decision covers is refused, not just noticed.** The Mutation Gateway's scope guard ran in
  report mode: a call to a marked service outside a gateway decision was logged and then ran. It now throws
  (`MutationScopeViolationException`) before the call runs, in production, and logs the attempt at Error. A decision
  covers its own capability and the nested operations listed explicitly in `MutationCompositions` (a deployment
  pulling its image and creating its container, onboarding issuing a certificate and saving the server, a workload
  start being a container start, a store writing its vault secrets, an nginx change reloading nginx, …) — nothing
  else, and no class is exempt as a whole: a `[MutationHandler]` now names the capability it runs under and its calls
  are checked against it. **Coverage:** raw host commands that change something go through the executor's new
  primitive `IHostCommandExecutor.ExecuteMutationAsync` (`execute_command`, `set_container_env`, `deploy_compose`,
  scheduled commands, cleanup and DB-backup retention, the container page's `.env` save and Artisan commands, UI
  compose deploys, webhook compose deploys), which runs only inside a decision whose execution is a raw host command
  (`MutationCompositions.RawHostCommands`) — an open configuration decision cannot run one; every other caller of
  the plain `ExecuteAsync`, in every namespace, is listed as a read or as the body of a marked operation of its own
  type. Server records (add/update/SSH key, remove), role writes (`SetRoleAsync`/`RemoveRoleAsync`, `config.roles`),
  the MCP tool switch (`SetToolEnabledAsync`), onboarding, webhook management, AI-trigger save, self-backup
  create/delete/prune/restore, Kubernetes workload start/stop/restart (the provider from `WorkloadProviderFactory` is
  guarded now), the AutoPatch runner (`container.update`), cloud power/snapshot with the Hetzner extensions and the
  Hostinger client, DNS provider writes and container notification preferences are marked; every other write-named
  interface method is listed with the reason it is no gateway operation. A nested operation must act on the server its
  decision was taken for (an `app.deploy` on server A cannot pull on server B); a call that omits its server is matched against
  the server its operation really routes to, declared once with `[OmittedServer]` (Docker and deployments: the
  configured default; volume backups and database access: Whiskers' own host `local`, read by the implementations from the
  same declaration); an unknown server, a missing or failing registry and an unresolvable decision server match nothing
  (a serverless operation is not matched). The omitted server is then passed to the operation as the id that was
  checked, so a default changed in between cannot redirect it; an empty or blank `serverId` is not an omission and is refused (`ArgumentException`) before any decision is looked at, also for the decided capability itself and for a decision without a server. **Detached work:** a closed scope
  authorizes nothing — work that escaped it (`Task.Run`, a timer, a fire-and-forget continuation) is refused once the
  decision ended, and a decision started there does not nest under the dead one; background work started from code
  that can run inside a decision suppresses the flow (test-enforced). The automatic end of a check pause is no longer
  a `checks.resume`: it removes only the expired pause it read (a newer pause stays). **First-run wizard:** it creates
  the first administrator through two narrow methods (`IWhitelistService.AddFirstAdminAsync`,
  `IRoleService.SetFirstAdminAsync`) that refuse as soon as any Admin exists — checked and written while the wizard holds
  the one write boundary every role and whitelist writer shares (`AccessControlWriteGate`), across both writes, so two
  submits at once create at most one admin and no other writer runs in between; the Admin is published only after it
  was saved — whose only caller is the wizard after the setup token was verified; the step is recorded in the audit log first (`setup.first_admin`) and nothing is written without that
  record. The whitelist entry is now written before the role (a crash in between leaves setup open). From then on the
  whitelist and roles change only through the gateway. **Local accounts (R1-49):** creating and deleting an account compose its role write (`account.create` / `account.delete` → `config.roles`); the host recovery CLI and the recovery-code page stay outside the gateway on their own authority (audited, pinned to their callers). **CVE scanners:** `apt-get update` and Trivy's image, throw-away
  container and cache volume are a designed exemption of the monitoring cycle (pinned). **Architecture test:**
  `docs/v1/mutation-bypass-baseline.txt` is removed and nothing is allowed by list; every capability is refused at
  run time outside a decision. `MutationGatewayCoverageTests` lists every write by surface (UI, MCP/agent,
  background, webhook, terminal) and pins who may call the primitive. A new boot test starts the app enforcing in each
  auth mode (disabled, local, OIDC) and completes the wizard. No database change.
  **Upgrade notes:** (1) `assess_update_risk` pulls the candidate image and is now **write** (new capability
  `image.assess_update`, was read): a read-only key or a Viewer can no longer call it; a stricter stored level stays.
  An assessment that could not be made is recorded as `Failed`. (2) The setup wizard refuses to complete when its
  audit record cannot be written (nothing is changed; try again once the database answers). (3) For code built
  against Whiskers' interfaces: `IBackupService` has three start-up methods (`CreatePreUpgradeBackupAsync`,
  `DeletePreUpgradeBackupAsync`, `BeginHostRestoreAsync`) used only by the upgrade guard and `--restore-backup`;
  `IRoleService.SetFirstAdminAsync` and `IWhitelistService.AddFirstAdminAsync` are new and take an
  `AccessControlWriteGate.Lease` (a new singleton that `RoleService`, `WhitelistService` and `SetupStateService` must share);
  `IMutationScopeMonitor.ResolveServer` is new (default: resolves nothing); a guarded operation with an omittable
  `serverId` must declare `[OmittedServer]` (test-enforced); `IHostingerService.DeleteSnapshotAsync`
  (never called) is removed; `IServerConfigService.AdoptCloudKeyRefAsync` has no default body any more;
  `WorkloadProviderFactory` needs an `IMutationScopeMonitor`; `CloudControlService` takes the Hetzner extensions only
  from their own registration; `[MutationHandler]` needs a capability. (4) A custom service or test that writes through
  a guarded service outside the gateway now fails with `MutationScopeViolationException` instead of a log line — run
  it through `IUiMutationRunner` / `IAutomationMutationRunner` / an MCP tool.
- R1-46: **The installer and the Helm chart install one pinned release and set up a vault key and the VPN mode
  explicitly.** **Installer** (`deploy/install.sh`): it installed `ghcr.io/lupusmalusdeviant/whiskers:latest`, so a
  re-run pulled whatever was newest, without a version, a vault key or a signature check. It now installs one released
  version — by default the release it belongs to (`WHISKERS_RELEASE`; the release pipeline refuses a tag whose version
  differs, and accepts only `vX.Y.Z` tags, so the `install.sh` of release X.Y.Z installs X.Y.Z), or `--version X.Y.Z` —
  and writes that exact image into the compose file. `--version latest` or a pre-release, an `--image` without a tag
  or digest, the tag `latest`, an image reference with other characters than a plain reference allows, a bind address
  that is not an IPv4 address or a well-formed `[IPv6]` address (groups checked, at most one `::`), a port outside 1–65535 and a data path with `:`, `#`, `$` (Compose would substitute it), quotes or line breaks
  are refused before anything is pulled or written. Before pulling it runs `cosign verify` when cosign is installed
  (`--verify auto`, default) and requires a signature from exactly the release workflow run of tag `vX.Y.Z` (an older
  signed image re-tagged as X.Y.Z does not pass); the digest the signature covers is pinned in the compose file
  (`…:X.Y.Z@sha256:…`), so the pull fetches the verified image even if the tag moved. A bad signature, or a verification
  that does not name exactly one digest, stops it with nothing changed; `--verify require` refuses to install without a
  verified signature, `--verify off` skips it (said on the console). A new install
  generates a vault key into `./whiskers/secrets/vault.key` (directory 0700, file 0600, never printed, never on a
  command line) and hands it to Whiskers as a secret file (`VAULT_KEY_FILE=/run/secrets/whiskers_vault_key`), so the
  vault and encrypted backups are on from the start; a re-run keeps the key and an empty key file stops it. The output
  says what to back up — the key and the data, apart from each other and off the host — and links the backup guide.
  A re-run with a newer release **upgrades**: only the `image:` line of the compose file the installer wrote changes,
  keeping the file's mode and owner (a copy of the previous file is kept as `docker-compose.yml.bak-<time>` with the same
  permissions; the output says it may contain secrets), the port and bind address are read from that file (`--port`,
  `--bind`, `--data` on a re-run are ignored with a warning), the wait for `/healthz` covers the pre-upgrade backup and
  migrations (about 5 minutes), and a **downgrade is refused** (going back is the previous release on the pre-upgrade
  backup). The installed release is the newest of the compose file's tag, the version the running Whiskers recorded at
  its last start (`/app/data/.last-started-version`) and the image's version label; when none tells (an install of
  `:latest` whose container is not running or predates R1-34), the change is refused unless `--allow-unknown-current`
  is given — which covers only an unknown *installed* release, never an unknown target or a known downgrade. The target release is its `X.Y.Z` tag or, for a digest-only `--image`, the release named by its verified signature (a tag and a signature naming different releases stop the install); an unverified digest target is refused on an existing install. The guard runs before the "already on this image" shortcut, so a re-run on data a newer release has migrated is refused before `pull`/`up`. **Helm chart:** the app container now always gets `VPN_PROVIDER=none` and `Vpn__Provider=none`.
  Before, neither was set, so `entrypoint.sh` took the legacy path and started `tailscaled` as uid 10001 on a read-only
  root file system, where it fails (measured on the 0.15.0 image: it starts, logs errors and costs the start two
  seconds); now nobody brings a VPN up in the pod, and `extraEnv` cannot override these names (also behind `DOTNET_` /
  `ASPNETCORE_`, in any letter case, and spelled with `:` such as `Vpn:Provider`, which .NET reads as the same key),
  `TAILSCALE_AUTHKEY` or `VPN_FORCE_LEGACY_TAILSCALE`. The Tailscale sidecar is pinned
  (`tailscale/tailscale:v1.104.4` plus its index digest instead of `:latest`; an unpinned or `latest` image is refused)
  and keeps its node identity on its own PVC (`tailscaleSidecar.state.*`, 128Mi) with `TS_AUTH_ONCE=true`. Its auth key is mounted from `authKeySecret` as a file (`/run/secrets/tailscale/authKey`, mode 0440, group 10001 via `fsGroup`) and passed as `TS_AUTHKEY=file:<path>`: the image hands `TS_AUTHKEY` on to `tailscale up --authkey=…`, so the literal key from a `secretKeyRef` was on that command line, readable in the process list; the CLI reads the `file:` value itself. The chart README creates the vault Secret with `--from-file` from a private (umask 077) key file instead of `--from-literal` (key in `kubectl`'s argv), with backup and cleanup steps. The sidecar
  also gets `TS_KUBE_SECRET=""`: inside Kubernetes the Tailscale image stores its state in a Secret named `tailscale`
  by default (upstream `cmd/containerboot`, read 2026-11-03), which needs RBAC the chart never granted — the node
  state never went to the old `emptyDir` at `TS_STATE_DIR`. The chart refuses to render (instead of producing a pod stuck in
  `CreateContainerConfigError`) for `database.provider=postgres` without `database.existingSecret` or
  `database.connectionString`, a provider other than `sqlite`/`postgres`, and `image.tag=latest`. `NOTES.txt` is
  English and names the setup token, the backup of key and data, and the upgrade path; the chart README documents
  installing a pinned chart version with the vault key in a Secret of your own, verifying the image signature, and
  upgrading. Tests: `InstallerContractTests` (runs the real installer under bash with fake `docker`/`curl`/`cosign`)
  and `HelmDeploymentTests` (chart structure, the real `entrypoint.sh` under the chart's environment, and `helm
  template` renders — these need helm and fail on CI without it). `chart-ci.yml`'s negative checks (`! grep …`) did
  not fail under `set -e`; they now do, and a new chart-ci step resolves the sidecar's tag in the registry and fails
  when it is not the pinned digest. The chart README lists a kind check with the sidecar enabled (not automated) and
  notes that `helm uninstall` deletes both PVCs. No application or database change. **Upgrade notes:** (1)
  **installer users:** an install made by an older installer (`:latest`) is moved to the pinned release on the next run
  of a new installer only when the running Whiskers tells its release (0.15.0 and older record none: then confirm it is
  older — the sidebar shows the version — and add `--allow-unknown-current`); it gets no vault key automatically — the installer warns and `deploy/README.md` → *Installer* says how to
  add one. To upgrade later run the newer release's `install.sh` or `install.sh --version X.Y.Z`; there is no
  `latest` any more. (2) **Helm users:** the sidecar's node state moves to a new PVC, so the first start after the
  upgrade logs the node in once more with `tailscaleSidecar.authKeySecret` (a reusable or fresh key; afterwards the
  node keeps its identity — remove the old node in the Tailscale admin console). A `tailscaleSidecar.image` override
  that is unpinned or `latest`, an `extraEnv` entry naming the VPN mode, and `postgres` without a connection-string
  Secret now stop `helm upgrade` with a message instead of deploying. With `helm upgrade --reuse-values` the new keys
  (`tailscaleSidecar.state`) are missing: use your values file or `--reset-then-reuse-values`. The sidecar now reads
  its auth key from a mounted file: the Secret named by `tailscaleSidecar.authKeySecret` must keep the key `authKey`
  (unchanged); nothing else to do.

- R1-27: **Google and OIDC sign-ins are bound to the provider's account, not to an email address.** A federated user
  is now identified by issuer and subject. The email address is used once per provider: to bind a new subject to an
  address an Admin configured (a role or a whitelist entry), automatically only when the provider is trusted and says
  `email_verified=true` for the standard `email` claim — Google is trusted by default, a generic OIDC provider is **not**
  (`Oidc:TrustEmailVerified=false`; `GoogleAuth:TrustEmailVerified=true`). Everything else — unverified or untrusted, a
  non-standard `Oidc:EmailClaim`, the same address from a second provider, a new subject for an already bound address
  (IdP account re-created, issuer changed) — becomes a **pending request**; the sign-in is refused with the usual
  message, which now adds that an administrator can confirm it under *Settings → Sign-in identities*. Admins confirm or
  remove bindings there (new audited capabilities `identity.binding_confirm`, `identity.binding_remove`; the last way the
  last Admin can sign in cannot be removed). The host operator confirms the newest pending request of an address with
  `docker compose exec serverwatch dotnet Whiskers.dll --confirm-identity-binding <email> [--issuer <issuer>]` (exit
  `0` bound, `2` usage, `3` nothing pending, `4` several issuers pending, `1` nothing changed; its audit row is written
  first); it shares the file with the running instance under a lock, and the running instance sees the binding at once —
  no restart. **No Google/OIDC sign-in is admitted before an Admin exists** — before, every Google/OIDC account in the
  world was admitted as Viewer until a role existed and reached `/mcp` and the hubs. Removing a binding ends that IdP
  account's sessions but not the access: while the address is configured, the next sign-in binds again (automatically
  or after confirmation). When an address stops being an entry (its role and its whitelist listing are gone), its
  bindings go in the same write, so a recycled address never revives the old account. New audit actions
  `auth.identity_bound`, `auth.identity_pending`; refused sign-ins of addresses that are not configured are recorded as
  `anonymous`. The application log no longer names the account of any `auth.*` audit row (written, failed or dropped —
  the audit row itself keeps it), shows no details of an identity row and keeps every value of such a line on one
  line, and no R1-27 log line carries an address or a subject. The readiness check
  `identity-bindings` reports an unavailable store: unreadable, incomplete, missing while its `.bak` exists (the `.bak`
  is never taken over by itself), after a failed save, a start-up prune that did not succeed, or a repair of the file
  (readable again with other content), until it is read and pruned again; every federated sign-in and session is then
  refused and local sign-in keeps working. Every role or whitelist change that adds or removes an address — the setup
  wizard's included — prunes the bindings of addresses that are no entries first, and is refused while that cannot be
  done. Do not set `DOTNET_SYSTEM_IO_DISABLEFILELOCKING`: it switches off the lock the instance and the CLI share. An
  Admin with a local password **or an active binding** counts as one who can sign in (replaces "seen signing in");
  removing a binding and deleting a local account check that rule one at a time.
  **Upgrade notes:** nothing on disk changes; `identity-bindings.json` appears in the data directory with the first
  binding and is part of the self-backup. **Every Google/OIDC browser session ends at its next request** (open pages
  within 30 s); local sessions continue. A Google user signs in again and is bound. **An OIDC user (Authentik included)
  is pending once:** run the `--confirm-identity-binding` command above (or let a local Admin confirm in Settings), then
  sign in again; only if your IdP emits `email_verified` honestly and users cannot change their own address, set
  `Oidc__TrustEmailVerified=true` to bind automatically. An instance whose setup is not finished admits no federated
  sign-in until the wizard has run. Changing the OIDC Authority (issuer) later puts every OIDC user back to pending.
  Restoring a backup from before this release restores no bindings. MCP keys are unaffected.
  See [docs/operations/identity-bindings.md](docs/operations/identity-bindings.md).

- R1-49: **Local accounts get a complete lifecycle.** Everyone with a local account changes their own password under
  *My account* (person icon in the top bar; the current password is required and a wrong one counts towards the
  lockout). Admins add, reset and delete local accounts under *Settings → Local accounts*. Whiskers never hands out a
  temporary password: adding an account and resetting a password produce a **one-time recovery code** (shown once,
  valid 24 hours, cannot sign in by itself) with which the account's owner sets a password at `/account/recover`
  (linked from the sign-in page); a reset removes the old password at once. Every credential change — own change,
  reset, code redeemed, account deleted — **ends every session of the account** (new security stamp, R1-26), the
  changer's own included. If no Admin can sign in any more, the host operator issues a code inside the running
  container: `docker compose exec serverwatch dotnet Whiskers.dll --recover-local-account <email>` (existing accounts
  only, printed to the terminal only, never logged; its audit row is written first and without it nothing changes).
  The last Admin who can sign in cannot be deleted (also when two Admins race; an Admin still waiting for a code does
  not count), and nobody deletes or resets their own account there. Deleting an account also removes its explicit
  role. New audited capabilities `account.password_change` (read level, own account only — also allowed while the kill
  switch is on), `account.create`, `account.password_reset`, `account.delete` (Admin); new audit actions
  `auth.recovery_issued`, `auth.recovery_redeemed`, `auth.recovery_refused`. `/account/recover` is limited to 10
  attempts per client per minute (an IPv4 address or an IPv6 /64, taken from the page's live connection — WebSockets
  or long polling — after the forwarded headers) and 10 per account per 15 minutes (throttled attempts check nothing
  and are audited once per client and minute; at most 4096 clients and accounts are remembered, beyond that new ones
  share one bucket); every answer takes at least one second; trying codes for an account that has none does not lock
  it; a successful redemption is audited before the password is set. Adding, resetting and deleting accounts, and
  redeeming a code, are refused while `AUTH_DISABLED` is set. Passwords and codes use ASP.NET Identity's
  hashing; no password, hash or code reaches a log or the audit trail. **MFA posture:** local accounts have **no second
  factor** in 1.0 — for production, sign in through your identity provider (OIDC) with MFA and keep local accounts as a
  break-glass fallback ([docs/operations/local-accounts.md](docs/operations/local-accounts.md)).
  **Upgrade notes:** no database change (codes live in the existing Identity token table, which the SQLite →
  PostgreSQL migrator copies) and no configuration change; existing passwords keep working. The password policy is
  unchanged (12 characters, upper- and lower-case letter, digit and symbol; 5 failures lock for 15 minutes).
  `Auth__LocalLogin__Enabled=false` still only hides the password form — delete local accounts you do not need.
  Behind a reverse proxy, narrow `ForwardedHeaders:TrustedNetworks` to the proxy's address: the default trusts all
  private networks, so a LAN client could pose as many clients to the recovery page's (and the sign-in's) per-client
  limit; a proxy outside the list makes all visitors share one limit.
- R1-43: **The VPN bring-up is decided, recorded and owned, and its key never reaches a command line.** With
  `VPN_PROVIDER=tailscale` or `netbird`, Whiskers started the VPN daemon and joined the mesh at every start on no
  one's authority and without an audit record. That bring-up now runs through the same gateway as every other change
  (`vpn.bootstrap`, Admin): unattended, on the authority of the admin who takes **"VPN bring-up"** over in **Settings →
  Automation owners**, re-checked at every start — never as an admin of its own and never with an implied confirmation.
  Every start is one audit event (executed, failed, or refused — also when it was refused for having no owner); an owner
  who can no longer sign in or is no Admin any more is refused and the bring-up is suspended until an admin takes it
  over again; the kill switch refuses it. The result of this start is shown in that panel. **The key:** `tailscale up`
  now reads it from `--auth-key=file:<path>` and NetBird from `--setup-key-file=<path>` — a file of mode 0600 in a
  private directory, deleted as soon as the call returns. The earlier hand-over through `TS_AUTHKEY` did not log
  Tailscale in at all: the `tailscale` CLI does not read that variable (upstream `cmd/tailscale/cli/up.go`, read
  2026-11-03 — only Tailscale's own container start script `containerboot` does). NetBird needs a CLI that has
  `--setup-key-file` (an older one fails with an unknown-flag error). CLI
  errors are logged and recorded with the key removed, also when the tool echoes part of it. Every argument is passed as
  a separate argv element, and the mesh hostname and the login server / management URL are checked before anything runs
  (a host name; an absolute http(s) URL without user info) — an invalid value fails the bring-up with "Invalid VPN
  hostname" / "Invalid … URL". **No key in a child's environment:** docker-compose passes the keys as
  `Vpn__Tailscale__AuthKey` / `Vpn__Netbird__SetupKey` in every mode, so every process Whiskers started — tailscaled
  for as long as it runs, ssh, docker, nsenter, shells — inherited them, readable in `/proc/<pid>/environ` under
  `pid: host`. Now `entrypoint.sh` moves the active provider's key into a 0600 file in memory (`/dev/shm`, or
  `WHISKERS_SECRET_DIR`) and hands Whiskers only its path (`Vpn__Tailscale__AuthKeyFile` / `Vpn__Netbird__SetupKeyFile`),
  and drops every key variable — also behind the `ASPNETCORE_` / `DOTNET_` prefixes the configuration reads — before
  it starts its first process, so neither the app nor the long-lived `tailscaled` of the legacy path inherits one. It
  takes the provider and the keys in the order Whiskers' configuration lets them win (the bare name, then `DOTNET_`,
  then `ASPNETCORE_`; `VPN_PROVIDER` only when no `Vpn__Provider` is set, and then it is handed to Whiskers as
  `Vpn__Provider` so both decide alike), and warns on the console when it drops a key no provider will read — expected
  on a compose start with `VPN_PROVIDER` unset and `TAILSCALE_AUTHKEY` in `.env` (compose passes it as
  `Vpn__Tailscale__AuthKey`, which only the in-app provider reads) — and when `VPN_PROVIDER` and `Vpn__Provider` differ.
  **Exactly one side brings the VPN up:** the in-app provider when Whiskers' provider is `tailscale`/`netbird`; the
  legacy entrypoint bring-up only when `VPN_PROVIDER` is unset and Whiskers' provider is not `tailscale`/`netbird`
  (before, `Vpn__Provider=tailscale` without `VPN_PROVIDER` — a `docker run`/k8s shape — ran both, the legacy
  `tailscale up` ungoverned; and `VPN_PROVIDER=tailscale` without `Vpn__Provider` ran neither); nobody when
  `VPN_PROVIDER` is set and the provider is `none`. Compose is unchanged (it sets `Vpn__Provider=${VPN_PROVIDER:-none}`).
  A provider given on Whiskers' command line or in the data directory's JSON layers is not visible to the script.
  Whiskers drops them (also prefixed) from its own environment after
  reading its configuration and removes them from the environment of every process it starts. Only the exact spelling
  is moved by the script: a name in other letter case still reaches Whiskers' own start environment. **New, preferred:**
  `TAILSCALE_AUTHKEY_FILE` / `NETBIRD_SETUP_KEY_FILE` (`Vpn__Tailscale__AuthKeyFile` / `Vpn__Netbird__SetupKeyFile`) —
  a file only Whiskers can read, mode 0600 or 0400, e.g. a Docker secret; it wins over the value, is read once at start,
  a wider mode is logged as a warning and an unreadable or empty file fails the bring-up. The value variant keeps
  working, but stays visible in `docker inspect`, and when Whiskers is started without `entrypoint.sh` its own
  `/proc/<pid>/environ` keeps showing it. A key file left by a crash or a kill during `tailscale up` is removed at the
  next start; key files are no longer flushed to disk. **Legacy entrypoint** (`VPN_PROVIDER` unset; it reads
  `TAILSCALE_AUTHKEY`, which docker-compose does not set — this applies to `docker run -e TAILSCALE_AUTHKEY=…`):
  `entrypoint.sh` no longer runs `tailscale up --authkey="$TAILSCALE_AUTHKEY"` (readable by every process on the host
  under `pid: host`); it writes the key with the shell's `printf` builtin into a 0600 file and passes
  `--auth-key=file:…`, removes the file right after — also when the script ends early — passes a stop signal during the
  call on (exit 143) instead of starting Whiskers, and prints `ERROR: tailscale up failed …` instead of swallowing a
  failure. The data directory there follows `WHISKERS_DATA_DIR` (default `/app/data`, as before). This path is still the
  host operator's container start and is not recorded by the gateway. **Upgrade:** when Whiskers' provider is
  `tailscale` or `netbird` — `Vpn__Provider` (also `DOTNET_Vpn__Provider` / `ASPNETCORE_Vpn__Provider`) or, without it,
  `VPN_PROVIDER` — **the VPN is not brought up after the upgrade until an admin takes "VPN bring-up" over** in Settings →
  Automation owners and Whiskers is restarted (the panel says "not run, no owner"; the log says why). If the Whiskers
  UI is reachable only over that mesh, open it over the host's LAN address for the takeover, or run the mesh on the
  host (`VPN_PROVIDER=none`) until then. This now includes a container started with `Vpn__Provider=tailscale` (bare or
  prefixed) and `TAILSCALE_AUTHKEY` but no `VPN_PROVIDER`, which the legacy entrypoint used to bring up: the legacy
  path is skipped there and `TAILSCALE_AUTHKEY` is no longer used (the entrypoint says so by name) — move the key to
  `Vpn__Tailscale__AuthKey`, better `Vpn__Tailscale__AuthKeyFile` (`TAILSCALE_AUTHKEY_FILE` in compose). A Tailscale node that relied on
  `TS_AUTHKEY` and never logged in now does so with the key. New optional `VPN_FAIL_STARTUP_ON_ERROR`
  (`Vpn__FailStartupOnError`, default `false`): `true` stops a start whose bring-up does not succeed — set it only after
  the takeover, or every start stops. **Recovery** when it locks you out (the owner later lost the Admin role or the
  sign-in, or `automation-owners.json` became unreadable, and the container restarts in a loop): set
  `VPN_FAIL_STARTUP_ON_ERROR=false`, restart, take "VPN bring-up" over again (or fix the VPN settings), restart, set it
  back to `true`; the error message names these steps. Recommended: move the keys to `TAILSCALE_AUTHKEY_FILE` /
  `NETBIRD_SETUP_KEY_FILE`. `VPN_PROVIDER` unset or `none`: nothing to do. Recovery: an unreadable
  `automation-owners.json` now also blocks the VPN bring-up until an admin takes it over. No database change.

- R1-26: **Revoked sessions and keys stop acting.** Deleting a local account, changing its security stamp, removing a
  user from the whitelist or lowering their role no longer waits for the 24-hour cookie to expire. The cookie is
  re-checked on **every request** (the synthetic identities as before, the local account by id with the same email and
  the security stamp it was issued with, the whitelist); a check that fails — also one that cannot read the account
  store — **rejects the cookie and removes it** (fail closed; before, any error let the request through and a cookie
  without an email was not checked). The role is not part of the cookie and is read per request, so a lowered role is
  in force on the next request. A Blazor circuit checks its session when it connects and every 30 seconds; a revoked
  session, or a role lower than the highest one the circuit has held (also after a promotion and a demotion), turns
  the circuit anonymous (the sign-in page is
  shown, a page no longer acts on the role it cached); a raised role keeps it. Every UI mutation re-checks the session
  through the current-user resolver and a revoked one is refused by the Mutation Gateway and recorded, whatever role
  the page cached; so does every role lookup a page makes (no reuse) and every approval decision (a revoked session
  decides nothing, a lowered role decides at that role). An open `/hubs/containers` connection re-checks its session on
  every call and is swept every 15 seconds (it receives server broadcasts without calling anything), and is closed and
  audited (surface `hub`) when it
  is revoked. Terminals keep ending within 15 seconds (R1-17). A running agent session (`instruct_agent`, the agent
  chat) re-reads the key or user that started it before **every** tool call: a disabled or deleted key, or a user
  whose account was deleted or who may no longer sign in, stops the run at its next call — the call already running
  finishes; a lowered key level or narrowed tool list applies to the next call. Every revocation is audited as
  `auth.session_revoked` (surface `cookie`, `circuit` or `hub`, the reason only — never a cookie, claim value or stamp), at
  most once per minute for the same account, surface and reason (a replayed cookie cannot flood the audit queue). A
  local account without a security stamp gets one at sign-in (a sign-in that cannot store it is refused,
  `no_security_stamp`).
  **Upgrade notes:** (1) **Users signed in with a local password are signed out once** after the upgrade: their cookie
  was issued without the security stamp it is now tied to; they sign in again. Google/OIDC sessions stay signed in.
  (2) With a database that cannot be read, local-password sessions are refused until it is back (previously they went
  on). (3) No configuration change, no database change. Password change/reset and a "sign out everywhere" action are
  R1-49 and use this mechanism (a new security stamp ends every session of the account).

- R1-41/R1-53/R1-54/R1-55/R1-56 follow-up: **open alerts are closed once, also across a restart and a removed
  server.** A `metrics_gap` that was open when Whiskers stopped is now kept in `alert-breaches.json` (a new optional
  `Gaps` list; a file written before is still read): after the restart it is not raised a second time while readings
  are still missing, and it is closed once with `metrics_gap_recovered` when they are back. The open `host_api_slow`,
  `host_cpu_anomaly` and `host_cpu_baseline_drifted` alerts of a server removed from the configuration are closed
  (unconfirmed), like its other alerts; a container alert of such a server now says that its server was removed instead
  of "no longer running". The generic webhook payload and the AI-trigger event data carry a new field
  `recoveryUnconfirmed` (`true` for a `<kind>_recovered` that no reading confirmed, `false` otherwise). A settings save
  with a loop interval out of range is refused before the gateway runs, so it no longer leaves a failed `config.save` in
  the audit trail. The AutoUpdate failure notification now ends with the stop (it is kept for redelivery like every
  critical event); a non-critical delivery a stop interrupts leaves a log line. A fleet container listing that fails for
  another reason after its deadline names that reason instead of "Timed out". Tests now cover the e-mail channel's TLS
  path end to end (no fallback to plain text when STARTTLS is missing, TLS from the first byte on port 465, an untrusted
  certificate ends the send, the TLS name is the configured host) and count the roll-ups after a clock set back.

- R1-56: **Threshold alerts say a breach once and say when it is over; quiet hours hold the channels back.** `high_cpu`,
  `high_memory` and `high_disk` now follow the host rules' duration model: a breach is raised once, after the value
  stayed at or above the threshold for `SustainedMinutes` of **elapsed time** (no longer a sample count that assumed a
  30-second interval — a changed collection interval or a missed sample no longer changes what "3 minutes" means), is
  never repeated while it lasts, and is closed by a new `high_cpu_recovered` / `high_memory_recovered` /
  `high_disk_recovered` once the value stayed five minutes below the threshold minus 5 points (a value hovering just
  under the threshold neither closes nor reopens it). A series without readings for longer than three collection
  intervals (at least three minutes) — a container whose stats call fails, a host whose node_exporter is down, a `df`
  that stopped answering, a server whose container listing keeps failing (one gap for its containers) — is no longer
  only a log warning: it raises one `metrics_gap` (naming the alerts it leaves unconfirmed) and a
  `metrics_gap_recovered` when readings are back, and an open breach starts again from the new readings instead of
  being bridged — a raise such a reset ends (a series gap, a paused server, a failed listing, alerts switched off and on,
  a held-up cycle, a disabled server enabled again) is always closed with its `*_recovered`, marked as not confirmed —
  titled "<alert> closed — recovery not confirmed", severity Warning, never "back to normal" — and a breach that goes on
  is raised again after `SustainedMinutes`, without waiting for `CooldownMinutes`. State that is dropped is closed the
  same way: a container that stops, is removed or recreated, and a server removed from the configuration, have their
  open alerts and their open `metrics_gap` closed. A collection cycle that was itself held up past the tolerance is a
  log line (its open alerts are closed by the rule), not one message per series; a paused server and a `df` that never answered since the start raise no gap. A listing
  that fails for a cycle no longer drops a container's breach. Open threshold alerts survive a restart of Whiskers
  (`alert-breaches.json`): a breach raised before the restart is closed with its recovery afterwards and not raised a
  second time. Raises, recoveries, gaps and the quiet-hours summary bypass the per-channel
  throttle, like reachability and certificate events. **Quiet hours** (Settings → *Quiet hours*, Admin only, a gated,
  audited configuration write): daily windows in an explicit IANA time zone (`Europe/Berlin`, `UTC`; a Windows zone name
  or an unknown zone is refused on save), global or for chosen event types, crossing midnight, on the local wall clock
  across DST changes. They hold notifications back from the channels only — the in-app feed and the alert history keep
  recording, AI triggers are dispatched as always — and record what they held (at most 20 events per rule, window and
  target, plus a count of the rest) for one summary per window, delivered when the window ends (or earlier, when the rule
  is changed, switched off or removed) through the notification outbox. Never held: suspended or refused automation,
  interrupted operations, a disabled webhook, an approval request, the paused-checks reminder, a suspended or resumed
  log scan, stalled or resumed monitoring, a failed update, and a CVE batch holding a Critical finding (CVE notifications
  now carry the batch's highest severity as a field). A muted container stays muted — only the administrative events
  override the mute, as before. Event types now come from one registry (`NotificationEventTypes`); a type it does not
  know is always delivered.
  **Upgrade notes:** (1) **Behaviour change** for `high_cpu`, `high_memory` and `high_disk`: they no longer re-fire every
  `CooldownMinutes` while a breach lasts — expect one alert and one recovery per breach. `MetricAlert:CooldownMinutes`
  now means the least gap between the end of one breach and the alert for the next breach of the same container or
  disk (a breach that becomes reportable sooner is raised once that gap has passed, if it is still going on); it still is
  the repeat interval of `metric_anomaly`, which is a point-in-time outlier and unchanged. Filters, AI triggers or channel
  rules keyed on the old repetition should listen for the new `*_recovered` and `metrics_gap` types instead. (2) Host
  CPU, memory and unexplained-load alerts reset an open breach after the same gap tolerance instead of bridging any gap.
  (3) The alert texts of the container and disk alerts are English now. (4) The migration `QuietHoursSuppressions` only
  adds one table; `--migrate-to-postgres` copies it. (5) No quiet hours exist after the upgrade — nothing is held until
  an Admin adds a rule. (6) A summary is created exactly once and delivered at least once: if Whiskers stops after a
  channel accepted a summary but before the outbox recorded that, the summary is sent again after the restart (Slack,
  Discord, ntfy, Telegram, e-mail and generic webhooks cannot deduplicate it; Matrix does, by its transaction id).
  `notification-outbox.json` now also remembers the keys of summaries for seven days; an older release reading the file
  ignores them. A window that is in effect again after its summary (its end moved later) gets a second summary of what
  it held since. (7) The new file `alert-breaches.json` in the data directory holds the open threshold alerts; it is
  local to the install and not part of a self-backup. Open API-latency and rolling-baseline findings are not kept: a
  restart during one of them raises it again if it goes on, as before. (8) A container that stops or is removed during
  a breach gets its `*_recovered` (unconfirmed, "no longer running"); the health monitor reports the stop as before. (9) A host whose `df` has not answered
  since the start (no command channel) gets one log line instead of a `metrics_gap`; one with a kept disk breach counts
  as read before. (10) The open alerts of a server that was removed from the configuration are closed (unconfirmed) and
  dropped; a disabled server keeps them, containers included, and closes them by the gap rule once it is enabled again.
  (11) A `<kind>_recovered` now carries `RecoveryUnconfirmed`; the notifications log calls the type "closed"
  ("beendet") instead of "recovered", since a close is not always a recovery. The recovery of an alert resumed across a restart says so (its duration includes the downtime).

- R1-55: **Metric history keeps its peaks and goes back 30 days and further.** Container and server samples are no
  longer only kept raw and deleted after one retention window: closed windows roll up into three tiers — 10 minutes,
  1 hour and 8 hours — each keeping the sample-weighted average, the true maximum and the sample count per series.
  A history reads the coarse tiers for its old part and the finer tiers and the raw samples for the recent part, so
  it has no gaps, ends with the last minutes, and a 2-minute spike still shows as the peak of a 30-day chart (it used
  to be averaged away). The Dashboard and container charts draw the peak as a second line and offer **30 days**; the
  `get_server_metrics` / `get_container_metrics` MCP tools accept `30d` and their `max` is now the true maximum (their
  `avg` is weighted by sample count). Each tier has its own retention and the alert history one of its own (it used
  to share the raw retention): raw 7 days (as before), 10-minute windows 30 days, 1-hour windows 180 days, 8-hour
  windows 730 days, alert history 30 days. Nothing is deleted before the next coarser tier has committed the windows
  built from it — also while that roll-up fails, and across a restart. Retention is now editable under Settings →
  *Metric retention* (Admin only, a gated, audited configuration write); a value set by the environment
  (`Metrics__RetentionDays`, `Metrics__Rollup10mRetentionDays`, `Metrics__Rollup1hRetentionDays`,
  `Metrics__Rollup8hRetentionDays`, `Metrics__AlertHistoryRetentionDays`) or `appsettings.json` wins and is shown
  read-only. The collector's retention no longer stops when `app-settings.json` holds a `Metrics` value that does not
  bind, and a first read that fails starts the loop on the defaults instead of not at all.
  **Upgrade notes:** (1) the migration `MetricRollups` only adds three tables (`ContainerMetricRollups`,
  `ServerMetricRollups`, `MetricRollupStates`); the existing raw samples stay as they are and readable. In the first
  minutes after the start they are rolled up once, a few days per collector cycle; until that is done a long chart
  still reads them raw. (2) The alert history is now kept 30 days instead of the raw retention (7 by default) — set
  `Metrics__AlertHistoryRetentionDays` to keep the old behaviour. (3) The shipped `docker-compose*.yml` set
  `Metrics__RetentionDays` (default 7), so on those installations the raw retention is read-only in the panel; remove
  the line (or `METRICS_RETENTIONDAYS`) to edit it there. (4) `--migrate-to-postgres` copies the tiers; a source from
  before this release has none and the target rolls its raw samples up on its first start. (5) If the host clock is
  set back after windows were rolled up, the roll-up pauses (one warning in the log) until the clock passes its
  watermark again, nothing below it is pruned meanwhile, and the charts read the finer tiers and raw samples for that
  stretch. Samples taken in that stretch are not rolled up later: they are kept for the raw retention, then only the
  windows from before the correction remain in the long view. Whiskers never rewinds by itself; to end a long pause,
  back up, then in one transaction delete that kind's rows of `MetricRollupStates` (`Id` starting `container/` or
  `server/`) and its tier rows (`ContainerMetricRollups` / `ServerMetricRollups`) with `WindowStart` at or after the
  current time floored to 10 minutes — the next cycle rebuilds the tiers from the raw samples (see the Metrics README).
  A deleted window whose raw samples are already pruned is gone for good, and raw samples still stamped with the old
  clock are merged into the rebuilt windows (their counts overlap). On PostgreSQL quote the identifiers
  (`"ContainerMetricRollups"`, `"MetricRollupStates"`).

- R1-41/R1-54 follow-up: **a stop at the wrong moment no longer rewrites what happened.** An e-mail the SMTP server
  already took and partially refused now stays a partial refusal, with who was reached, when Whiskers stops while
  saying goodbye — before, the stop turned it into a cancellation and the retry mailed the reached recipients again.
  An auto-update whose container was replaced and verified is recorded as done when Whiskers stops during the health
  check that follows, not as a failure. A retry that ran out of time in Whiskers' own queue no longer counts against the server's circuit,
  and a first attempt that ran out of time there is no longer mistaken for a broken tunnel and sent again.
  The CVE monitor's start log shows the effective interval and no longer reads raw options outside its recovering loop,
  and the background-loop ratchet now also flags work that runs in a loop outside the catch that recovers a cycle.

- R1-53: **One request can be followed from the client to the audit row, and the logs can be JSON.** Every HTTP answer
  now carries an `X-Request-Id`: the client's own when it is safe (1 to 64 characters of `A-Z a-z 0-9 . _ -`), else
  the W3C trace id of an incoming `traceparent`, else a new one — an unsafe value is replaced, never echoed or logged.
  Whether an id is accepted never depends on Whiskers' secrets (that would let anyone probe them before signing in); a
  secret sent as the id goes back only to its sender and is written as `***` in every log line, like any other value
  the log redaction catches. The id is the log scope `HttpRequestId`. The Mutation Gateway now writes
  one line per decision (`Operation … decided …: decision …, request …`), one per refusal or wait for approval and one
  per outcome (`… Executed: verification …, approval …, audit …, request …`; `Warning` when it failed), and
  puts the operation id around the whole execution as the log scope `OperationId`, so the lines of the services it
  calls carry it too. The ids pass the log redaction (R1-25) unchanged; nothing else of the request (headers, path,
  query, body) is logged for this. Structured logs need no new setting: `Logging__Console__FormatterName=json` and
  `Logging__Console__FormatterOptions__IncludeScopes=true` write one redacted JSON object per line with the scopes.
  `/metrics` adds the audit trail's completeness, so far only the `audit` readiness check:
  `whiskers_self_audit_events_pending`, `whiskers_self_audit_intents_unsettled` and
  `whiskers_self_audit_legacy_write_failed`. The new guide `docs/operations/observability.md` lists where the logs
  are, how to follow a request, and the loop-stall, operation-incomplete and audit-failure signals with example
  alert rules. The correlation middleware is registered first in the pipeline; the forwarded-headers and
  authentication order behind it is unchanged. No database change. **Upgrade notes:** at the default `Information`
  level the gateway now logs two to three lines per mutation; raise `Logging__LogLevel__Whiskers.Services.Mutations`
  to `Warning` to keep only failed outcomes. An `X-Request-Id` a reverse proxy already sets is taken over when it
  is safe.

- R1-54: **Notifications no longer reach into Whiskers' own network unless an Admin allows it.** Every notification
  channel (Mattermost, Matrix, Telegram, ntfy, Discord, Slack, the outgoing webhook) and the e-mail channel's SMTP host
  now go through one outbound guard: it resolves the name once, refuses it when any answer is a loopback, link-local
  (the cloud metadata address `169.254.169.254` included), private (RFC 1918, which covers Docker's networks),
  CGNAT/Tailscale, unique-local, site-local, multicast, broadcast or otherwise reserved address — also in disguise
  (`::ffff:127.0.0.1`, `2130706433`, `0177.0.0.1`, NAT64, 6to4, a zone such as `fe80::1%eth0`; Teredo and local-use
  NAT64 always) — and connects to exactly the address it checked, so a name that changes its answer in between cannot
  slip through. Redirects are no longer followed (a 3xx answer is a failed delivery) and proxies are no longer used.
  The ranges are fixed; an Admin can add exact exceptions under Settings → *Notification targets*: an IP address and
  port, or a host name, port and the exact addresses it may resolve to (no ranges, no wildcards). Each change is a
  gated, audited configuration write and takes effect from the next delivery on — removing one also ends the open
  connection it allowed. A refused delivery is logged and counted as a failed notification with its reason (host,
  port and refused address, never the URL's path or query) and is neither retried nor kept for redelivery. The e-mail
  channel now uses MailKit (implicit TLS on port 465, which did not work before, and required STARTTLS on every other
  port when *SSL/TLS* is on, as before). The server certificate is checked against the configured name as before;
  its revocation is not checked, as before (a relay with a private CA and no CRL keeps working).
  **Upgrade notes:** (1) a channel inside your network — a self-hosted ntfy, Mattermost or Matrix, an SMTP relay on the
  LAN or on Tailscale — is **refused from the first start after the upgrade until an Admin adds an exception** for it.
  The start-up log and the *Notification targets* panel list every configured channel the guard refuses, with the
  address it refuses (a host that does not resolve is listed as unresolved); check them right after upgrading. (2) An
  installation that reached Slack, Telegram or another channel **only through an egress proxy** (`HTTPS_PROXY`) loses
  that path: such a delivery now fails with its own reason ("an egress proxy is configured for it, but notification
  channels do not use a proxy"); allow the direct connection. (3) A stored notification kept for redelivery before the
  upgrade whose target is now refused is dropped (logged and counted).

- R1-45: **A release can only ship what was tested and scanned, and never replaces a published version.** Every
  GitHub Action in the CI, chart and release workflows is pinned to a full commit SHA, and each job holds only the
  permissions it needs. A release now runs the full test suite on the tag commit first, builds amd64 and arm64
  once each, scans **both** with Trivy by digest and publishes exactly those digests (verified, then cosign-signed)
  — nothing is rebuilt after the scan. The tag must match the version in `Whiskers.csproj` and `Chart.yaml`. An
  existing `X.Y.Z` image, chart or GitHub release is never overwritten; the run fails instead, also when it cannot
  tell whether the version exists. `X.Y` and `latest` still move; they only follow the highest *published*
  release (of the line, or overall), decided in a last job after the GitHub release exists, so a hotfix for an older line or a re-run of an older failed release no longer moves them back, a failed higher tag does not hold them back, and a version finished later takes them back.
  The scan no longer skips CRITICAL findings without a fix: such a finding fails the release unless it is recorded
  in `.trivyignore` with a reason and an expiry date. Each release now carries one SBOM per architecture
  (`whiskers-X.Y.Z-linux-amd64.sbom.spdx.json`, `…-linux-arm64…`) instead of a single one that only described amd64.
  **For release operators:** a manual run must be started from the tag itself (Actions → release → "Use workflow
  from" = the tag); a run from `main` is refused, and it only helps while nothing of that version is published. If a
  run fails after publishing started (for example signing during a Sigstore outage), use **Re-run failed jobs**:
  the image step accepts an `X.Y.Z` that already holds exactly the run's scanned digests and finishes signing and
  tagging; a new run would be refused. If the GitHub release was created but its assets failed, delete that release
  first. To re-release a version with other content, bump the version and tag again. Only one release runs at a
  time; a pending release run can be cancelled by GitHub when two more tags arrive, so start it again by hand.
  Untagged per-architecture images from each run stay in the GHCR package (staged by digest).

- R1-41: **A timeout inside a background loop no longer stops Whiskers.** Nine background loops (health, scheduler,
  AutoUpdate, certificates, log alerts, log hygiene, scan supervisor, self-metrics, action outcomes) treated every
  cancellation as "Whiskers is stopping", so a dependency's own timeout — an HTTP client timing out is a
  cancellation too — ended the loop and, with the host's default behaviour, stopped the whole app. Now only a
  cancellation while the host really stops ends a loop; anything else is one failed cycle, logged, and the next cycle
  runs. The image-update check catches a failed cycle too. A loop that still dies stops the host as before, so it
  stays visible (no global "ignore"). Loop intervals have a range: `HealthMonitor:CheckIntervalSeconds` and
  `Metrics:CollectionIntervalSeconds` 5 s to a day, `ImageUpdate:CheckIntervalHours` and
  `CveMonitor:CheckIntervalHours` 1 to 720 h (30 days). A positive value outside its range still starts: a warning is
  logged and the nearest limit is used (a 2-second health check runs every 5 s, a 2000-hour image check every 720 h),
  so a configuration that ran before keeps running. The Settings page uses the same limits, and saving a value outside
  them is refused with a message. All four loops use the value actually in effect, and a value that becomes invalid
  while Whiskers runs (the file edited, also to something that is not a whole number) does not stop a loop: it keeps its last valid interval and logs a warning.
  **Breaking only for zero or a negative interval:** such a value — which made a loop spin or stopped Whiskers before —
  now stops the start with an error naming the key. *Upgrade/recovery:* a value saved on the Settings page lives in
  `app-settings.json` in the data directory (`WHISKERS_DATA_DIR`, by default `/app/data`, the `serverwatch-data`
  volume of the shipped compose files). That file is the last configuration layer, so an environment variable such as
  `HealthMonitor__CheckIntervalSeconds` does **not** override it. To recover without the UI, stop the container, edit
  the key in that file on the volume (or remove it to get the default: 30 s health, 30 s metrics, 6 h image updates,
  12 h CVE) and start again. `/metrics` escapes label values
  (quotes, backslashes, line feeds — nothing else, so distinct names stay distinct) and keeps an error comment on one line, so a container, image or server name
  can no longer break the exposition or forge a series. The AutoPatch planning, the image-update check and the
  advisor chat's context pass their cancellation to the Docker fleet listing and the image inspect (new
  `IDockerService` overloads), so the preview deadline, a stop and closing the chat reach them — also while they wait for a connection or a free slot —
  and a cancelled caller is never counted against the server (the circuit breaker stays closed). The image-update check
  stops with the host instead of finishing in the background, and changes nothing after the stop. **A stopped loop no
  longer goes on in the background:** the health, log-alert, log-hygiene, image-update and auto-update loops used to
  stop *waiting* for a running cycle on shutdown and leave it running, so a listing that answered afterwards could
  still record metrics, send alerts or change state. Now the stop reaches every call such a cycle waits on — the
  container listings (fleet-wide and per server), inspects, digest reads and log reads (including their wait for a
  connection and a free slot), the registry, the notification delivery and its retries — the loop
  waits for them to end, and nothing happens after the stop. A notification that a stop interrupts is not reported as
  failed; an important one is kept for redelivery. An update whose container was already replaced is finished and
  recorded instead of being left unobserved. The fleet listing's 8-second limit per server now also covers the wait
  for a connection and for a free slot (before, a server whose slots were all taken could hold the listing
  indefinitely). Running out while waiting for a slot means Whiskers itself was busy and the server was never asked:
  it is shown as "not asked", never counted against the server's circuit breaker and never toward a
  `server_unreachable` alert. A log read that runs out of time the same way keeps its window, so the lines are read
  on the next cycle instead of being skipped (counted as `log_fetch_saturation` in the self-metrics). A CVE or
  metrics interval reloaded out of range (for example `2147483647` hours) no longer breaks `/metrics`: the loop,
  `/metrics` and the MCP CVE summary share the interval actually in use, so the staleness series also stays consistent
  with it.

- R1-42: **Values that end up on a command line can no longer pose as options, and kubeconfigs run no programs.**
  Quoting a value only protects it from the shell; a Git branch like `--upload-pack=…` or an SSH user like
  `-oProxyCommand=…` still reached git or ssh as an option. Whiskers now checks these values with one shared rule
  wherever the command is built — not only on the page — and puts them after `--` where the program allows it. A Git
  deployment's branch must be a plain branch name (it starts with a letter or digit and uses only letters, digits and
  `. _ / -`, no `..`); this is checked on save, also for `create_git_deploy_app`, and again before a deployment runs. An
  SSH server's host must be a host name or IP address, its user may use only letters, digits and `. _ -` and must not
  start with `-` or `.`, and its port must be 1–65535 — checked when the server is saved and before any SSH command,
  tunnel or terminal starts. Nginx site names must start with a letter or digit and must not contain `..`; a container
  terminal refuses a container id that is not one. **Kubeconfigs:** the Kubernetes client Whiskers uses starts an
  `exec` credential plugin as soon as it reads the kubeconfig, so Whiskers now accepts only self-contained kubeconfigs:
  one with an `exec` plugin, an `auth-provider` (OIDC, GCP, Azure) or a file path (`client-certificate`, `client-key`,
  `certificate-authority`) is refused, for any user or cluster in it; there is no setting to allow it. **Upgrade:** a
  Kubernetes server saved with such a kubeconfig stops connecting with "Kubeconfig refused: …" — paste a kubeconfig with
  an inline token or `client-certificate-data`/`client-key-data` (for example for a ServiceAccount, see
  `deploy/k8s/whiskers-agent-rbac.yaml`). A Git deployment app saved with a branch outside the rule fails its next
  deployment before anything runs on the server — save it with the branch's plain name. An SSH server whose stored
  host or user breaks the rule stops answering with an "Invalid SSH host/user" error until it is corrected on the
  Servers page. The rules are strict on purpose: a host name with `_`, an IPv6 address with a zone id (`%eth0`), an
  SSH user with `@` or `\` (domain logins) and a branch name with `@` or non-ASCII letters are refused too. A
  container's database page and the database backup copy now check the container reference as well. Existing checkouts update with `git fetch` of the branch's full ref and `git checkout -B`; nothing to
  do on the servers.

- R1-40: **A notification that fails is retried, and an important one is not lost.** Notification channels used to
  swallow their own errors, so the retry never saw a failure. Now a failed Slack, Discord, Mattermost, Matrix,
  Telegram, ntfy, e-mail or webhook delivery is tried a second time; every attempt ends after 20 s at the latest, and an
  e-mail (SMTP) conversation after 20 s as well (30 s for the test mail) — a stalled mail server is disconnected, also while it connects or withholds
  its final answer — so a hanging provider no longer holds up the health, log or update checks
  that send the alert. An alert an admin must not miss (an error such as a stopped or unhealthy container, an approval
  waiting for a person, a suspended or interrupted automation) that a channel still could not take is kept in
  `notification-outbox.json` and sent to that channel again — also after a restart — after 1, 2, 4 … minutes (at most
  an hour apart), for up to 8 more tries within 24 hours; a redelivery is not held back by the channel's throttle, a Matrix message is not posted twice when its first answer was lost, and a mail server that takes some recipients and
  refuses others gets the message again only for the ones it refused (also after a restart). At most 200 are kept; when it is full, the oldest alarm goes first — approval requests and administrative events
  only when nothing else is left. A server's recovery is kept like its outage, so the two arrive together. Keeping one never makes the sender wait for the disk; the file is written in the background — after every single
  redelivery, so a long backlog never holds back what is new — and a file that cannot be read for the moment is read again
  later instead of being overwritten; one that is damaged (also one that parses but holds no valid entries) is kept aside
  and its previous version used. An entry is checked again just before it is sent, so one that ran out of time while
  others were sent is not sent late.
  Giving up is logged with the channel and the reason (never a webhook URL, a URL path or token) and shown in `/metrics`
  (`whiskers_self_notification_outbox_pending`, `whiskers_self_notification_outbox_total`,
  `whiskers_self_notification_outbox_unsaved`,
  `whiskers_self_notification_failed_total`). The outbox is not restored from a self-backup. AI triggers are started
  once per event, no longer a second time when the first dispatch was slow.

- Brokkr provisioning (contract 0001): **Git deployments can be set up over MCP.** Five tools let an MCP client
  such as the Brokkr CLI create a Git deployment app (`create_git_deploy_app`), set or remove its access token
  (`set_git_deploy_token`, its own capability `gitdeploy.set_token`), read an app back without its token
  (`get_git_deploy_app`), create the GitHub push webhook for it (`create_git_deploy_webhook`) and rotate that
  webhook's secret (`rotate_git_deploy_webhook_secret`). Every write is Admin, goes through the guardrails and the
  mutation gateway (approvals included) and is read back afterwards. The person who approves the call in Whiskers
  becomes the owner of the app and the webhook — never the key — so a later push deploys on that person's authority;
  a call nobody approved (a policy that lets the key through) is refused. A repository URL that carries credentials
  is refused (also on the page), and so is saving a copy of an app deleted meanwhile (except a copy of an app saved before R1-08 and never since); a token of blanks removes the
  stored token. Tokens are
  never echoed, logged or shown on an approval card; a webhook secret appears once, in the tool's answer. The four
  writes are not offered to the AI agent. Their errors are JSON with a code (`invalid_argument`, `not_found`, `conflict`,
  `denied`, `pending_approval`, `failed`) — also when the gateway refuses a call or holds it for approval, which answers
  with the operation id and the approval id; repeating the identical call after the approval runs it once. A Git
  deployment app name is now unique (case-insensitive), also when saved on the page. Switching off one of these tools
  in Settings → MCP also stops the matching action on the Git deploy and Webhooks pages.

- R1-39: **A stop lets running work finish; what a hard stop cuts off is reported, not repeated.** (Adds the nullable
  `ProcessInstance` column to `TaskRunHistory` and `UpdateHistory`; additive migration, applied at start.) When Whiskers
  stops, no scheduled task, AutoUpdate or AutoPatch container is started any more, and what is already running gets
  up to 30 s to finish (the host's shutdown budget is now 45 s; Compose `stop_grace_period` (also in the file the installer writes) and Helm
  `terminationGracePeriodSeconds` are 60 s so the container is not killed first). A task that falls due during the
  stop stays due and runs once after the restart; "Run now" is refused while Whiskers stops or while the task already
  runs. Every scheduled run is on record before it starts (in the same save that consumes its occurrence); a run that a
  forced stop, a kill or a crash cut off is marked at the next start as failed with "INTERRUPTED: … whether it took
  effect is unknown. It was not run again." — in the task history and as the task's last result (unless a later run
  has recorded one). An AutoUpdate attempt is on record before it changes anything; one that was cut off is marked
  interrupted in the update history and AutoUpdate is switched off for that container until an admin has checked it
  and switches it on again. Operations that were still running in the operation journal become `Interrupted`: a retry
  under their id stays refused as "outcome unknown" and their audit intent is settled as unknown. Admins get one
  notification (`operations_interrupted`) naming what was cut off, so they can check the targets. The AutoPatch night
  is kept in `autopatch-nightly.json` (not restored from a self-backup): its start must be saved before anything is
  patched (otherwise the night is skipped) and an unreadable state file stops the nights until it is fixed, so a
  restart in the window hour does not run it twice; a night that was cut
  off, or ended early, still says so after a restart in the AutoPatch status (`get_auto_patch_status`).

- R1-38: **Nothing waits without end.** An AI provider that sends no response, stops sending in the middle of an
  answer or trickles on for too long is cut off (60 s for the response to start, 90 s between two lines, 10 min for
  the whole answer) and the run ends with a timeout message instead of hanging. An unattended AI-trigger run ends after
  15 minutes: the provider call and any tool still running are cancelled, its parallel-run slot is freed, and the
  notification says it timed out and to check the audit log for what it had already started. The advisor chat gives up
  after 90 s and stops when the chat is closed; it now takes its HTTP client from the factory for each answer. The
  AutoPatch preview started from MCP is owned by the service: only one at a time, at most 2 hours, and cancelled when
  Whiskers stops. Cancelling a host command on an mTLS server now kills its helper container. An operation stopped by
  its deadline or its caller while it ran is reported with "its effect is unknown", not as if it had not run.

- R1-37: **Every change says what was checked afterwards.** Each capability now declares how its effect is verified:
  a read-back, or "not verifiable" with a reason that the outcome and the audit row carry. Read back after the call
  are compose deployments (every container of the project runs), networks, firewall rules and the ufw status, nginx
  site files and links, nginx and systemd unit states, renewed certificates (valid for at least 29 more days: certbot's 30-day renewal window with a day of slack),
  DNS records at the provider, cloud power on / shutdown and Hetzner rescue and backup settings (polled until the
  provider shows them, within the deadline), deleted Hetzner images, and database, volume and self-backups with their
  deletes and retention. An effect that is not there fails the operation; a read-back that cannot be made fails it
  with the state unknown. A capability without a read-back (an arbitrary command or query, a reboot, a rule removed
  by number) is recorded as not verifiable, never as verified. MCP tools that answered with an error text — a refused
  call, a missing DNS token, a provider error, a Hetzner action that ended in `error` — now fail the operation instead
  of being recorded as executed. A compose deployment is read back under the project the plan resolved (a top-level `name:` included), a one-shot
  systemd unit that ran to completion counts as started, and a unit without an `[Install]` section is not verifiable
  for enable / disable. Also fixed: the firewall page showed an inactive ufw as active.

- R1-34 follow-up: **the recovery command is recognised in every spelling.** `--restore-backup=<id>` and the option
  after `--for-previous-release` now stage the restore; before, they started Whiskers normally on the data the restore
  was meant to replace. A restore staged from the Settings panel now resets the upgrade state like the command line,
  so the first start on the restored data takes a pre-upgrade backup of it; a failure to reset it no longer reports a
  staged restore as failed. When an unfinished upgrade's own backup is gone, the log lists the older pre-upgrade
  backups by id, and only a schema that had migrations pending logs its migration time.

- Dependencies: **three known high-severity flaws removed.** The unused SignalR client package is gone; it pulled
  `SignalR.Common` 10.0.10 (CVE-2026-70159 / GHSA-334f-vvp4-7g6p, a denial of service up to 10.0.12). The server-side
  SignalR of the Blazor circuit and the container hub is part of the ASP.NET Core runtime in the base image, and the
  release build now always pulls the current base image (`pull: true`) so a cached older runtime is never shipped.
  The SQLite migrations project now ships the same `SQLitePCLRaw` 3.x bundle as the app instead of 2.1.11, whose
  bundled SQLite is affected (GHSA-2m69-gcr7-jv3q); the test project pins SSH.NET 2026.0.0 over the 2025.1.0
  Testcontainers pulls (GHSA-mggc-4xg6-vcxf, GHSA-q939-rpr3-3284; test-only, not in the image). Also updated: Google
  and OpenID Connect sign-in 10.0.14, KubernetesClient 21.0.8 (kubeconfigs are now read with
  `KubernetesYaml.Deserialize`), Markdig 1.5.1, Blazor-ApexCharts 7.1.0, SQLitePCLRaw bundle 3.0.5, bUnit 2.13.3,
  coverlet 10.1.0, ASP.NET Core test host 10.0.14. `dotnet list package --vulnerable --include-transitive` reports no
  vulnerable package.

- R1-34: **An upgrade starts from a recovery point.** When another release starts on an existing database (a new
  version, or schema migrations to run), it first takes a self-backup labelled `pre-upgrade-…` — right after the vault
  checked `VAULT_KEY` and before anything rewrites data in a newer format (server list, credential move into the
  vault, key ring, database schema), so it holds the state the previous release left (only `vault.json` may already
  carry the vault's key check). When that backup cannot be taken (no space, a wrong `VAULT_KEY`, missing PostgreSQL
  client tools), the start stops with nothing changed; `WHISKERS_UPGRADE_SKIP_BACKUP=true` upgrades without one and
  says so in the log, and an invalid value of an upgrade setting stops the start before any backup. An upgrade whose
  migration failed stays unfinished: the next starts keep its backup instead of taking new ones of the half-migrated
  state, and it is never pruned. The scheduled backup retention no longer deletes pre-upgrade backups; the newest three
  are kept (`WHISKERS_UPGRADE_KEEP_BACKUPS`). A failed migration logs which backup to restore. The restore needs no
  running UI: `--restore-backup <id>` (or `latest-pre-upgrade`, the backup of the unfinished upgrade) stages a backup
  through the same checks as the Settings panel and exits; the next start applies it. A **downgrade** is the previous
  release started on the pre-upgrade backup, staged with `--for-previous-release` (this release's migrations are then
  not run on the archive, since they may be what fails) — an older release does not run on a database a newer one
  migrated. The Helm startup probe now allows five minutes (`probes.startup.*`, with readiness and liveness
  configurable too), and the image's health check waits five minutes on a starting container. The one-time SQLite →
  PostgreSQL transfer (`--migrate-to-postgres`) now copies all tables and the sign-in accounts in one transaction: a
  failure leaves the target empty, so the transfer can simply run again. The container now hands command-line options
  to Whiskers (it dropped them before), so `docker compose run --rm serverwatch --restore-backup …` and
  `--migrate-to-postgres` work in the image, after the same VPN bring-up as a normal start. Tests now catch a model
  change without a migration, and a migration shipped for only one of the two databases.

- R1-33: **A backup of a PostgreSQL instance now holds the database.** The self-backup takes a `pg_dump` of the
  database together with the files, so one archive is one recovery point for sign-in accounts, scheduled tasks,
  webhooks, audit and settings alike. Before a restore is committed, Whiskers takes the pre-restore safety backup and
  then applies the dump in a transaction and rolls it back (a dry run). The dry run runs the archive's SQL, so the
  safety backup is what protects the database from a crafted archive; a script that ends that transaction on its own
  is also detected and the archive refused (the backup guide describes the recovery from the safety backup). A restore
  that leaves functions, triggers, rules, views, row policies or operators in `public` is refused, as on SQLite, and
  so is one that changed role or database settings or the schemas, relations and code objects of other schemas, or
  brings grants, unlogged or foreign tables. A restore counts as done only when psql ran the end of the script. The
  restart then replaces the schema in one transaction, so the database is either the backup's or unchanged, never half
  of each. Backups of PostgreSQL instances from before this version hold the files only: the backup list marks them
  **files only** and Whiskers does not restore them, because the files would no longer match the database. The image
  ships the PostgreSQL 18 client tools; outside the image, set `WHISKERS_PG_BIN_DIR`; `WHISKERS_PG_TIMEOUT_MINUTES`
  sets the time limit per tool run (default 30). The new backup guide `docs/operations/backup-and-recovery.md` covers
  the recovery onto a new host after the data volume is lost. Grants of other roles are not part of the dump. The dry
  run also checks that the migration histories match the manifest and that the next start's migrations run on the
  dump. The restore script goes from `pg_restore` straight to `psql` and never lands on disk, and it runs only with
  the meta-command protection of client tools 17.6 or newer, so a crafted dump cannot run shell commands. The role
  Whiskers connects as must own the `public` schema (on PostgreSQL 15 and later the database owner does) and hold
  `CREATE` on the database, and should not be a superuser; the PostgreSQL overlay's role is one, and the guide shows
  how to change that. The dump is written in UTF-8. A backup whose dump does not hold Whiskers' tables (for example
  because a search path put them in another schema) fails instead of claiming the database. A restore commits only
  when the dump was read to its end without an error. On PostgreSQL the readiness check shows whether the client tools
  are usable and new enough to restore.

- R1-36: **A container action counts only when the container shows it.** After a start, stop, restart, remove,
  update or rollback — and after a single-container deployment from the Deploy page — the container is read back; one
  that is not running (or still running, still there, killed for lack of memory) makes the action **failed** — on the
  page, for the agent, for MCP clients, for scheduled tasks, webhooks and AutoPatch, and in the audit (verification
  `Failed`), even though Docker accepted the call. A one-shot container that already ran to completion (exit code 0)
  counts as started. Compose and Git deployments, and the `deploy_app` tool (it answers with text), are recorded as
  not verifiable until R1-37. **Automatic rollback works:** an automatic update whose new container is not verified
  or not healthy afterwards is rolled back to the previous image when its policy has rollback on (the default), on
  the owner's authority and verified; the update history says whether the rollback worked, and a rollback that did
  not work keeps its snapshot for a manual one (so does a manual rollback from the Dashboard that does not come up). A
  state that cannot be read after the update is reported, never rolled back on; an image whose update was rolled
  back is not tried again until a newer one appears. The snapshot rule applies to automatic updates: an update from
  the Dashboard or the `update_container` tool still runs after a failed snapshot and says so. With rollback on, an update whose snapshot could not be taken is not run; without a usable vault no
  snapshot can exist, and the update still runs without a way back (as since R1-31).

- R1-35: **Settings files survive a power loss and say when they were damaged.** Every settings store (roles, whitelist,
  vault, servers, permissions, guardrails, triggers, …) is written to a flushed temporary file and renamed into place,
  keeping the version before as `<file>.bak`; the CVE and image-update state, the agent and app settings, the chat
  history, the automation owners, the known local accounts, the audit outbox and the audit checkpoint are written the same way (without
  a previous version). A store that cannot be read is no longer the end of the start: Whiskers uses the
  previous version, keeps the unreadable file as `<file>.corrupt-<time>` and shows both on the readiness card
  ("Datenspeicher lesbar") until the kept file is deleted. Without a readable previous version the start (or, for stores read on first use, that feature) still stops —
  never with an empty, more permissive store. The guardrail policy is the exception: an unreadable `guardrails.json`
  keeps refusing every guarded action (as before) instead of using the previous version. `servers.json` and
  `mcp-permissions.json` keep no previous version while they still hold a legacy plaintext key. A restore removes the
  previous versions of what it replaces. **Operators:** to reset a store (e.g. delete `roles.json` to seed the admin
  again) delete `<file>.bak` too — a store with only its previous version is recovered from it.

- R1-32: **A restore writes only what a backup may hold, and only after checking it.** A self-backup archive may
  contain only Whiskers' own stores and directories (one list, `SelfBackupLayout`, used for packing and restoring);
  an archive with backups, restore state, database journals, temp files, a vault key, duplicate, conflicting or
  non-canonical entry names, overlong names, oversized headers, JSON stores or key files, or more than 100,000 entries or 32 GiB of content is
  refused, and unknown entries are left out; a restore that would not fit on the data volume is refused too. Before
  the restore is committed the extracted database must pass SQLite's integrity check, hold exactly the migrations its
  manifest names and migrate cleanly on a copy (as the next start will), the stores a service reads at start must read as what it loads, and the key files must parse;
  only then is the pre-restore safety copy taken. The vault's CBC recovery copy (`vault.json.legacy-cbc*.bak`) is no
  longer part of a self-backup. The restore
  makes each store equal to the archive — a store the archive does not hold is removed (the safety copy keeps it);
  the database and the VPN daemon state are only ever replaced, the audit outbox is merged. Key directories and the vault file are readable by
  the app's user only afterwards, and encrypted archives appear under their name only when complete. A second restore
  is refused while one is committed and waiting for the restart. **Breaking:**
  with `VAULT_KEY` set an unencrypted archive is no longer restored (it cannot be checked for changes); restore it on
  an instance without `VAULT_KEY`, or keep using the encrypted backups made since the key was set.

- R1-31: **Webhook secrets, rollback snapshots and the sign-in key ring are encrypted.** The inbound webhooks' signing
  secrets and the rollback snapshots (a container's full configuration, its environment variables included) are
  stored in the database encrypted with a key derived from the vault's, and so is each key of the data-protection key
  ring (sign-in cookies, antiforgery). Without a usable vault a webhook cannot be created or have its secret
  regenerated, and an update keeps no rollback snapshot (it goes ahead and says so). **Upgrade:** plaintext secrets,
  snapshots and key files are encrypted in place at the first start with a usable vault; nobody is signed out.
  **Changing or losing `VAULT_KEY`** now also signs everybody out and leaves webhooks and snapshots unreadable until
  the right key is back (see SECURITY.md). A readiness line counts what is still in plaintext.

- R1-30: **Notification and AI credentials are kept in the vault.** The SMTP password, the Matrix, Telegram and ntfy
  tokens, the ntfy topic, the Mattermost, Discord, Slack and custom-webhook URLs, the custom webhook's signing secret,
  and the API keys of the AI chat and the agent are now stored encrypted in the vault; app-settings.json and
  agent-settings.json only name the vault key. The editors never show a stored value again: an empty field keeps it,
  a new value replaces it, the delete icon removes it. Saving a credential needs a usable vault (refused otherwise),
  and a failed save leaves the previous credentials in use. With the vault disabled or locked, a credential kept in it
  counts as not set (the channel stays silent and the log says why). **Upgrade:** existing plaintext values move into
  the vault at the first start with a usable vault, like the cloud and DNS keys; until then a save of those settings is
  refused. A second readiness line lists any credential still in plaintext. Credentials set by environment variables
  still work and are not moved.

- R1-29: **Cloud API keys and the DNS token are kept in the vault.** A server's cloud API key (Hetzner, Hostinger) and
  the DNS provider token are now stored encrypted in the vault; servers.json and app-settings.json only name the vault
  key. Saving one needs a usable vault — with no `VAULT_KEY`, or a wrong one, the save is refused instead of writing
  plaintext — and the stores refuse a plaintext key from any path. **Upgrade:** at the first start with a usable vault,
  existing plaintext keys are moved automatically: each is stored in the vault, read back from vault.json on disk and
  compared, and only then removed from its file; an interrupted start finishes at the next one, and without a vault
  nothing changes and the keys keep working (the readiness check lists them); until then a server or DNS
  save other than removing the credential is refused, since the save would have to move it. Removing a server also removes its cloud
  key from the vault. The production-readiness card now reports where these credentials actually are and no longer
  claims that every secret is encrypted: notification, chat and AI credentials and webhook secrets are still plaintext
  (R1-30, R1-31). A token set by the environment (`Dns__ApiToken`) still works and is not moved.

- R1-28: **A wrong `VAULT_KEY` no longer touches the vault.** `vault.json` now carries a key check; at start the key
  must open it (or, on a vault written before, one of its entries). A wrong key, a tampered check or entries without a
  salt lock the vault: no secret is read, written, deleted or migrated and the file stays exactly as it was. Settings
  and the production-readiness check show that the key does not match. Legacy AES-CBC entries are converted only when
  every one of them decrypts to valid text — a padding that happens to fit is no proof of the key — and only after an
  untouched copy `vault.json.legacy-cbc.bak` is written next to the vault (never overwritten); they are converted all or
  none. **New:** `VAULT_KEY_FILE` (Docker/Kubernetes secret file) wins over `VAULT_KEY` for the vault, the backup
  encryption and the audit chain. A configured `VAULT_KEY_FILE` or `WHISKERS_DB_CONNECTION_FILE` that is missing,
  unreadable or empty now stops the start (it fell back silently before); so does a configured
  `WHISKERS_ADMIN_PASSWORD_FILE` unless `AUTH_DISABLED` is set, while the admin it seeds does not exist yet (it skipped the seed with a warning). The
  setup wizard no longer offers a new key when the configured one does not match, every vault-dependent page names a
  wrong key as such, and no self-backup is created while the vault rejects the key (it would be sealed with the typo). **Recovery:** if a converted legacy vault is later reported as locked with
  the right key, stop Whiskers, put `vault.json.legacy-cbc.bak` (or the newest `vault.json.legacy-cbc.<time>-<n>.bak`)
  back as `vault.json`, set the right key and start again. Delete the copy once the converted secrets read correctly —
  it holds the old unauthenticated ciphertext and is part of every self-backup.

- R1-25: **Logs and outbound notifications carry no secrets.** Every logger now writes through a redacting logger
  factory, in front of all providers (console, debug, event log): the message, each structured value, the message template, scopes and the full
  exception text are redacted before they reach it, and an entry that cannot be redacted is replaced by
  `<log entry withheld: redaction failed>` instead of being written as it was. Notifications are redacted once in the
  composite, before the in-app feed, the alert history, each channel (Mattermost, Matrix, Telegram, ntfy, Discord,
  Slack, e-mail, webhook) and the AI-trigger dispatcher see them, so a matched log line, a connection error or a model's
  summary no longer goes out with a password or token in it. An AI trigger's summary is redacted before it is sent and
  cut at a complete word. Capability URLs (a Telegram bot token, a Discord, Slack or Mattermost webhook key) and every
  configured channel secret (bot token, webhook URL, ntfy topic URL, Matrix access token, SMTP password, webhook signing
  secret) are replaced by `***` wherever they appear — log lines, exception texts and every stored text; a URL also in its
  normalized form. The HttpClient request log stays at Warning by default in code (a configuration rule bound to a
  provider or to a more specific category can still switch it on; the redaction catches what it lets through). The notification feed and the alert history are
  redacted on save, whoever writes them. A failed channel test (also from the Settings page) names no URL; the in-app bell shows redacted text too; a failed manual CVE scan or an unreadable
  restore marker writes its error to stderr redacted. The audit-log fallback (database down) logs the entry redacted
  like the row.

- R1-24: **No persistent sink keeps a secret.** Every free-text column of the audit log, the MCP/agent call history,
  approvals, policy decisions and scheduled-task results is redacted on save by a database interceptor, whoever wrote
  the row (bulk updates excepted; they only copy redacted values or fixed texts). The agent transcript now redacts
  every message role (it redacted tool output only) and the arguments of kept tool calls; the advisor chat history is
  redacted per message. Tool arguments in the call history and the audit log keep free-text payloads (a command, SQL,
  a compose file, an `.env` block, a prompt) only as their length, matched on every spelling of the name
  (`composeContent`, `compose_content`, `docker-compose`, `existingContent`, `envVars` …); a key named after a secret
  is `***` at any depth; URL user info (`https://user:token@host`, also a token alone) is removed before the patterns.
  Approval cards keep a chat or MCP call's payload readable for the approver, with secrets in it redacted. The agent
  history's error column is redacted too (it was stored as it came). The audit log shows an executed shell command
  (`command.execute`) and a scheduled custom command only as their length (it showed the pattern-redacted command).
  Stored JSON keeps quotes readable (`'`, not `\u0027`). In a gateway event's `principal`, the credential's id is now
  `keyId` (was `credential`, a name the secret rule hides). An approval's reason no longer quotes a payload argument
  (the card's arguments still show it). A stored JSON field that is too long is shortened by its values and stays valid
  JSON. Long texts (task output up to 1 MB) get a larger redaction time budget instead of being withheld whole; the
  pattern budget is 1 s per pattern (was 100 ms, which a loaded host could exceed on ordinary text). A command output
  cut at 1 MB — and a tool output cut for the agent (8000 characters) or by `execute_command` (50 000) — now ends at
  its last complete line or word (one long line without any is dropped), as do log lines shortened for a search
  result or an alert, so no half-cut credential is stored. Rows written before this version stay as they are.

- R1-23 follow-up: an unreadable checkpoint file and a pending VAULT_KEY adoption are logged as what they are (not as
  tampering); the page counts every finding, also above the display cap; export scratch files left by a crash are
  removed by the next export.

- R1-23: **The audit trail is tamper-evident, retained on purpose and Admin-only.** Every written version of an audit
  row becomes a link of one SHA-256 hash chain, in the same transaction as the write (ADR-0006). Every minute and at
  shutdown the chain's end is signed with HMAC-SHA-256 under a key derived from `VAULT_KEY`, in the database and in
  `audit-checkpoint.json` beside it. *Verify integrity* on the Audit log page reports a changed row, an inserted row, a
  deleted row, a broken link, a link added outside Whiskers (every link is signed too), a forged checkpoint and an end
  cut off before a signed checkpoint, in the page's language. Without `VAULT_KEY` nothing is signed and the trail is
  not tamper-evident against a database writer (the page says so); a compromised host can still rewrite everything,
  and a database writer can cut off the last minute before the next checkpoint (documented limits). **Once the trail
  is signed, a start without `VAULT_KEY` refuses audit writes — and with them every mutation — until the key is set
  again** (fail closed). An audit row changed in the database is never completed or settled into a new version: the
  outcome write is refused and the change stays a finding. On the first start the existing rows are sealed once (genesis). New tables
  `AuditChain` and `AuditCheckpoints` (additive migration, both providers); the SQLite→PostgreSQL copy does not copy
  them, so PostgreSQL starts a new chain and the old checkpoint file is kept as `audit-checkpoint.json.sqlite-<time>`.
  **Retention changed: audit log and MCP call history are now kept 365 days (was 90)**, configurable with
  `Audit__RetentionDays` (never below 30); a daily retention run first verifies the trail (and removes nothing from
  one that does not verify), is recorded as an `audit.retention` event naming both counts and signs the chain's new
  start. The Audit log and Agent History pages (and the audit rows of the Self status timeline) are Admin
  only. `GET /audit/export` (Admin only) downloads the whole trail as JSON — verification result, the checkpoint file,
  checkpoints, chain and rows from one snapshot — and is recorded as `audit.export` before anything is read (503 without that record).
  A self-backup restore is recorded after the restart as a chained `selfbackup.restored` event (who, which archive,
  when) — the restore's own event lived in the database it replaced. Self-backups take the checkpoint file before the
  database copy; restoring an archive from before R1-23 keeps the
  current checkpoint file aside (`audit-checkpoint.json.pre-restore-<time>`) so the restored rows start a new chain.
  A retention run removes at most 10,000 rows (the next day's run continues) and is skipped while a signed trail has no
  `VAULT_KEY`. Setting `VAULT_KEY` later is supported: the first start with it signs the existing (unsigned) start of the chain
  once. **Changing** `VAULT_KEY` makes earlier checkpoint signatures invalid. To start over with a fresh database in
  the same data directory, move `audit-checkpoint.json` aside first — otherwise the new database is not sealed and
  its trail reports the old end as cut off.

- R1-22 follow-up: a webhook switched off automatically writes its own `webhook.suspended` row with the version
  before and after; a restore refused by archive validation is a Denied event; flags named after a secret
  (`HasToken`) stay readable; the configuration pages' Admin gates are checked to record their refusals.

- R1-22: **Configuration, sign-ins and backup downloads are audited.** A configuration or policy write — whitelist,
  roles, MCP keys and tool switches, guardrails and presets, AI triggers, agent settings, Settings sections, servers,
  registries, Git-deploy apps — is one gateway event that carries what changed: the section's version before and
  after (`config.version`, the first 12 hex digits of a SHA-256 over the redacted content) and each changed setting
  with its old and new value (target type `setting`, at most 8 per operation over all its sections, the rest counted;
  webhooks, log-alert rules, servers, registries and Git-deploy apps also on create, toggle, rotation and delete).
  Changes are detected on the complete values, so an edit past the shown part or inside a URL's path counts. Secrets
  never show a value: only `[not set]` or `[set #<fingerprint>]`, keyed per process so a replaced secret is a visible
  change while nothing stored lets anyone test a guess. URLs show their origin only (a URL-named value that is no absolute URL shows as text); free text (prompts, log
  patterns, commands, descriptions — however short — and any long or URL-holding value) shows as
  `[text, N chars #<fingerprint>]`. The version is computed without secrets, URL paths and free text, so a change of
  those alone keeps it and appears as a changed setting. ntfy topics count as secrets, an address with user info as
  free text, and a registry host carrying user info (`user:password@host`) is now refused on save — the credential
  belongs in its own field. Two records with one identity (two role entries for one account) are numbered by
  occurrence (`a@example.com~2`), so neither hides a change of the other; a list of
  records such as API keys is classified field by field, so a key's level change is readable. An event keeps at most
  20 reports and counts the rest (`results.omitted`). Git-deploy takeovers carry the owner change; a replaced Git token is a changed setting (fingerprints only) and an
  app's revision counter and last deploy are not part of its version, nor are a webhook's trigger counters. An
  automation takeover records the whole owner entry, so lifting a suspension shows even when the owner stays. The
  configuration pages (Settings, Servers, Webhooks, self-backup, log alerts, agent, AI operations, automation owners,
  registries) no longer write audit rows of their own: a refused attempt is the gateway's **Denied** event, and the old
  action names (`config.save`, `server.add`/`update`/`remove`, `webhook.create`/`enable`/`disable`/`delete`/`test`/
  `adopt`, `registry.create`/`update`/`delete`, `mcp.key.create`/`delete`, `vault.set`/`delete`,
  `selfbackup.create`/`delete`/`restore`, `automation.owner_set`) are replaced by the capability ids. New rows:
  `auth.login`, `auth.login_failed` (with `reason=unknown_account|wrong_password|locked_out|not_whitelisted|
  provider_failure` and the client address; an unknown account name is never recorded) and `auth.logout`, for local,
  Google and OIDC sign-ins — hooks only, the login itself is unchanged; the rows are queued and written in the
  background, so a slow or locked audit database never delays a sign-in, and a lost row is reported to the `audit`
  health check; a queued row keeps the time the sign-in happened. A replaced SSH key (also under the same file name) is a changed setting of `ssh-key:<server>`, a kubeconfig
  reference counts as a secret, and token limits (`MaxTokens`) stay readable numbers. Secrets are compared by their whole per-process HMAC, so colliding short fingerprints never hide a change.
  Saves of one section (also log-alert rules) are serialized from capture
  to diff, and a server's diff compares with what was stored, so every event describes the change it made. Refusing an enabled whitelist without addresses is a Denied event. A self-backup
  download (`selfbackup.download`) is recorded with the requester, archive and size before the first byte, and is
  refused with 503 when that row cannot be written; a signed-in non-Admin's attempt is recorded as refused.
  No migration.

- R1-21: **One audit event per infrastructure action in the UI.** The container, deploy, network, firewall, nginx,
  systemd, certificate, cloud, Git-deploy, scheduled-task and volume-backup pages no longer write audit rows of their
  own next to the gateway's: each attempt is exactly the gateway's event — also when the page's role check refuses
  it before its confirmation dialog (recorded as **Denied**), and when the service answers a failure without throwing
  (a result whose `Success` is false, a missing migration framework, an unsupported import file → **Failed** instead
  of Executed; a Hetzner action Hetzner answers with status `error` now fails instead of reporting "Action: error" as
  success). What the page knows about the result — the created container(s), network or Git-deploy app, the backup
  file, the deployment output, the template or registry deployed, the previous owner on a takeover — is part of that
  event. The old page-level action names
  (`deploy.app`, `backup.create`, `task.adopt`, …) are no longer written for these pages; filter the audit by the
  capability instead (`app.deploy`, `volume.backup`, `scheduler.update`, …). A page's refusal names the resolved
  target like the action's other outcomes. Automation runs (scheduled tasks, AutoPatch) likewise record a result
  whose `Success` is false — for example a failed mTLS renewal — as **Failed**. No migration.

- R1-20: **One outcome per MCP call, one audit event per mutation.** The Agent History of a direct MCP call no longer
  takes success from the HTTP status: each call records what it came to — executed, denied, failed, waiting for
  approval, or executed with a failed verification (`ResultSummary`) — with its verdict and, for a mutation, the
  gateway's operation id (`CorrelationId`, the same id as its audit event); each call of a batch keeps its own. A call
  no outcome was reported for is recorded as not successful. MCP tools no longer write audit rows of their own next to
  the gateway's: what they report (the record created, the backup file, a failure) is added to the gateway's event
  (`results` in its details), and a failure a tool reports makes the mutation **Failed** instead of Executed.
  No migration.

- R1-19: **Complete, fail-closed audit events.** Every gateway attempt — allowed, denied, waiting for approval, failed —
  is one `AuditLog` event whose security fields are columns of their own (operation, parent operation, decision,
  trigger, surface, principal kind/id/credential/session/on-behalf-of/delegation, request session, approval id and approver, phase, outcome,
  verification, duration, the redacted arguments in full) and a unique event id: they no longer depend on the capped
  `Details` JSON, and a pending call now names the approval it waits for. An outcome that cannot be written is kept
  and written again; a refusal that cannot be written is kept in `<data>/audit-outbox/` and inserted later (also
  after a restart, never twice); intents left without an outcome are settled from the operation journal (`Unknown`
  when the operation was cut off).
  New readiness check `audit`: **Degraded** while the trail is incomplete (also until its first check after a start), also for an hour after an older,
  non-gateway audit write failed. No mutation runs and no configuration is published without a durable intent
  (unchanged, now tested). A quoted MySQL password flag (`"-p…"`) is masked too, while a hyphenated name such as
  `web-prod` no longer is. **Upgrade:** additive migration `AuditTypedFields` (SQLite and PostgreSQL) — existing rows
  keep their columns empty; `--migrate-to-postgres` reads an older `AuditLog` without them.

- R1-10: **Webhook deliveries are fresh, run once and never overlap on a target.** Each webhook has a signature scheme
  (`Provider`, new column, existing webhooks keep `github`): `github` checks `X-Hub-Signature-256` over the body as
  before; the new `whiskers` scheme signs a timestamp, a delivery id and the body (`X-Whiskers-Timestamp`,
  `X-Whiskers-Delivery`, `X-Whiskers-Signature`) and refuses a delivery more than five minutes off. GitHub signs no
  time, so for `github` a repeated delivery is recognised by its signed body for 90 days — not by its age. Every
  accepted delivery is recorded (new table `WebhookDeliveries`) and runs at most once, also across a restart; one that
  was running when Whiskers stopped is reported `interrupted` and not run again. The endpoint now answers at once —
  202 with a `runId` (the run's operation id), 200 `duplicate` with the first run's id and state, the same 400 for
  every invalid request, 429 / 503 with `Retry-After` — and runs the action in a bounded queue, one run per target at a
  time — container actions and deployments (also Git deployments, from their checkout) one at a time per server, by its id whatever alias is stored —
  and re-reads the webhook before it runs, so one switched
  off while its delivery waited does not run. New settings under `Webhooks` (body size, request rates, queue,
  concurrency, timestamp window, retention, run timeout). The SQLite → PostgreSQL migrator copies webhooks of a
  database from before this release as `github`.
  **Upgrade notes:** a new table and column, created on start. A CI job that read the action's output from the
  webhook response gets a run id instead; the result is in the webhook log.

- R1-18: **One operation id runs at most once.** Every mutation claims its operation id in the new `Operations` table
  (paired SQLite/PostgreSQL migrations, copied by the SQLite → PostgreSQL migrator) right before it runs, and the claim
  records how it ended. A retry under the same id — concurrent, after it ran (also after it failed), or after a
  restart cut it off — does not run again: an operation that was running when Whiskers stopped stays "outcome
  unknown" and is never replayed blindly; the same id used for another principal, capability or arguments is
  refused. A call waiting for its approval claims nothing, so the approved retry runs once; a retry of an operation
  that ran, runs or was cut off is refused as such before the approval gate (also in the decision an agent session
  asks for), so it never raises another approval.
  Only the claim that took the id can finish it or give it back (a claim token), so a retried release never frees a
  later claim. Decision, approval, execution, verification and audit carry the same id — also for an approve-then-retry
  call that names no id of its own (a direct MCP call): the retry continues the operation that raised the approval
  (`IApprovalGate.OpenOperationOf`), and concurrent identical first calls share one id and one approval; an approval
  is used only by the operation it was raised for (approvals raised before this release are asked for again); a mutation nested in another one's execution gets an id of
  its own and keeps the link (`parentOperationId` in the audit row, `ParentId` in the journal). A journal that cannot
  be written refuses the mutation. The SQLite → PostgreSQL migrator rewrites an operation's `AuditId` to its audit
  row's new id. **Upgrade notes:** a new table, created on start; nothing to migrate by hand.

- R1-17: **Web-terminal sessions go through the Mutation Gateway and are audited.** Opening a shell in a container
  (`terminal.container_open`) or on a host (`terminal.host_open`) is an Admin, root-equivalent capability decided like
  every other mutation: the kill switch opens nothing, a shell in Whiskers' own control plane needs an explicit Admin
  approval (given once, it lets the next open of the same terminal through), and the open is audited. A session is
  owned by the principal the gateway decided on and opens only inside that decision. Every session ends with exactly
  one `terminal.close` audit event — principal, target, duration, the open's operation id and why it ended (closed, shell exited, idle, revoked, kill switch, terminals
  switched off, shutdown); what is typed is never recorded. Running sessions are re-checked every 15 seconds: a
  session whose owner no longer holds Admin (role lowered, removed, not allowed to sign in) ends, and switching the
  kill switch on ends every session (with `Auth:Disabled` the local user stays Admin, as in the web UI). A server id that names no configured server is refused instead of opening a
  shell on Whiskers' own host. **Upgrade notes:** a terminal open is refused while the kill switch is on; running
  terminals end when it goes on or when their owner loses Admin; a terminal into a control-plane container
  (`whiskers`, `socket-proxy`, …) needs an Admin approval and is opened again after it.

- R1-16: **Whiskers protects its own control plane, and free text no longer slips past protected resources.** A
  built-in list — containers named `whiskers*`, `serverwatch*`, `socket-proxy*`, `docker-socket-proxy*`,
  `ghostunnel*`, `step-ca*`, `nginx-proxy-manager*` and those in `WHISKERS_SELF_CONTAINERS`, plus the data
  volume's directory under `/var/lib/docker/volumes/` (or the host directory in `WHISKERS_DATA_HOST_DIR`, which
  `install.sh --data` now sets), mTLS / step-ca / ghostunnel directories (also every
  managed host's `/opt/dockerproxy`, which holds its tunnel key and CA) and the Docker
  socket (also as a mount of a directory above them, such as `/` or `/var/run`) — is protected on every surface for
  every change, whatever the guardrail policy lists. Touching it (or a resource the policy protects) is refused for
  API keys, agents instructed over MCP, automations and Operators; an Admin in the web UI or the web chat gets an
  approval at Admin level instead, single-use and audited (with *Require a second approver* on, another Admin must
  give it). Protected names are now also found in commands, SQL and compose files (word by word, with path
  segments; a command is read as the shell reads it, so quoting tricks such as `whis""kers` do not hide a name), by
  a container id or id prefix written in a command, by a container's other names, among the stopped containers a
  cleanup removes, and in what a deployment mounts — `deploy_app` volumes and every host path and volume of a compose
  deployment, also a local volume that binds a host path through `driver_opts: device:` and the volumes a service
  takes over with `volumes_from` (another service's, or a container's — looked up on the server by id, name or id
  prefix; one that is not found refuses the deployment); a raw compose file is read as YAML for its mounts and names
  (flow style, anchors, escapes decoded), and one that is no YAML is refused (the Docker socket and the data volume are part of the control plane);
  the control plane's names apply to containers, volumes and networks, not to a server called `whiskers-vps`. Free
  text that is completed at run time counts as touching a protected resource: in a command anything the shell
  expands (`$VAR`, `$(…)`, backticks, `{a,b}`), `eval`, `xargs`, decoding, or inline code of another interpreter
  (`python -c`, `perl -e`, `awk … system(`); in SQL dynamic SQL, `COPY … PROGRAM`, file-reading functions,
  `U&"…"` escapes, inline functions and untrusted languages (`plpython3u`), and client commands the text is fed to
  (psql `\i`, `\gexec`, `\c`, `\!`; mysql `source`; sqlite `.read`; mongosh `load()`) and string escapes a driver
  decodes into a name (`b`, `\x62`). A `.env` saved in the web UI
  decides what the project mounts too (a whole short entry from a variable, `- ${SPEC}`, is expanded before it is
  read; relative sources — also of a long `type: bind` entry — resolve against the project directory the deployment
  runs with; mounts merged through any depth of YAML anchors, `volumes_from` a service of another file, and the
  files of file-backed `secrets` and `configs` count too). Besides its own reading, Whiskers has compose resolve
  every deployment on the server (`docker compose config`, with the planned files and `.env`) and decides every
  mount compose reports — `extends`, merges and interpolation as compose itself does them; what only compose found is
  decided as possibly from an `.env` (withheld); a deployment compose cannot resolve is refused. A container whose
  volumes a service takes over brings its own mounts along (read with `docker inspect`; unreadable refuses the
  deployment). The database a request runs in (`database`) counts as a name too, so Whiskers' own database on a
  shared database server is the control plane, and a qualified SQL name (`billing.orders`, `"billing"."orders"`)
  names each of its parts. A SQL import or seed decides the database it runs in. A mount whose source
  comes from an `.env` value is decided but never recorded (the audit keeps the length of `mounts`, the decision
  record `(withheld)`). A policy pattern that equals a control-plane pattern makes a hit approvable only where the
  control plane's own check finds it too (not a server named `whiskers-vps`); text that cannot be analysed is
  refused first, whatever names stand beside it. A withheld mount is never quoted in a refusal either. The
  SQL console, SQL import and seed and the raw compose deploy now hand their payload to the policy (Artisan
  migrations carry no free text: their command is fixed); the audit keeps only its length, and an approval raised from the web UI shows only the length of a
  command, SQL or compose file (refusal reasons never quote words of it); a card that shows the text (an agent's
  chat or MCP call) masks SQL password literals (`PASSWORD '…'`, `E'…'`, `IDENTIFIED BY …` / `"…"`, also with comments
  between the tokens) as it masks other
  secrets, value by value, and so do the audit log and the host-command logs. **Upgrade notes:** a scheduled cleanup
  task no longer removes unused networks (which ones are unused cannot be decided before the prune): it lists the
  stopped containers first, decides exactly those and removes them by id (`docker rm`, no `container prune`: one
  that stops meanwhile stays), then dangling images and build cache; a run needs `docker compose` v2 on the server
  for compose deployments (it resolves each deployment with `docker compose config`); an install whose data lives in a host directory (not the
  `whiskers-data` volume) set up before this release should add `WHISKERS_DATA_HOST_DIR=<that directory>` to the
  container's environment, so Whiskers protects it; calls that used to
  pass are now refused or need an Admin approval — an MCP key or a scheduled task restarting the Whiskers container
  or its proxy; a command or query that merely mentions a protected name (also inside a domain such as
  `whiskers.example.com`); any command from MCP or automation that uses a shell variable or `$(…)` (for example a
  scheduled CustomCommand with `$(date +%F)`), `xargs`, `awk '{print $1}'`, `python -c`, a pipe into a shell or an
  interpreter (`curl … | sh`), a script (`bash deploy.sh`, `./deploy.sh`, `/opt/x/run.sh`, `python3 x.py`), `make` or
  `npm`, any interpreter (`python3 …`, `node …`), `xargs` by any path, `find -exec` from `/`, a changing
  `docker compose` / `docker-compose` / `docker stack` command (`up`, `down`, `rm`, … — the compose file, not the
  command, says what it touches), `docker system|container|volume|network prune` (what it removes is named nowhere;
  `docker image prune` stays allowed), a compose bind mount whose relative source climbs to `/` (`../../..`), or `rm`/`mv`/`chmod` on `/var/lib/docker`; renewing or issuing mTLS certificates and restoring a self-backup
  (an Admin approval in the web UI, refused for keys);
  editing an nginx site that proxies to Whiskers; deploying your own `step-ca` or `ghostunnel`; SQL with dynamic
  statements or inline functions (`EXECUTE`, `PREPARE`, `CREATE FUNCTION`, typical in migrations and `pg_dump`
  imports — a scheduled SQL task is refused, a UI import needs an Admin approval, and is refused outright while
  the guardrail policy lists protected resources of its own, as the default policy does), also `decode(`, `chr(` and
  `convert_from(`; a command or compose file with a 12-digit hex word (an image digest, a git SHA) while the
  server's containers cannot be listed; a compose deployment
  that mounts the Docker socket, `/`, `/var/run` or `/var/lib/docker` (cAdvisor, node-exporter, Portainer) or a volume
  whose source uses a variable the project's `.env` does not set; a scheduled `volume.backup` of the
  `serverwatch-data` volume run by a task (refused — back Whiskers up with its own self-backup instead); AutoUpdate
  and AutoPatch of the Whiskers container itself (refused each cycle with an `automation_refused` notification —
  update Whiskers from the web UI); a raw compose file in the UI deploy that uses a variable without a default
  (Admin approval, since the target directory's `.env` is not read; refused while the policy lists protected
  resources of its own, as the default policy does). Rename a
  container that only shares the prefix, put the command into a script file on the host, or run the action from
  the web UI as an Admin. The Admin exception covers the control plane only: a resource protected by the policy's
  own pattern stays refused for everyone, as before, wherever in the call it is named — and while the policy lists
  patterns of its own, free text that cannot be analysed is refused for everyone too (it could name one). The data
  volume is recognised under a compose project's prefix as well (`prod_serverwatch-data`).

- R1-15: **who may decide an approval, until when, and for how long a chat's approval lives.** Approving and
  rejecting follow the same rules: the approver holds the action's effective level (Viewers decide nothing, and the
  chat now passes the approver's current role), and nobody decides their own non-interactive request — direct MCP,
  AI trigger, scheduled task, webhook (PC-D4). The agent chat and UI clicks may still be confirmed by the person who
  made them, recorded as a self-approval on the Approvals page, unless the new guardrail option **Require a second
  approver** is on (off by default; left out of the stored policy while off, so existing policy versions stay the
  same; a legacy single-policy file that sets it keeps it when migrated). An expired approval can no longer be decided (checked by the store and the database update; a decision whose write waited past the expiry is not committed), overdue
  approvals are expired every 30 seconds so a waiting chat is released, and a grant is usable only within its
  lifetime (checked again when its use is committed; on PostgreSQL inside its retrying execution strategy). Clearing or closing a chat, leaving the page or losing the browser connection stops its run and cancels
  its pending approvals, so approving one elsewhere can no longer resume it unattended (calls it left open are recorded as not completed, so the chat can go on); sessions wait on unique operation ids instead of provider tool-call ids. One
  principal has at most 25 approvals waiting, counted for the user behind every chat too. The chat's Approve and
  Reject buttons stay disabled until the approval is recorded, and both decide only through it. New optional setting
  `WHISKERS_PUBLIC_URL` (`Whiskers:PublicUrl`): the address people open Whiskers at; when set, approval
  notifications sent by e-mail, Telegram, ntfy and other channels carry a direct link to the approval. Approval notifications name the approval, risk, target and a link to
  it (the target's kind, and its id and server only where Whiskers took them from its inventory; the bell opens the
  card), never the arguments or a value a caller can put into the call; Viewers no longer see an approval's
  arguments. Additive migrations (SQLite + PostgreSQL)
  add `RequesterIds` and `SelfApproved` to `Approvals`. **Upgrade note:** a pending approval raised before the
  upgrade records no requester and is cancelled at startup — the caller asks again; approved, unused ones keep their
  grant.

- Tests: the full-app boot tests that switch modules off ("lean boot") or set `Auth:Disabled` now really boot that
  configuration. Program reads those settings while the builder is set up, before a test's in-memory configuration
  is layered in, so these tests had been booting every module (and the real authentication setup) while reporting
  green. `BootSettings` passes the settings as environment variables too (all or nothing, restored afterwards);
  every boot test that sets one of these now asserts the complete module set and the authentication setup it ended up with
  (`BootSettings.AssertBoot`, an inherited flag fails it too), lean boots switch default-off modules off explicitly
  (`BootSettings.OnlyCore`), and `BootMatrixTests` compares against module sets written out per case. The only product
  change: `ModuleCatalog.All()` is internal instead of private, for that check.

- R1-14: **approvals are durable and fully bound.** Every approval is written to the new `Approvals` table (additive
  migrations for SQLite and PostgreSQL; the SQLite → PostgreSQL migrator copies it) before it is shown, decided or
  used, so a restart no longer loses a decision or lets an approval run twice, and the history on the Approvals page
  is no longer cut at 200 entries. An approval is now bound to the principal, the credential it authenticated with,
  the capability and its level, the normalized arguments, the **resolved target** and the **policy version** it was
  decided under, and stays usable for ten minutes after the approval: a call that differs in any of them — another
  key, another target, a changed preset — needs a new approval. Using an approval is a single conditional update in
  the database, so of concurrent calls (also across a restart) exactly one runs; an approval that cannot be recorded
  is not raised and the call is refused. **Upgrade notes:** the migration runs on the first start. Approvals that
  were pending in memory before the upgrade are gone (as on any restart before); after the upgrade a restart keeps
  open approve-then-retry approvals and unused grants, while a pending agent-chat approval is cancelled (its chat
  session ended with the restart) — ask the agent again. To go back, start the previous image: it ignores the new
  table.

- R1-13: **AI triggers are read-only unless an Admin grants them writes** (PC-D6, PC-D7). A trigger may change
  something only when an Admin lists both the **capabilities** it may perform and the **targets** (names or ids, `*`
  and `?` as wildcards) it may act on; saving such a grant makes that Admin the trigger's **owner**. The run then
  starts with the owner's current level, and the Mutation Gateway checks **every** mutation made for the run — also
  one nested in it, such as the actions of a scheduled task the trigger runs, and every resource an operation also
  touches, such as the containers a network removal cuts off — against the grant and the owner's current authority:
  anything outside the grant is refused. Targets are matched on what Whiskers resolved (an id, or a name from its
  inventory), never on a name the call supplied, and a touched resource counts as granted by any one of its names
  or ids. Deleting a Hetzner snapshot can no longer be granted to a trigger at all (the grant could not bound which
  snapshot); a saved grant for it is dropped. Saving the trigger page after another admin saved or a trigger was suspended is refused with a hint to reload, so a stale page cannot lift a suspension or widen a grant again. **Every trigger now has an owner** (the Admin who created it, changed its
  grant or switched it on again), re-checked before each run; an owner who lost the right (demoted below a granted
  capability, removed from the whitelist, account deleted) suspends the trigger and notifies the Admins
  (`automation_suspended`); switching it on again makes the Admin who does it the owner. Every Confirm of a trigger
  run — also for mutations nested in it — is refused in the gateway instead of raising an approval, and every audit
  row of the run (nested ones included) carries `trigger:<id>`. The event that starts a run (container, image and
  server names, the log line) is handed to the agent as delimited JSON data with a random marker per run, never as
  part of its task. The AI triggers page shows the grant, the owner and a suspension, and its help text no longer
  claims that confirmations reach you. **Behaviour change / upgrade notes:** every existing trigger is **read-only**
  after the upgrade, whatever `MaxLevel` it had (the field is gone and ignored when read): a trigger that used to
  restart or update containers now only investigates and reports. Existing triggers also have **no owner**, so each
  is suspended (with a notification) the first time it fires; to keep one, open AI triggers, switch it on again
  (or off and on for one that never fired), grant the capabilities and targets it needs and save — you become its
  owner. Operations that also touch other resources need those granted too. Nothing in `ai-triggers.json`
  needs to be edited by hand; to go back, start the previous image (it ignores the new fields, and its triggers
  fall back to `MaxLevel=write`).

- R1-12: every policy decision of the Mutation Gateway is **persisted before anything acts on it** (new tables
  `PolicyDecisions` and `PolicySnapshots`, additive migrations for SQLite and PostgreSQL; the SQLite → PostgreSQL
  migrator copies both). A record holds the principal, surface, capability and required level, the resolved target,
  a normalized input hash (keys sorted at every level), the policy version (content hash of the exact policy
  evaluated), the preset, the matched rules, the verdict and the reason; a Deny is recorded too. If the record cannot
  be written the mutation is refused. Audit rows (intent, outcome, refusal) carry `decisionId`; an approval names the
  decision and preset that asked for it. Policy versions are content-addressed: an edited preset is a new version and
  stored versions never change. **AI trigger runs are pinned** to the trigger's preset for the whole run (the agent
  session and the gateway both decide against it); switching or editing the active preset no longer reaches a running
  trigger, the global kill switch still does, and a fail-closed guardrail store stops it. Mutations nested in a
  trigger's call (a scheduled task it runs, also one a person created) are decided against the trigger's preset with
  the agent rules (tool lists, forbidden arguments, confirmation), and a cleanup task run there protects what that
  preset protects. Decision records never store URL credentials (a Git app's repository URL). **Behaviour change:** a
  trigger whose preset no longer exists does **not run** any more (it used to fall back to the first preset); the
  refusal is audited (`agent.run`, unsuccessful) and the AI triggers page marks the missing preset. Likewise an active
  preset that does not exist yields the fail-closed policy instead of the first preset. An agent session has the
  gateway decide (and record) a call before it pauses for an approval or reports a block, and follows exactly that
  recorded verdict, so the approval names the decision that asked for it. A decision that cannot be recorded is
  refused with a localized message. **Upgrade notes:** check that every AI trigger names an existing preset. The migration only adds the two
  tables and runs at start; if it fails, Whiskers does not start and nothing else has changed — restore `/app/data`
  from the backup taken before the upgrade (or a Whiskers self-backup) or start the previous image again, which
  ignores the new tables. Decision rows are not pruned yet.

- R1-11: every security configuration write goes through the Mutation Gateway as an **Admin-only** capability:
  the Settings sections (`config.save`), whitelist (`config.whitelist`), roles (`config.roles`), MCP tool and key
  permissions (`config.mcp_permissions`), the agent's LLM provider, key and system prompt (`config.agent`), MCP key create/delete (`mcp.key_create`, `mcp.key_delete`), guardrails and
  presets (`guardrails.save`, from the Guardrails and AI operations pages), vault secrets (`vault.secret_set`,
  `vault.secret_delete`, also a Kubernetes server's kubeconfig) and registries (`registry.save`, `registry.delete`).
  Each is authorized at the moment of the click (a Viewer or a demoted user is refused whatever the page shows),
  decided and audited before the store is written; secret values and keys are never arguments. While the kill switch
  is on it refuses all of them; the one exception is the new **Switch kill switch off** button on the Guardrails page
  (`guardrails.kill_switch_off`: Admin, a person in the UI only, audited, changes nothing but `ReadOnlyMode` of the
  active preset). A fail-closed guardrail store (unreadable `guardrails.json`) refuses every save too, also of the
  guardrails — **upgrade note:** to recover, fix or remove `guardrails.json` on the host and restart Whiskers
  (removing it recreates the SafeDefault preset). Agent provider saves are serialized as well. MCP
  permissions, the vault and registries now publish a change only after it is written, a Settings save of several
  sections (notifications, channels) is one file replacement, and a registry credential is stored under a new vault
  key per change that only takes effect with the registry entry naming it (a pull during the change reads the new one);
  a changed kubeconfig goes to a new vault key that only takes effect with the saved server record;
  removing a Kubernetes server writes the server list first and only then removes its kubeconfig, within the one
  `server.remove` decision (`ServerRemoval`) — so a failed write leaves the live configuration as it was (`UiMutationGatewayCoverageTests`). The first-run setup wizard writes the whitelist outside
  the gateway, before any admin exists (documented baseline entry).

- R1-09: webhooks have an **owner** — whoever creates the webhook, or an admin taking it over on the Webhooks page
  (new Owner column). An external call to `/api/webhooks/{id}` now runs its action through the Mutation Gateway on
  the owner's authority, re-checked at each delivery — every delivery is decided and audited, also one without an
  owner or whose target cannot be resolved — for all four actions: restart and rebuild as
  `container.restart` / `container.update` on the resolved container (a stopped one too; an ambiguous id prefix is
  refused), compose deploy as `compose.deploy` plus a decision per container of its plan, Git deploy as
  `gitdeploy.deploy` by the webhook's owner and then by the app's owner (R1-08). A webhook without an owner, or whose
  owner lost the authority (role, whitelist, account, key), is refused, switched off and an admin is notified; any
  other refusal (kill switch, protected resource, unknown target, the Git app owner's, unwritable audit) is reported
  to an admin and leaves it on. A webhook whose server was removed can still be switched off and deleted. A compose planning error is not passed into the audit, notifications or the webhook log (it can quote `.env` values); the Test button shows it. A compose project name interpolated from the `.env` or `name:` is recorded in decisions and the audit as its source expression (e.g. `${DB_PASSWORD}`), a literal name as it is (`ComposePlan.ProjectNameForAudit`). Creating,
  taking over, rotating the secret, switching on/off and deleting a webhook are decided by the gateway too
  (`webhook.save`, `webhook.rotate_secret`, `webhook.set_enabled`, `webhook.delete`, all Admin), on the stored
  webhook; the secret is never a decision argument. The Webhooks page's Test plans the action as the trigger does
  (`WebhookTargets`), decides it as the clicking person, and the signed call then runs on the owner's authority. Git
  deployments and webhooks fired by their owner now get the rules for a person (PC-D1), not an agent's
  (`WebhookMutationTests`).

- R1-08: Git deployments have an **owner** — whoever saves the app, or an admin taking it over on the Git deploy
  page. A webhook deploy runs on the owner's authority, re-checked every time through the automation runner: the
  deployment and every container of its compose plan are decided as the owner; an owner who lost the authority (role,
  whitelist, account) is refused before anything is fetched. The decision is made on the **stored** app (the target
  resolver looks up its name, server and revision; another server or an outdated revision is refused), and the
  deployment runs exactly that snapshot: the app must still be at the same revision with the same inputs before the
  fetch and again before the build, otherwise it is refused. A save from a stale copy is refused too. The Webhooks
  page's Test of a Git deploy decides as the clicking person and then runs on the owner's authority
  (`GitDeployMutationTests`).

- R1-07: every compose deployment is planned before anything is written (`ComposePlan`, `IComposePlanner`): the
  project `docker compose` really uses (`-p`, else `COMPOSE_PROJECT_NAME` from the `.env`, else the top-level
  `name:` with `.env` interpolation, else the directory name, normalised as compose does), the compose files (a
  directory's `COMPOSE_FILE` or default file plus override, or the container's recorded `config_files` for a `.env`
  change), and every existing container the deployment recreates, removes as an orphan (`--remove-orphans` or
  `COMPOSE_REMOVE_ORPHANS` in the `.env`) or collides with by `container_name` / default name. Each of those containers is decided — the compose editor, App Store, Deploy page,
  container `.env` save, Webhooks **Test** and Git deploy once per container, MCP / agent `deploy_compose` and
  `set_container_env` as related resources of the one decision — before the first write. Execution then runs the
  plan with an explicit `-p`, `--project-directory`, `-f` (and `--env-file` when there is one); right before writing
  it plans again and refuses if that plan touches a container that was not decided. What cannot be planned (a
  variable without default in a name or a compose switch that is not in the `.env`, a multi-line `.env` value,
  `include`, `extends` from another file, invalid YAML, an unreadable file) refuses the deployment
  (`ProtectedResourceResolutionTests`). A `${VAR:-default}` whose variable is not in the `.env` is planned with the
  default; compose gives a value from the host's environment precedence over the `.env`, which the plan does not
  see. The host environment's `COMPOSE_REMOVE_ORPHANS` / `COMPOSE_IGNORE_ORPHANS` are cleared for the command.
  A compose file with several YAML documents is refused. A Git deploy fired by the webhook **Test** button checks
  its plan against the Test decision, which covers no containers yet: until R1-08 it is refused when the deployment
  touches an existing container.

- R1-06: every mutation's target is resolved against the inventory before policy — servers (an empty server id is
  the configured default server, decided by the gateway), containers, networks with the containers attached to them,
  cloud servers (Whiskers id or name), scheduled tasks (id or unique name), DNS zones (normalised, within
  `Dns:AllowedZones`) and Kubernetes workloads (their **live** controller). An unknown or ambiguous target is refused
  before policy. MCP and agent tools then run on the resolved identity (canonical id and server id), not on a second
  lookup of what the caller typed. A protected container attached to a network that is removed, or connected to /
  disconnected from one, and a workload's current controller are matched against protected patterns
  (`TargetResolutionTests`).

- R1-06: AutoUpdate no longer turns the server id `local` into "the default server": a container listed on `local` is
  decided and recreated on `local`, also when another server is the default. Webhooks decide (Test button) and run
  on the same server and container (`WebhookTargets`).

- R1-06: a tool that takes a `serverId` always receives the server the gateway decided on, also when the caller left
  it out (a missing server is the default server to the gateway, but some tools read it as `local`). AutoPatch, the
  Docker workload provider and the Networks, Scheduled tasks, Volume backups, App Store and Webhooks pages no longer
  turn `local` into "the default server" either. Refused targets carry a reason code (unknown, ambiguous, not
  resolvable) and are shown in the user's language.

- R1-05 review: the two refusals the effective requirement adds (underlying capability switched off, requirement
  not determinable) are shown in the user's language on the pages; covered end to end through the real gateway,
  approval gate and store (`SchedulerApprovalEndToEndTests`).

- R1-05 review: interactive approvals (agent sessions) are raised at the operation's effective requirement, and the
  approval gate refuses to reuse an approval raised below it, so an Operator can no longer approve a scheduled custom
  command, retention or self-backup. The scheduler requirement resolver reads `taskType` in every form the tool
  accepts (name or number) and fails closed to Admin otherwise, reads `maxBackups` like the tool (`"0"` is no
  retention), and applies stricter or disabled settings of the underlying capability from Settings → MCP.

- `scheduler.delete` is Write on every surface: the Scheduled tasks page now takes the level from the capability
  catalog (it required Admin). Deleting a task is not root-equivalent; creating, updating and running stay gated by
  the task's effective requirement.

v1.0 readiness, work package 1 (immediate fixes from the Phase 0 audit, `docs/v1/TECHNICAL_AUDIT.md`).

### ⚠️ Upgrade notes

- **R1-47 (maintainers): new required checks.** Make the `ci` jobs `build-test` and `artifact` and the `security` jobs
  `dependency-vulnerabilities`, `dependency-review`, `secret-scan`, `static-analysis` and `codeql` required on `main`, and
  enable the repository-hosted controls listed in `docs/operations/ci-and-release-checks.md` (dependency graph — the
  dependency review fails without it —, code-scanning merge protection, tag rules for `v*`). CI now needs Docker on its
  runner (the PostgreSQL suites fail without it), and the in-process and Docker test floors in `ci.yml` must be raised
  when the suite grows; a new mandatory test goes into `.github/required-tests/` in the same change. The upgrade check
  needs a published release below the version under test (N−1: below the release version, on pull requests below the
  `<Version>` of `Whiskers.csproj`), pulled anonymously from GHCR, and pulls the pinned `node:24-alpine` image for its
  data tool.

- **R1-11: while the kill switch is on, configuration can no longer be saved** — Settings, roles, whitelist, keys,
  vault, registries and guardrails included. Switch it off with the button on the Guardrails page first. Registry
  changes now need Admin (they had no role check). An unreadable `guardrails.json` can no longer be fixed by saving
  on the Guardrails page: repair or remove it on the host and restart Whiskers (without the file the safe default is
  created).

- **R1-09: webhooks created before this release have no owner and stop working.** An external call (and the Test
  button) is refused and switches the webhook off (an admin is notified) until an admin takes it over on the Webhooks
  page (person icon in the Owner column) and switches it on again; CI callers keep their URL and secret. A webhook whose owner later loses Admin (restart and rebuild need Operator),
  is removed from the whitelist or loses their account is switched off on its next call, and an admin is notified.
  Managing webhooks still needs Admin, now also checked by the gateway (kill switch, audit).

- **R1-08: Git deploy apps saved before this release have no owner.** A webhook (or the Webhooks page's Test) no
  longer deploys them until an admin takes them over on the Git deploy page (person icon in the Owner column);
  deploying from the Git deploy page itself still works. Saving an app makes you its owner. A webhook deploy now fails
  when the owner lost Admin, was removed from the whitelist or lost their account.

- **R1-07: compose deployments name their project and files explicitly.** Whiskers now runs
  `docker compose -p <project> --project-directory <dir> -f <file>…` with the project and files it planned, instead of
  `cd <dir> && docker compose up`. The project is the one compose itself would have picked, so existing projects keep
  their name. Differences: a deploy from the compose editor, App Store or MCP `deploy_compose` uses exactly the file
  it writes — a `compose.yaml` or `docker-compose.override.yml` left in `/opt/deployments/<project>` is no longer
  picked up; a webhook deploy directory must contain a compose file itself (compose no longer searches parent
  directories); a `.env` change keeps the container's project even if the new `.env` sets another
  `COMPOSE_PROJECT_NAME`. A compose file whose `name:` or `container_name` uses a variable without default that is
  not in the `.env` (only in the host's environment), a `.env` whose compose switches do the same or that holds a
  multi-line value, and a file using `include` / `extends` from another file can no longer be deployed through
  Whiskers. `COMPOSE_FILE` in a webhook directory's `.env` is honoured as before; in `/opt/deployments/<project>`
  (editor, App Store, MCP) the written file is used instead.

- **R1-06: unknown or ambiguous targets are refused.** A call naming a server, network, cloud server or task that
  does not exist, or a name that fits more than one (two servers called "web", a network id prefix shared by two
  networks, two tasks with the same name), is refused before anything runs; use the id. A server id that differs
  from the stored one only in case is resolved to the stored id.

- **R1-06: an MCP or agent call without `serverId` acts on the default server, for every tool that takes a
  server.** Some tools
  (`set_container_env`, database backups, …) used to read a missing server as `local` while the gateway decided on
  the default server; they now receive the default server's id. Pass `serverId` where you mean `local`.

- **R1-06: a scheduled task saved without a server acts on the server with the id `local`**, for its gates as well
  as for its execution (the execution always did; the decision used the default server). Set the server explicitly
  where `local` is not the intended machine.

- **R1-06: a webhook saved without a server acts on the server with the id `local`.** Restart and rebuild webhooks
  used the default server before, compose-deploy webhooks used `local`; now all four actions use `local`. If your
  `local` server is not the machine such a webhook should act on (or you have no server with that id), set the
  webhook's server explicitly. A webhook whose container id prefix matches more than one container no longer acts
  on the first match; it fails with "Ambiguous".

- **R1-05: one capability level table; several MCP tools now require Admin.** The capability catalog
  (`CapabilityCatalog`) is now the single source of the level of every mutating capability; MCP's
  `DefaultToolLevels` takes its mutating entries from it. Where the UI and MCP disagreed, the stricter level
  won: no MCP level was lowered. One UI level was: creating and deleting a scheduled task on the
  Scheduled tasks page now needs Operator (was Admin), and the gateway then raises it to Admin per task type
  (custom command, self-backup, DB backup, retention) — Operators can now manage restart, cleanup and volume
  backup tasks without retention there. API keys and agents at Write lose these tools:
  - Firewall `add_firewall_rule`, `remove_firewall_rule`; nginx `update_nginx_config`; systemd
    `manage_systemd_service` — Write → Admin (their UI pages already required Admin).
  - `set_container_env` — Write → Admin: it writes the compose `.env` (any key) and runs `docker compose up -d`,
    the same root-equivalent operation as saving the `.env` on the container page (already Admin).
  - Database dump `backup_database` and certificate renewal `renew_ssl_certificate` — Write → Admin (their UI
    pages already required Admin). A scheduled **DB backup task** now needs an Admin owner, with or without
    retention.
  - Scheduled tasks: creating and deleting a task follow the catalog on the page as well (Operator); the gateway
    then holds each task type to what it runs (a custom command, retention, a DB dump or a self-backup needs Admin).
  - Networks `create_network`, `remove_network`, `connect_container_to_network`,
    `disconnect_container_from_network` — Write → Admin (Networks page requires Admin).
  - Cloud `cloud_power_on`, `cloud_shutdown`, `cloud_reboot`, `cloud_hard_reset`, `cloud_create_snapshot` and
    Hetzner `hetzner_disable_rescue`, `hetzner_enable_backups`, `hetzner_disable_backups`,
    `hetzner_change_server_type`, `hetzner_delete_snapshot` — Write → Admin (Cloud page requires Admin;
    `hetzner_enable_rescue` was already Admin).
  - `gitdeploy.deploy` (UI) — Write → Admin: it is root-equivalent. The Git deploy page now shows the deploy button
    to Admins only (it was shown to Operators).
  - `create_scheduled_task` / `run_scheduled_task` stay Write, but the requirement — and therefore the approver's
    level — is now resolved per task before the caller check and the approval: CustomCommand (`host.exec`),
    SelfBackup (`selfbackup.create`/`selfbackup.prune`) and DB/volume backups with retention (`maxBackups` > 0,
    the default 7; `db.backup_delete`/`volume.backup_delete`) require Admin, and so does every DB backup (a dump
    holds every row). Pass `maxBackups=0` to keep a **volume** backup task at Write. The Scheduled tasks page sends
    the same arguments and asks the same resolver for "Run now". Retention is read with the executor's own rule (`TaskExecutor.RetentionOf`), the one the
    Scheduled tasks page already uses for "Run now".

- **Deploying from the UI now requires Admin** (was Operator): the deploy form, the compose editor and the app store.
  A compose file can mount `/` or run privileged, which is root on the host. The MCP deploy tools already
  required Admin.

- **The first-run wizard asks for a one-time setup token.** It is printed by the installer and written to
  `/app/data/setup-token.txt` (mode 0600), or preset with `WHISKERS_SETUP_TOKEN`. Instances that already have
  an admin are not affected.

- **`AUTH_DISABLED=true` now only serves the trusted network.** Allowed are loopback, RFC1918, CGNAT/Tailscale
  and IPv6 ULA clients, with an IP or `localhost` as host name and no cross-site `Origin`. Behind a reverse
  proxy with a host name, add it via `Auth__DisabledAllowedHosts__0=…`.

- **MCP and agent mutations run through the Mutation Gateway** (ADR-0005 step 2.1). What changes for callers:
  - A tool switched off in Settings → MCP, or raised to a stricter level there, now applies to web users, the
    agent and AI triggers too, not only to API keys.
  - A key's `AllowedTools` list can no longer lift it above its level: a Read key listing `start_container` is
    refused. Give such a key the level the tool needs.
  - An unknown or ambiguous container is refused before the guardrails run, with the same "Container not found" /
    "Ambiguous container id" text as before.
  - Mutating tools have deadlines (2 to 30 minutes by capability; `execute_command` 11 minutes, above its own
    600-second limit). A tool that runs longer is reported as failed.
  - The agent runs a mutation that needed a human only with the approval registered for exactly that call. A
    confirmation answered in the chat without a registered approval no longer executes; the agent is told to
    repeat the call, which raises a regular approval.
  - Each MCP or agent mutation now leaves **two** AuditLog rows: the gateway's row (decision, approval, intent,
    outcome; `Details` is JSON with `operationId`) and the tool's own result row as before.
  - A mutating tool that answers with an error text (container not found, command exit code ≠ 0, failed backup …)
    now returns that text with `isError: true` over MCP, and the agent sees it as a failed call. The gateway row
    records it as `Failed`.

- **UI actions run through the Mutation Gateway** (ADR-0005 step 2.2, PC-D1). What changes for people using the web UI:
  - The **read-only kill switch** and **protected resources** now apply to UI actions too. With the default preset
    the Whiskers container itself (`whiskers*`) can no longer be stopped, restarted, updated or removed from the
    Dashboard or the container page; remove the pattern from the active guardrail preset if you need that.
  - A tool **switched off** in Settings → MCP, or raised to a stricter level there, also switches off or raises the
    matching UI action (e.g. `restart_container` → the restart buttons).
  - A refused action shows the reason as a warning; the gateway's AuditLog row records it.
  - Also covered: Kubernetes start/stop/restart on the Dashboard (decided on the Deployment/StatefulSet the pod
    belongs to), saving a container's `.env` (every service of its compose project is decided, since
    `compose up` may recreate them all), Artisan migrate/rollback, SQL file import, deploys from the compose editor
    and the App Store (every existing container of the project is decided as well: a protected `payments-db` in
    project `payments` refuses the deploy), the **Test** button of a webhook (decided as its real action — restart,
    rebuild, compose deploy, Git deploy — on its real target), adding, editing and removing a server, saving AI
    triggers, server onboarding, and creating, deleting and restoring a Whiskers self-backup.
  - A **cleanup task** (`docker system prune`) is refused while a stopped container matching a protected pattern
    exists on that server — the prune would remove it. A task on `local` checks the local server, not the default
    server.
  - "Run now" of a scheduled task is checked for the task's own action and target as well (a restart task on a
    protected container is refused); over MCP, `run_scheduled_task` also needs the caller's access to
    `restart_container` / `backup_database` / `execute_command` for those task types.
  - A UI action whose service answers with a failure instead of an exception (failed command, query error,
    `false`) is recorded as `Failed` in the gateway row, not `Executed`.
  - The gateway row no longer stores free-text arguments (SQL text, commands, file content); only their length.
  - No approval is asked for a person's own click; tool allow/deny lists and argument patterns stay agent rules.

- **Scheduled tasks, AutoUpdate and AutoPatch run on their owner's authority** (ADR-0005 step 2.3, PC-D6). What
  changes:
  - A new task records who created it (the signed-in user, or `key:<id>` for an MCP key; for the agent, the user or
    key it acts for). Creating a task without such an owner (an AI trigger, a session without an email) is refused. Every run re-checks that
    owner's current role or key level and refuses the run when it no longer covers the task (owner demoted, removed
    from the whitelist, key deleted or disabled). A refused run is recorded as failed, sends an
    `automation_refused` notification, and leaves a gateway AuditLog row.
  - **Tasks from before this version have no owner, and owners are required** (`Automation:RequireOwner`, default
    `true` since R1-04): an ownerless task is refused and switched off until an Admin takes it over — "Take over
    all" or per row in Scheduled Tasks, which shows them with a warning. To keep them running for a while instead
    (grace period, audited as "no owner" and limited to the capability they run), set
    `Automation__RequireOwner=false` explicitly.
  - **AutoPatch does not run until an Admin takes it over** in Settings → Automation owners. `AutoPatch:Enabled`
    alone is no longer enough; the nightly cycle logs a warning and skips. AutoUpdate without an owner is refused
    as well (unless the grace period is switched on as above); take it over there too.
  - What a task needs from its owner: a **custom-command** task needs an Admin owner (it runs as root on the host);
    a backup task whose **retention** deletes old backups (volume and DB backups) needs an Admin owner for the
    deletion; a **self-backup** task needs an Admin owner; the **cleanup** task (`docker system prune`) needs
    Operator. Restart tasks and volume backup tasks without retention need Operator; **every DB backup task needs
    an Admin owner** (R1-05: a dump holds every row) — take an existing DB backup task owned by an Operator over as
    an Admin, or it is refused.
  - **A task owned by an API key keeps every rule that binds the key**: tool deny/allow lists, forbidden arguments
    and confirmation. Where the policy would ask for a confirmation (the default preset does for every write by a
    key), the run is refused — nobody is there to confirm it. Have a person take the task over, or adjust the
    preset for that key. The refusal is final: it raises no approval in the Approvals page, so approving something
    there can no longer let the next scheduled occurrence through.
  - **A custom-command task is checked against the forbidden-argument patterns of today**, on every run, not only
    when it was created (key-owned tasks). A command that a pattern added later matches is refused.
  - **A task whose owner lost its authority** (removed or demoted user, deleted or disabled key) is flagged in
    Scheduled Tasks; an Admin can take over any task there, alone or with "take over all".
  - **A refusal reason never quotes a command or SQL text**: the gateway removes payload values before the reason
    reaches a log line, a notification, the task history or the UI.
  - **The AutoPatch preview over MCP** (`preview_auto_patch`) runs on the caller's authority and decides every
    container like the nightly cycle; a key whose preset confirms writes gets every container refused.
  - **AutoPatch decides every container on its own** (`container.update` of that container), in a dry run too: a
    protected container, or one the owner may no longer update, is skipped as `Aborted (refused: …)` instead of
    patched or tried. A patch that ends as `Unknown`, `RolledBack` or `RollbackNotVerified` is audited as failed,
    and so is the night it belongs to.
  - **An unreadable or invalid `automation-owners.json`** (also a directory at that path, or a file that cannot be
    opened) blocks AutoPatch and AutoUpdate (logged as an error; AutoUpdate records a failed update) until an Admin
    takes **each** of them over again; it no longer counts as "no owner". Taking one over keeps the other blocked,
    across restarts; the unreadable file is kept as `automation-owners.json.unreadable-<timestamp>`.
  - The kill switch and protected resources apply to scheduled runs, AutoUpdate and AutoPatch. With the default
    preset a scheduled backup of a `whiskers*` / `serverwatch*` volume or a restart of the Whiskers container is
    refused; remove the pattern from the active preset if you rely on it.

- **Arbitrary SQL needs Admin everywhere** (PC-D3): the database query in the container page and the
  `execute_query` MCP tool were Operator / write.

- **Container shells need Admin** (PC-D2): `/terminal/{server}/{container}` and the terminal tab in the container
  page were Operator.

- **"Run now" needs Admin for a custom-command task, a self-backup task and any backup task with retention**
  (`maxBackups`): retention deletes old backups, which is decided as `volume.backup_delete` / `db.backup_delete` /
  `selfbackup.delete`. Restart, cleanup and backup tasks without retention stay Operator.

- **Unattended items need an owner and are suspended when it loses its authority** (R1-04, PC-D6, INV-04):
  - `Automation:RequireOwner` now defaults to `true`: a scheduled task or AutoUpdate without an owner is refused.
    Take such items over in Scheduled Tasks, or keep the grace period with `Automation__RequireOwner=false`.
  - A run refused because its owner lost their authority — local account deleted, removed from the whitelist,
    demoted below what the item does (including retention deletes of a backup task), API key deleted or disabled —
    now **suspends** the item: a scheduled task is switched off — take it over in Scheduled Tasks **and switch it
    on again** (the takeover does not switch it on by itself, since a task may have been off on purpose) —,
    AutoPatch / AutoUpdate are blocked until an Admin takes them over in Settings → Automation owners (AutoPatch
    also stops mid-cycle). If switching a task off fails, every run is still refused and an Admin is notified. Admins get an
    `automation_suspended` notification that no container mute hides. Policy refusals (kill switch, protected
    resources) still only report `automation_refused`.
  - With Google/OIDC sign-in next to local accounts, Whiskers now records which emails have a local account and
    which were seen signed in through Google/OIDC (`known-local-accounts.json` in the data directory). An owner
    whose local account is deleted loses their authority. **After the upgrade, items owned by a Google/OIDC user
    are refused (not switched off) until that user has opened Whiskers once**, or an Admin takes them over — an
    owner without a local account is only trusted once seen signing in federated (fail closed; the same applies if
    the record is lost).
  - Authority loss is judged by what the run actually needs: a level raised in Settings → MCP, and a key's tool
    list, count as well.
  - **Tasks from before provenance was recorded** (created before this release, owner set, no `CreatedVia`) may
    have come from an agent, so they now run with the agent's guardrail rules; with the default preset their writes
    are refused (`automation_refused`). Take each such task over in Scheduled Tasks to run it with the PC-D1 rules
    again — also a task you own yourself (the take-over button is shown for it, and "Take over all" includes it).
  - A task an agent created keeps the agent's guardrail rules when it runs (autonomy level and confirmation, tool
    lists, forbidden arguments). With the default preset such a task's writes are refused; an Admin who takes it
    over makes it a task of their own.
  - Taking an item over records the previous and the new owner's authority in the audit row.

- **Principals have stable ids** (R1-03, INV-01/06):
  - Owners of scheduled tasks, AutoPatch and AutoUpdate are now recorded as `user:<email>` or `key:<key id>`. The
    database migration `PrincipalIdsForTaskOwners` (which also adds `ScheduledTasks.CreatedVia`: who created the task,
    e.g. `agent:<session>`) and the owner file (on load) rewrite plain ASCII emails to `user:<email>`;
    any other value is left as it is and its runs are refused until an Admin takes the item over.
  - Each AI trigger acts as its own principal `trigger:<id>` instead of the shared pseudo key `ai-trigger`; its audit
    rows now name `trigger:<id>` as actor. Renaming a key or a trigger keeps its identity.
  - The MCP call log (Agent History) records callers as `key:<key id>` / `user:<email>` / `anonymous` (was the key
    name); audit rows of API-key calls name `key:<key id>`. The gateway audit `Details.principal` adds the session and
    the kind of the principal acted for.

- **The guardrail policy fails closed** (R1-02, INV-25):
  - A forbidden-argument pattern that is not a valid regex now **refuses every call** (it was ignored). Fix or
    remove the pattern in Guardrails; saving a configuration with an invalid pattern is refused.
  - A pattern or protected-resource glob that takes longer than 100 ms counts as a match and refuses the call.
  - An unreadable or invalid `guardrails.json` (broken JSON, no presets, duplicate names, a missing field or list
    entry, an active preset that does not exist) no longer falls back to another preset or to the default: every mutation is refused, agents and MCP clients get no
    tool, and the Guardrails page shows a single "Fail-closed" preset until an Admin saves a valid configuration.
    The file is not overwritten and a copy is kept as `guardrails.json.unreadable-<timestamp>`. An old
    single-policy file (from before presets) is still migrated into a "Standard" preset; fields it lacks get the
    restrictive default.

- **Guardrails and approvals no longer switch off with the Agent module.** With `Features:agent:Enabled=false`,
  direct MCP calls are still checked against the active guardrail preset, a `Confirm` raises an approval, and the
  Guardrails and Approvals pages stay in the menu.

### Security
- **Agent-created tasks ran with a person's authority, and a deleted local account kept its tasks running**
  (known items 6/7, PC-D6). See the upgrade note.

- **Identities were rebuilt from display names** (TA-31, C-12, INV-01/06): every AI trigger shared one identity, and
  renaming an API key split it into two in the logs. Principals now carry typed ids end to end.

- **Guardrail policy errors allowed the call, and the live policy could be changed without saving** (TA-32/33,
  C-09/15, INV-25/28). See the upgrade note. The store now hands out copies only, and a save becomes live only
  after it was written: a failed write leaves the previous policy active.

- **Unattended runs had no owner and were never re-checked** (TA-04, INV-04, B-10). Scheduled tasks, AutoUpdate and
  AutoPatch now run through the Mutation Gateway as an automation principal acting on behalf of their owner, whose
  authority is read again on every run. A task created by a key that was later deleted no longer keeps running
  with root on the host.

- **UI actions bypassed the kill switch and protected resources** (TA-26, INV-20/21, PC-D1). Every UI call to a
  mutating capability now goes through the Mutation Gateway.

- **Root-equivalent actions at Operator** (INV-19): container shells, arbitrary SQL and running custom commands now
  need Admin (TA-43, B-10).

- **The tool switch and stricter tool levels applied to API keys only** (TA-10, INV-15). They now apply to every
  caller.

- **`AllowedTools` could widen a key beyond its level** (TA-34, INV-16). The level is now checked first.

- **Protected resources are also matched against the resolved container** for MCP and agent mutations: a unique
  id prefix of a protected container no longer slips past the rule (part of TA-09; commands and SQL text are not
  covered yet).

- **Disabling the Agent module removed the guardrails from direct MCP calls** (TA-27, INV-26). The guardrail
  engine, its store, the approval store and the direct-MCP guard are core now; the MCP filter refuses every call if
  they are missing.

- **Critical — Operator could gain host root through the UI deploy** (TA-01). UI deploys need Admin now. App-store
  template values may no longer contain line breaks or control characters, which could inject YAML.

- **First-run setup could be claimed by whoever reached the port first** (TA-02). It now requires a one-time
  token. Removing or demoting the last Admin is refused at the service boundary, so setup can no longer be
  re-opened that way.

- **Local login had no brute-force protection** (TA-03). Five wrong passwords lock the account for 15 minutes,
  login POSTs are rate-limited to 10 per minute per client, and unknown accounts cost the same hashing time as
  real ones.

- **`AUTH_DISABLED` made every network client an Admin** (TA-11). The trusted-network, Host and Origin checks above
  apply. With auth enabled, a cookie carrying a synthetic identity (`AuthDisabled`, `AgentSynthetic`) is now
  rejected; before, anyone holding the DataProtection keys could forge an Admin cookie that skipped the whitelist.

- **The Trivy image is pinned by digest in the code default too** (TA-14). An operator-configured image without
  a digest now logs a warning.

### Added
- **Mutation Gateway, step 2.3 (ADR-0005).** `IAutomationMutationRunner` runs scheduled tasks, AutoUpdate and
  AutoPatch through the gateway on their owner's authority. Owners are shown and taken over in Scheduled Tasks and
  in Settings → Automation owners (`automation-owners.json` in the data directory). The bypass baseline shrinks from
  29 to 23 entries.

- **Mutation Gateway, step 2.2 (ADR-0005).** `IUiMutationRunner` runs a page's action through the gateway as the
  signed-in user. Sixteen pages use it; the bypass baseline shrinks from 75 to 29 entries.

- **Mutation Gateway, step 2.1 (ADR-0005).** Every MCP tool and agent tool call that maps to a capability runs
  through the gateway: tool switch and level, container target resolution, guardrail policy, approval, audit
  intent, deadline, audit outcome. A shared approval gate binds an approval to the operation and the arguments
  hash and uses it once — only after the audit intent is written, so a refused attempt does not spend it, and
  concurrent identical calls share one approval. The agent session pauses on the same (stricter stored) level and
  tool switch as the gateway. The bypass baseline shrinks from 104 to 75 entries.

- **Mutation Gateway, step 2.0 (ADR-0005).** `IMutationGateway` with a capability catalog of every mutating action, a decide → audit intent → execute → verify → audit outcome pipeline that refuses on any doubt (unknown capability, missing level, policy error, audit not writable), and an architecture test that lists every call to a mutating service outside the gateway in `docs/v1/mutation-bypass-baseline.txt` (104 today; the list may only shrink). No entry point uses the gateway yet, so permissions and behaviour are unchanged.

- **Scope guard in report mode.** Mutating service interfaces are wrapped at startup; a mutation that runs outside the gateway still runs, and the first one per capability is logged at Information (`ran outside the Mutation Gateway (report mode)`). Expect these lines until the surfaces are migrated.

- `server.json` for the official MCP Registry (`io.github.LupusMalusDeviant/whiskers`, OCI image on ghcr.io,
  Streamable HTTP at `/mcp` with a Bearer key). The image carries the matching
  `io.modelcontextprotocol.server.name` label, which the registry checks before it accepts `mcp-publisher publish`.
  The 0.15.0 image was rebuilt with the label from `release/0.15` (no application change), so 0.15.0 can
  already be published. A test keeps the version, image tag and label in step with the app version.

### Fixed
- Tests: no test empties the SQLite connection pools of other tests any more (`ClearAllPools`); each clears only
  its own database files. This removes the random "cannot start a transaction within a transaction" failures.

- A refused container update on the Dashboard no longer overwrites the saved rollback point; the snapshot is taken
  only once the update has been allowed.

- The installer no longer recommends `Auth__Disabled=true` for first sign-in. It prints the setup token and
  points to `VAULT_KEY`.

- The README renders completely on catalogues that drop raw HTML and relative paths (LobeHub): the header is
  Markdown, images and links are absolute, and the quick start shows the MCP client config for a self-hosted
  instance (`http://localhost:5100/mcp` with a Bearer key).

## [0.15.0] — 2026-10-26

Hardens the MCP surface and finishes the ServerWatch → Whiskers clean-up. MCP keys are now stored only as hashes.
Direct MCP tool calls now pass the same guardrails as the agent. The server ships prompts, resources and full
`serverInfo`.

### ⚠️ Upgrade notes
- **Copy new MCP keys when you create them.** Only the hash is stored now, so *Settings → MCP* can no longer
  reveal or copy an existing key. Existing keys are converted on the first start and keep working.
- **External MCP write calls may now need an approval.** Under the default guardrail preset, write tools
  called directly by an external client (including the Claude Code runtime) raise an approval on the
  *Approvals* page. Repeat the identical call within 10 minutes after approving it. Adjust the active preset
  if that is too strict for your setup.
- **Fresh installs no longer create a default MCP key.** Create one under *Settings → MCP*.
- **Leftover `api-keys.json` / `initial-mcp-key.txt` are deleted on start.** Their keys were no longer
  accepted for any tool call.

### Security
- **The ServerWatch-era flat MCP key store is gone.** `api-keys.json` held unscoped plaintext keys and was
  still accepted as a fallback at the `/mcp` gate. That fallback, the store and the one-time
  `initial-mcp-key.txt` are removed; leftover files are deleted on start. MCP access now works only with a
  key created under *Settings → MCP* with a Read / Write / Admin scope. The setup wizard no longer shows an
  initial key and points there instead.
- **MCP API keys are stored only as SHA-256 hashes.** New keys come from the CSPRNG (256 bits) and are shown
  once at creation; `mcp-permissions.json` keeps only the hash and the last four characters. Existing
  plaintext keys are converted on the first start and keep working. A fresh install no longer creates an
  unseen default key, a settings save can no longer alter key material, and rejected tokens are no longer
  logged with a prefix. **Behaviour change:** the key column in *Settings → MCP* can no longer reveal or
  copy an existing key — copy a new key right after creating it.
- **Guardrails now apply to direct MCP tool calls.** An external client calling a tool directly was bounded
  by its key's scope only; protected resources, forbidden arguments, the read-only switch and required
  approvals applied to the in-process agent alone. Every `tools/call` now passes the same guardrail engine.
  `Confirm` raises a Human-in-the-Loop approval; once approved, the identical call runs once within
  10 minutes. **Behaviour change:** under the default policy, write tools called by external clients now
  need an approval. If that is too strict, adjust the active guardrail preset (autonomous level,
  confirmation for writes) — the preset applies to the agent as well.

### Added
- **MCP `serverInfo`.** `initialize` now reports name, title, version, description, website and icon, so MCP
  clients and directories show what the server is instead of the bare assembly name.
- **`lhm.plugin.json`**: the LobeHub Marketplace manifest (owner declaration), generated with
  `lhm plugin init` from the running server — 86 tools, 2 resources, 4 prompts.
- **MCP prompts and resources.** Four user-invokable prompts (`triage_unhealthy_containers`,
  `security_review_server`, `plan_safe_update`, `investigate_host_load`) package common operations workflows,
  with a built-in stop before any write. Two read-only resources describe the MCP surface:
  `whiskers://tools/catalog` and `whiskers://me/permissions` (the caller's own scope and callable tools).

### Changed
- **Remaining ServerWatch names cleaned up.** README examples use the `whiskers` MCP server key and
  `/whiskers` paths; `WHISKERS_SELF_CONTAINERS` replaces `SERVERWATCH_SELF_CONTAINERS` (the old name is
  still read) and its default now covers both `whiskers` and `serverwatch`; the safe guardrail default
  protects `whiskers*` as well as `serverwatch*`. Names that existing installs depend on — the compose
  service/volume `serverwatch`, Prometheus `serverwatch_*` metrics, the Data Protection application name,
  the mTLS client CN, the Trivy cache volume and the host-shell label — stay unchanged on purpose.
- **Architecture rules moved to `docs/ARCHITECTURE.md`.** Application shape, security-critical areas,
  protected layers and engineering rules now live there. `CLAUDE.md` is no longer tracked.

### Fixed
- The Helm chart's fallback `version`/`appVersion` still read 0.12.1; releases stamp both from the tag, but
  directories that read the file showed a stale version.

## [0.14.1] — 2026-10-25

Security fix in Settings: the role table could turn whitelisted addresses into Admins on the next save.

### Fixed
- **Settings → User roles could hand out Admin by accident.** Whitelisted addresses without a role entry were
  merged into the role table as Admin. The server gave them the default role, but the next save of *any* role
  wrote those phantom Admin rows to `roles.json` — also for addresses deleted from the role list earlier. The
  table now shows only explicit entries; whitelisted addresses without one are listed below it with the role
  they really get, and a click on one starts assigning a role on purpose.
- **Saving roles reset the default role** to Viewer, because the page did not carry it through.
- **Creating or deleting an MCP API key discarded unsaved "active" toggles** of the other keys. Pending toggles
  now survive the reload.
- **Deleting an MCP API key had no admin check and no audit entry.** It now requires Admin, and both create and
  delete are written to the audit log (`mcp.key.create` / `mcp.key.delete`, never the key value).

## [0.14.0] — 2026-10-25

AutoPatch, measured update risk, DNS records over MCP — and a full pass over the UI for buttons that claimed
success without checking, filters that did not filter and pages that crashed on empty data.

### Fixed
- **Copying an API key (or any other value) silently did nothing over plain HTTP.** `navigator.clipboard` exists
  only in a secure context, the error was swallowed and the page reported "copied" anyway. Copying now falls back
  to a selection copy, reports the real outcome, and when it fails it reveals the value and says to copy it by hand.
- **The image update check never saw stale containers.** It read the local digest through the tag, so any pull
  without a recreate made an outdated container look current — and once the tag moved, Docker listed the container
  by its bare image id and the registry was asked about that id ("could not reach registry"). The check now
  compares the registry with the image the container actually runs.
- **Container detail:** for a Viewer the tab positions shifted, so the database tab spun forever while the hidden
  env tab's loader ran instead; buttons for Admin-only database and `.env` actions were shown to roles the handler
  then refused; saving `.env` after opening a secret for editing without typing wiped the secret; the CVE tab of a
  notification deep link always said "no scan".
- **Adding a server twice.** Save → failed connection probe → corrected host → Save again created a second entry
  with the same id. A retry now updates the first entry, and the store refuses duplicate ids.
- **Renaming the active guardrail preset silently activated a different one** (the first in the list).
- **The setup wizard accepted a password the account creation then rejected** — on the last step, in Identity's
  English wording. The admin step now checks the real password policy and names it; the "local Docker host
  connected" step actually probes the host.
- Pages that ignored their own controls or failures: "Scan now" on the CVE page never refreshed it; health-report
  filters applied only after Refresh; the notification log could not filter most event types; the self-status
  timeline never refreshed; App Store port conflicts were computed for the first server, not the chosen target;
  the container diff read env from the default server; duplicate ports or variables in the deploy form crashed
  the page; a failed agent start left the chat busy for good; agent-mode chats in the floating widget were never
  saved; terminals printed "session ended" twice; scheduled tasks could be created without a target or with an
  invalid cron expression.
- Kubernetes servers are no longer offered as targets for compose deploys, the compose editor, the App Store or
  volume backups.
- Cloud power and snapshot actions are now written to the audit log, like every other host action.
- English UI showed German in the notification bell and the DNS settings panel.

### Fixed
- **A server stayed paused until the next restart after a probe that produced no verdict.** When the half-open
  probe of the per-server circuit breaker ended in an application error or was cancelled, nothing cleared the
  probe flag, so every later caller was refused and the server looked throttled forever. The probe is now
  released on every exit path; a transport failure still re-opens the circuit as before.
- **Restarting Whiskers briefly cut the whole tailnet when the host already runs Tailscale.** With
  `VPN_PROVIDER` unset, the entrypoint started a second `tailscaled` inside the host-network container, which
  made the host daemon restart and dropped every mesh connection for one to two minutes. The entrypoint now
  skips its legacy bring-up when `tailscale0` already exists (`VPN_FORCE_LEGACY_TAILSCALE=1` restores it).
- **The second server to go down within the throttle window was never reported.** Server-level events carry no
  container id, so every channel throttled them on one shared key — exactly when a mesh outage takes several
  hosts down at once. The key now includes the server, and reachability transitions are never throttled (the
  tracker already announces each outage once and closes it with exactly one recovery).
- **A background loop that ran on time and failed every single cycle was reported as healthy.** The
  supervisory rule fell back to the last *attempt* when a loop had never succeeded, so a permanently failing
  loop kept resetting its own age to zero and never triggered — the exact shape of the 2026-08-26 incident,
  where the thing that ran on schedule and achieved nothing was the thing nobody noticed. A loop with no
  successful cycle is now judged by how many chances it has had (three), not by how recently it tried; a
  freshly started process still gets those three cycles, so a restart is not an incident.
- **`/metrics` produced comma decimals for German-speaking clients.** The endpoint sits behind request
  localization, so a scraper or browser sending `Accept-Language: de` received `0,120` instead of `0.120` —
  and Prometheus rejects an entire scrape at the first unparsable line, which would have taken the monitoring
  dark because of a request header. The endpoint is now pinned to the invariant culture for the duration of
  the request. This affected the pre-existing container metrics too, not only the new self-metrics.

### Added
- **Dashboard: the "Stopped" and "Unhealthy" tiles filter.** One click shows exactly the containers the tile
  counts, across all servers (combined with the group/tag filter); a chip or a second click clears it.
- **AutoPatch — nightly same-tag security patching (opt-in, off by default, dry run by default).** Rolls a
  container onto the newest image of its own tag through compose: never across a major version, skipped when the
  compose file has drifted from the running container or the image is built on the host, databases dumped and
  verified first, health-checked afterwards with automatic rollback. `get_auto_patch_status` and
  `preview_auto_patch` show what it would do.
- **Measured update risk.** `assess_update_risk` pulls the candidate image and compares it with the running one;
  `get_blast_radius` lists what a change takes down with it.
- **DNS records over MCP** (module `dns`, Infomaniak): `list_dns_records`, `set_dns_record` (idempotent),
  `delete_dns_record`, restricted to allowed zones.
- **borg-fleet** (`deploy/borg-fleet`): nightly append-only backups of every server to a storage box, one
  sub-account per server.
- **`renew_mtls_certificate` renews a proxy certificate without locking anyone out.** The host generates a new
  key, step-ca signs the CSR, and ghostunnel restarts with it. Before the swap the host arms a three-minute timer
  that restores the previous certificate unless Whiskers confirms through the new one. Admin-only, audited.
- **Warnings before a certificate expires.** Whiskers now watches its mTLS client certificate and CA bundle, the
  certificate of every mTLS proxy and certbot's certificates on all servers. It notifies 30, 14 and 7 days before
  expiry and once more on expiry (`certificate_expiring` / `certificate_expired`). An expired client certificate
  or CA used to take every SSH-free server down at once with no warning beforehand.
- **The generic outbound webhook can sign its requests.** With a secret set (Settings → generic webhook) every
  POST carries `X-Webhook-Timestamp` + `X-Webhook-Signature-V2` (HMAC-SHA256 over `<timestamp>.<body>`, so a
  captured request cannot be replayed) and the GitHub-style `X-Hub-Signature-256`. Receivers that require a
  signature — e.g. a Hermes agent webhook route that forwards alerts to Telegram — now accept Whiskers alerts.
  The payload also names the server (`server`, `serverId`).
- **"Did the automatic actions help?" is now a section on `/self-status`**, directly above the timeline that
  says what Whiskers did. A hit rate is shown only once at least five outcomes have been judged — "67%" from
  three attempts reads as a measurement and is a coin toss — and it is marked unreliable when more outcomes
  could not be measured than could, because such a number describes the checking rather than the actions.
  Unmeasurable outcomes never enter the rate and are never left out of the table either: a gap that is not
  shown is coverage the page would be silently claiming. Rows are ordered by how much attention they need,
  since somebody opening the page is asking "is any of this working?" and a good rate is not an answer to
  that.
- **Automatic actions are now checked against a promise made before they run.** Until now an action counted
  as successful when the call returned without an error — not when the problem went away, which is the
  incident's own confusion one level up. Every automatic action kind must declare how it can be checked
  (metric, direction, threshold, window); one without a criterion fails the test run and cannot be recorded as
  done. Afterwards the window is judged against series that already exist, and the verdict is one of three:
  it worked, it changed nothing, or **it could not be measured**. That third verdict is never folded into the
  first — missing data read as success is precisely the failure this whole package is about. The three
  existing self-throttles are wired in, and for those "changed nothing" means something sharp: the load was
  never ours, and monitoring was taken off a server for no benefit. `get_action_outcomes` reports the hit
  rates. **MCP clients must reconnect to see it.**

  Rollback and repeat-locking are deliberately **not** switched on: the plan asks for four weeks of
  observation first, and enabling them before anyone knows whether the criteria are any good would be the
  same habit this package exists to break.
- **A rolling baseline per host — and a guard for when the baseline learns the fault.** Each server now has
  an exponentially weighted mean and deviation for its own CPU, so a host behaving unlike itself is reported
  even when no fixed threshold is breached. Four sigma rather than the textbook three, because host CPU has a
  floor, a ceiling and a long tail of legitimate busy periods, and three sigma on that shape alerts most days.

  The important half is the guard. A baseline that keeps learning through an incident eventually decides that
  98% is normal and stops complaining — going quiet precisely when the problem has lasted longest. On the
  incident bench the deviation alert is raised eleven minutes after the step and clears again within the
  hour; anyone reading that all-clear alone would conclude the server had recovered. So the rule watches its
  own mean, and reports when the learned normal itself crosses the fixed threshold — 19.7 hours into the
  incident, two days earlier than the plan asked for. This is the third time this codebase has produced a
  measure that adjusts to what it is meant to detect, which is why four of the nine tests cover this one
  behaviour.
- **A Docker daemon that has become slow is now a signal of its own.** Whiskers makes hundreds of API calls a
  minute and never timed one of them: an overloaded daemon was visible only as things "feeling slow". The
  fleet listing — one call per server per health cycle — is now timed, and a recent median three times the
  host's own baseline raises an alert. Deliberately a ratio, not a millisecond threshold: a Pi over a tunnel
  and a local socket differ by an order of magnitude while both are healthy. Only successful calls are
  measured, because a call cut off at its timeout says "at least 8 seconds" and would peg the median the
  moment a host went fully silent — a case the circuit breaker already covers far better. The rule detects
  the *transition*; once a host's new normal is slow, the sustained state is the host-CPU rule's job.
- **Host alerts close themselves.** A server that goes back to normal now says so, with its own event type
  and informational severity — an all-clear delivered under the alarm's own name would be rendered red, next
  to a warning icon, and read as a fresh incident. The all-clear waits until the value is five points below
  the threshold and has stayed there five minutes: without that margin a host sitting at 87% would be
  declared recovered while still nearly saturated, and one grazing the threshold would flap until somebody
  muted the channel. An open finding is escalated only when it gets materially worse, never repeated. The
  count of unclosed findings and the age of the oldest are on `/metrics` — a number that only ever climbs
  means the closing path is broken, not that the problems are patient.
- **A server pinned at 98% CPU is now reported — the gap the 2026-08-26 incident fell through.** Alerts were
  evaluated per container plus one server-level rule for disk, and `dockerd` runs in no container: Whiskers
  recorded roughly 8,900 measurements of that host over six days, practically every one above 98%, and judged
  none of them. Sustained host CPU and memory are now evaluated, and so is the more specific signal — host
  load that **no container accounts for**, which names the class of cause instead of only the symptom.

  Comparing those two numbers means reconciling two conventions: host CPU is a percentage of the whole
  machine, container CPU is Docker's, where one busy core is 100 and a 2-core host reaches 200. Skipping that
  conversion fails silently in the dangerous direction — the unexplained gap comes out too small and the
  alert never fires — so it is pinned by a test on a 4-core host, a case the incident's own numbers do not
  expose. The rules are driven by sample time rather than the wall clock, which lets a reconstruction of that
  week replay through them in under a second and report 20 August, 14:14 instead of six days later. The new
  read-only `get_host_load` MCP tool answers the same question for the agent — per-container stats never
  could, since `dockerd` appears in none of them. **MCP clients must reconnect to see it.**
- **"What has been happening" on `/self-status`** — the last six hours of fleet-changing actions in one list:
  deploys, restarts, rule changes, agent actions, and Whiskers' own decisions (a pause, an open circuit, a
  suspended log scan) side by side, because those change the numbers with nobody having touched the fleet.
  Everything is computed in UTC and converted only at the point of display: a timeline with an offset in it
  is worse than none, since it will confidently suggest that the thing which happened *after* the spike
  caused it. Read-only actions are filtered out by excluding read verbs rather than by listing write ones, so
  a newly added kind of intervention shows up by default instead of being silently missing.
- **Containers whose logs are not being read are now listed, with the two reasons kept apart.** A container
  the scan gave up on after repeated timeouts and one deliberately excluded as Whiskers' own access path look
  identical from outside — no findings — and mean opposite things: a fault it will retry versus a decision.
  Both appear on `/self-status` under one honest heading, "Containers not being read", with distinct labels
  and faults listed first. Shown on separate pages, an operator would have to know both existed to be sure a
  container was covered at all.
- **A "Whiskers about itself" page** at `/self-status`: per loop and server, how long ago it last completed a
  cycle (as an age, never a timestamp — a clock time makes the reader do the subtraction, and doing it wrong
  is how a six-day-old failure goes unnoticed), the cycle duration, failures, and which servers a loop
  deliberately skips. Plus the per-server load budget, circuit state, and whether background checks are
  paused. The verdict comes from the same threshold the alert uses, read from it rather than copied: a page
  with its own quietly different rule would look authoritative while contradicting the alert that woke
  someone up.
- **Whiskers' own loop health survives a restart, and the agent can ask about it.** The self-metrics are
  written to a new `SelfMetricSamples` table once a minute (additive migration, both providers) and restored
  on boot. The history is the smaller half of the point: after a restart the in-memory view is empty, and an
  empty "last success" is indistinguishable from "never succeeded" — so the supervisory rule would have had to
  either alarm on every restart or stay quiet about fresh loops, and the second is exactly the window in which
  a bad deploy has most likely broken something. A live reading always beats the stored one, nothing older
  than a week is restored, and there is a test specifically for the direction that matters most: a restart
  must not make a genuinely stalled loop look alive. Sampling costs zero Docker calls, which is pinned by a
  test. The new read-only `get_whiskers_self_status` MCP tool reports the same thing in prose — including
  whether an absence of findings can be taken at face value. **MCP clients must reconnect to see it.**
- **Container logs growing without a rotation limit are now reported before the disk fills.** A daily survey
  reads each container's log driver configuration and the size of its log file, works out the growth per day
  from consecutive readings, and judges it **against the free space on that host** — 150 MB is a footnote next
  to 10 GB of headroom and an alert next to 200 MB, so an absolute threshold would be wrong nearly everywhere.
  A size that cannot be read is reported as *unknown*, never as zero and never estimated. The alert carries
  the compose snippet, the exact recreate command for that container, and the `daemon.json` default that
  stops it happening to the next one — and says plainly that applying it recreates the container. It never
  runs anything. Findings and exclusions are both in the `get_log_hygiene_report` MCP tool. Every message
  states that this removes the *trigger* of the 2026-08-26 incident and not its cause, so that closing this
  ticket does not read like closing the incident.
- **The log scan no longer reads the record of its own traffic.** The two containers that triggered the
  2026-08-26 incident were the tunnel and socket proxy Whiskers reaches Docker through: every request it makes
  is a line in their logs, and in two weeks those logs reached 822 MB. Containers on the access path are now
  detected — by matching the address and port Whiskers actually connects to against what the container
  publishes, never by name — and skipped by the log-alert scan only; health, metric and CVE monitoring still
  cover them. `SERVERWATCH_SELF_CONTAINERS` keeps working and takes precedence. Every exclusion is visible in
  the new `whiskers_log_scan_exclusions` metric and the read-only `get_log_hygiene_report` MCP tool, because
  a container excluded by mistake looks exactly like one with nothing to report. This removes the *trigger*
  of that incident, not its cause — the cause was a log fetch that was abandoned rather than cancelled, and
  both the alert text and the report say so. **MCP clients must reconnect to see the new tool.**
- **An emergency stop for Whiskers' own background checks.** During the 2026-08-26 incident the load on the
  host *was* Whiskers, and the only way to stop it was SSH on the affected server — past the tool causing the
  problem. Background checks for a single server can now be paused, from the UI-facing service or over MCP
  (`pause_server_checks`, `resume_server_checks`, `list_paused_servers`). The check sits at the one point every
  Docker call passes through, so a background loop cannot miss it; interactive access keeps working, because
  an operator pauses a server in order to look at it. Agents may pause for at most 120 minutes and must state
  a reason — an open-ended pause is a decision about how much blindness is acceptable, and that stays with a
  person. **MCP clients must reconnect to see the new tools.**

  The switch is built with its own failure mode in mind: a pause that outlives its reason is a server nobody
  is watching and nobody remembers deciding not to watch. So every pause is announced when set and when
  lifted, expires on its own, is not carried across restarts, and — while it stands — is re-reported every 24
  hours. The rule that reports the *absence* of checks cannot be paused by it; that separation is enforced by
  a test rather than by convention.
- **The agent can now see deploys, backups and its own alert history.** Four read-only MCP tools closed real
  blind spots: `list_git_deploy_apps` (repo, branch, outcome of the last deploy), `list_volume_backups` and
  `list_volumes` (answering "when was this volume last backed up?", with the age spelled out), and
  `list_recent_alerts`. The last one mattered most — the agent could read container states and raw logs but
  not the conclusions Whiskers had already drawn, so it re-derived them or missed them entirely. Their write
  counterparts are deliberately absent: starting a deploy belongs with a post-deploy health check and
  automatic rollback, restoring a volume overwrites live data, and an agent that can send notifications can
  flood the one channel that has to stay trustworthy. **MCP clients must reconnect to see the new tools** —
  connectors read the tool list once, at session start.
- **Every MCP tool now declares its permission level in its own source**, via `[McpToolLevel]` next to
  `[McpServerTool]`, and the served surface is pinned in a generated
  [tool catalog](docs/mcp-tool-catalog.md). Levels were carried over unchanged — this adds enforcement, not
  policy.
- **A server that stops answering now raises an alert.** A host dropping off the fleet was silent: the
  dashboard marked it unreachable, but nothing was sent — and every container alert and log-alert rule
  covering that host quietly stopped producing anything, which looks exactly like "all quiet". The health
  monitor now emits `server_unreachable` after two consecutive failed cycles
  (`HealthMonitor:ServerUnreachableCycles`) and `server_recovered` when the host is back. Both are
  rendered by every channel, link to `/servers`, and can drive an AI trigger. A server that has not
  answered once since startup gets a longer grace (`ServerUnreachableColdStartCycles`, default 10 cycles),
  because remote connections need a moment after a restart — without it a six-server fleet produced ten
  notifications per deploy.
- **The alert history is finally written.** `AlertHistory` has existed since the first migration but
  nothing ever wrote to it, so "what fired last week, and on which host?" was unanswerable. Every
  delivered notification is now recorded with its server, container, type and message; a
  `server_recovered` closes the outage rows it ends. Retention is the existing hourly prune.

### Fixed
- **Whiskers held a two-core host at 98% CPU for six days — its own doing.** The log monitor bounded each
  fetch with `Task.WhenAny(fetch, Task.Delay(timeout))`, which ends the *wait* and leaves the *request*
  running: dockerd kept reading the log file until the proxy cut the connection 600 seconds later, while a
  fresh fetch started every 60-second cycle. Ten concurrent full-log scans per container was the stable end
  state (1.15 million `read()` syscalls per second, `dockerd` at 184% of 200%). The cancellation token now
  reaches the Docker calls *and* the multiplexed read loop, so an expired deadline ends the request on the
  server too. Two more places bounded a per-server call the same abandoning way — the fleet-wide container
  listing and the system-info probe, both of which run for every server on every cycle — and were converted
  as well. Full analysis in [the incident report](docs/reviews/2026-08-26-logmonitor-dockerd-cpu-incident.md);
  the invariants that would have caught it now run in the test suite.
- **Nothing limited how much Whiskers asked of one server.** Five background loops each policed only
  themselves with their own timeout; the server sees the sum. There is now a per-server load budget at the
  point Docker calls pass through, with separate lanes for background work and for anything a person is
  waiting for — a background scan can no longer make the UI look frozen. Alongside it a circuit breaker:
  after a run of transport failures Whiskers stops calling that host and retries with a single probe after a
  cooldown. **Every such self-throttling is announced** (`server_throttled` / `server_throttling_ended`),
  because a pause nobody is told about is indistinguishable from "all quiet" — which is what let the incident
  above run for six days.
- **A forgotten permission entry made a tool look present and never work.** `McpPermissionCheck` resolves a
  tool that is missing from `DefaultToolLevels` to `admin` — correct as a fail-closed default, but it meant a
  forgotten entry produced a tool that is registered, appears in `tools/list`, and is then denied to the
  in-process agent on every single call, with no error and no log line. Levels are now declared on the method
  and the build fails on any drift, including a stale dictionary entry for a tool that no longer exists, a
  misspelled name in the permission gate, and a wire name that stops matching the method.
- **The tool-registration guard only checked a lower bound.** It asserted "more than 40 tools", which stays
  green while an entire module falls out of the catalog and its tools vanish from the served surface — the
  shape of the regression that left the shipped MCP server serving nothing from 0.12.0 to 0.13.0. Counts are
  now pinned per module, and a new test boots the real application and asks the running server for its tool
  list over the real endpoint. Reintroducing the original bug was verified to fail exactly that test, and
  none of the others.
- **Log alerts only ever watched one server.** The monitor's scan listed containers without a server id,
  which returns the **default** server's containers only — so every "on all containers" rule silently
  covered a single host while the other five in a six-server fleet were never read. The scan now uses the
  fleet-wide container list and fetches each container's logs from its own server. Pulled along with it:
  log watermarks and rule cooldowns are keyed by server + container id (container ids are unique per host
  only), hosts are scanned in parallel with a bounded per-fetch timeout, and the alert names the server and
  the line that matched.
- **A rule filtered to one container fired on all of them.** "No filter" was decided on `ContainerId`
  alone, but the UI dialog and the `create_log_alert` MCP tool only ever set `ContainerName` — so every
  container-specific rule degraded into an all-containers rule. Both fields are now considered. (Masked
  until now by the single-server scan; with a fleet-wide scan it would have meant an alert storm.)
- **An outage no longer swallows the log lines written during it.** An unreachable host returns an empty
  container list, which the monitors treated as "these containers are gone" and dropped their state for —
  re-baselining the host to "now" on recovery. Per-server state is now kept for hosts that did not answer,
  so the outage window is still scanned and a container that really stopped is still reported.
- **Log search searched one server too.** Without an explicit server it asked only the default host, while
  the page's container picker lists containers from every server — picking a remote container returned "no
  matches". Results now name the server they came from.
- **Muting a container silenced only a third of its alerts.** The per-container notification preferences
  were consulted by the health monitor alone; log alerts, CVE findings, image updates and metric alarms
  ignored them. The check now runs centrally for every producer.
- **Bounded log transfer.** With a `since` watermark the Docker call asked for *all* new lines, so one
  chatty container could pull its whole burst over a remote connection every minute, per cycle,
  fleet-wide. The tail limit (200 lines) now applies in both cases.
- The self-log guard that keeps Whiskers from alerting on its own log lines now applies only to the host
  Whiskers runs on. A container that merely shares its name on a remote host is a different process and
  stays monitored.

- **An AI trigger acting on a remote alert reported the container as missing.** Its tools take a server id,
  while everything it reads names the server ("Rabenhof (Hetzner)") — so it passed a name (or invented an
  id), got "Server not found" and concluded the container did not exist. The trigger's task message now
  states the server id explicitly, and the server lookup accepts a display name as well (ids win). Only
  surfaced once alerts started arriving from hosts other than the default one.

### Security
- **Onboarding no longer ships the proxy's private key across the network.** The key was generated on Badwolf
  and sent base64-encoded through the command channel. It is now generated on the new host; only the CSR and the
  signed certificate travel. Whiskers refuses any CSR that asks for more than the host's own name and IP, so a
  compromised host cannot obtain a certificate for another host.
- Matrix messages are built from an HTML-encoded copy of the event. The log-alert detail now carries the
  matched log line — third-party text that must not reach an HTML body unescaped.

## [0.13.1] — 2026-07-24

### Fixed
- **The MCP server exposed no tools.** The module-driven MCP registration (added in 0.12.0) passed the
  enabled modules' tool types as a `Type[]`, which binds to the generic `WithTools<T>(T target)` overload
  instead of `WithTools(IEnumerable<Type>)` and registers **zero** tools. As a result the server
  advertised only the `logging` capability and every client got `-32601 "Method 'tools/list' is not
  available."` — the entire tool surface was invisible over MCP. Fixed by passing the tool types as
  `IEnumerable<Type>`. Added `McpToolRegistrationTests` to guard the overload binding, since MCP tool
  serving previously had no test coverage.

### Added
- The running app version is shown in the sidebar, under the Whiskers wordmark.

## [0.13.0] — 2026-07-17

The governance story, end to end: every agent action now carries one correlation id from guardrail
through approval to history, a guided setup takes you from zero to a governed agent in four steps,
and the whole UI is available in English.

### Added
- **Full English UI + in-app handbook.** Every page, notification and the handbook are localized;
  English is the default and the app follows the browser language (switch anytime). The AI chat
  now answers in the user's language.
- **Guided "Secure AI Operations" setup.** A new admin-only **AI Operations** page walks you to a
  governed agent in four steps: choose how AI connects, create a read-only MCP key (shown once),
  pick one of three starter guardrail presets (*Observe only* / *Safe operations* / *Approval
  required*) with a full policy preview before you activate it, then try it and verify. It reuses the
  existing key and guardrail flows — no new secret handling.
- **End-to-end correlated governance chain.** Every agent tool call carries a stable correlation id
  from guardrail → approval → execution → history and notification, so one action reads as one thread.
  Approvals now show the *real* required permission level, a derived risk band, the target
  server/workload and the guardrail preset + rule that matched — on the approval card and in the
  call-detail dialog.
- **Server groups & tags.** Give each server an optional group and free-form tags; the dashboard
  gains a group/tag filter and the `list_servers` MCP tool takes an optional `tag` filter — quick
  ways to narrow a larger fleet.
- **Keyless-signed release images (cosign / Sigstore).** Release images are signed with the release
  workflow's GitHub OIDC identity and logged in Rekor — verify with `cosign verify` (see README →
  Security → Supply chain). Complements the existing Trivy gate, SLSA provenance and SBOM.

### Changed
- **The governance surfaces explain themselves.** Agent History, Audit Log, Approvals and Guardrails
  now lead with what they are for; the Approvals empty state spells out that Confirm genuinely pauses
  execution, and Guardrails opens with the allow/confirm/block framing.
- Documentation restructured around the governance positioning, with screenshots and a demo script.

### Fixed
- **CI never actually ran the test suite.** `Whiskers.Tests` was missing from the solution file, so
  `dotnet test --no-build` exited 0 without executing a single test — every green CI run before this
  release was build + boot-gate only. The project is now in the solution and the Test step fails on a
  silently-empty run.

### Upgrading
- Adds one additive, nullable migration (the correlation id on the MCP call log) for both SQLite and
  PostgreSQL. It runs automatically on first start — no action required.

## [0.12.1] — 2026-07-11

Security hardening plus the features that landed right after the 0.12.0 tag was cut.
The three security items below were previously listed under 0.12.0, but the published
0.12.0 artifacts were built before they landed — they ship starting with this release.

### Security
- **SSH host keys are now verified** (trust-on-first-use, pinned in
  `<data>/ssh-keys/known_hosts`). *Behavior change:* an intentionally rebuilt server needs
  its line removed from that file before reconnecting.
- **Fail-closed authorization**: every endpoint/page requires authentication unless it
  explicitly opts out (login, setup wizard, health probes, HMAC webhooks, token-gated
  metrics). The SignalR hub is no longer reachable anonymously.
- MCP bearer scheme is parsed case-insensitively (RFC 7235).

### Added
- **Git-based deployments** (`gitdeploy` module) — clone/pull a repository on a target
  server and bring it up with Docker Compose; deploy tokens are vault-only (surfaced to git
  via a 0600 `GIT_ASKPASS` file), and the new `git-deploy` webhook action enables
  push-to-deploy from CI.
- **Container registries (v1)** — manage private registries in Settings with vault-stored
  credentials; image pulls authenticate automatically by registry-host match.
- **Localized navigation & app chrome** (English/German) — all nav items and groups.
- Audit log now also covers scheduler and webhook management actions in the UI.
- CI on every push/PR (build, full test suite, DI boot gate); first bUnit component tests.

### Changed
- Dependency refresh: MudBlazor 9.7, YamlDotNet 18, Npgsql EF provider 10.0.3,
  MCP SDK 1.4.1, NCrontab 3.4; release pipeline moved to docker/* actions v4/v7.

## [0.12.0] — 2026-07-11

First **published** release: the container image (`ghcr.io/lupusmalusdeviant/whiskers`)
and the Helm chart (`oci://ghcr.io/lupusmalusdeviant/charts/whiskers`) are built and
scanned by the release pipeline from this version on.

### Added
- **First-run setup wizard** — create the admin account in the browser; no `.env` needed.
  `VAULT_KEY` and the initial MCP key are generated/shown once in the wizard.
- **Local login** (ASP.NET Identity, email + password) alongside Google/OIDC; unattended
  admin seed via `WHISKERS_ADMIN_EMAIL` + `WHISKERS_ADMIN_PASSWORD_FILE`.
- **Self-backup & restore** of Whiskers' own data dir — optionally VAULT_KEY-encrypted
  (AES-256-GCM), crash-safe deferred-swap restore, schedulable backup task.
- **Kubernetes**: Helm chart for running Whiskers *on* a cluster (single-replica by design,
  restricted PodSecurity, PVC), and managing k3s/Kubernetes clusters *from* Whiskers —
  pods on the dashboard with owner grouping, logs, honest scale/rollout actions; kubeconfig
  stored encrypted in the vault. Least-privilege RBAC manifest in `deploy/k8s/`.
- **Module framework** — every feature area (CVE, agent, terminal, notifications, webhooks,
  cloud control, image updates, …) is a module, toggled via `Features:<id>:Enabled`.
- **PostgreSQL** as a second database provider next to SQLite (`WHISKERS_DB_PROVIDER`).
- **Release pipeline** — multi-arch image (amd64/arm64) gated by a Trivy CRITICAL scan,
  SBOM + provenance, GitHub Release with pinned compose files and `install.sh`.
- **Guided onboarding** — dashboard first-server guide, upfront Tailscale question,
  step-tracked onboarding with actionable errors and safe resume, production-readiness
  checklist in Settings.
- **Light mode** with dark/light/system toggle; auto-update **rollback** (snapshot-based);
  i18n groundwork (English default, German complete for migrated pages).

### Changed
- **Webhook secrets are mandatory** (HMAC `X-Hub-Signature-256` over the raw body).
  *Breaking:* pre-existing webhooks without a secret are disabled at boot (not deleted) —
  regenerate their secret in the UI and update the CI caller, then re-enable.
- Fresh installs no longer seed the "local" Docker server when no Docker socket is present
  (Kubernetes deployments start with an empty fleet).

### Fixed
- The webhook UI test button now sends a genuinely signed request.
- `DockerService` split into focused internals (no behavior change); numerous smaller fixes.

## [0.11.0] and earlier

Pre-publication development (dashboard, MCP server + acting agent with guardrails and
approvals, CVE scanning with dedup/age tracking, zero-SSH mesh+mTLS onboarding, cloud
control for Hetzner/Hostinger, terminal, deployments, notifications, audit log, …).
History: `git log v0.12.0` and `docs/reviews/`.
