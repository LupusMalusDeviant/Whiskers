![Whiskers logo](https://raw.githubusercontent.com/LupusMalusDeviant/Whiskers/main/src/Whiskers/wwwroot/brand.png)

# Whiskers

Self-hosted MCP control plane for Docker hosts and Kubernetes clusters. Humans and AI agents call scoped tools instead
of holding SSH keys or the Docker socket. Every call is authenticated, scope-checked, guardrail-evaluated and logged. <!-- claim:RM-24 -->

[![Release](https://img.shields.io/github/v/release/LupusMalusDeviant/Whiskers?color=orange)](https://github.com/LupusMalusDeviant/Whiskers/releases)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://github.com/LupusMalusDeviant/Whiskers/blob/main/LICENSE)
[![MCP Badge](https://lobehub.com/badge/mcp/lupusmalusdeviant-whiskers)](https://lobehub.com/mcp/lupusmalusdeviant-whiskers)

**Status: stable (1.0).** Run it on a trusted network and read [SECURITY.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/SECURITY.md). <!-- claim-free:policy -->

**Scope of this README:** `main`, i.e. the first release after 0.15.0. Releases up to 0.15.0 differ (installer defaults
to `latest`, no generated vault key, no `VAULT_KEY_FILE`, no setup token); for those use the
[README at v0.15.0](https://github.com/LupusMalusDeviant/Whiskers/blob/v0.15.0/README.md). <!-- claim-free:history -->

## Facts

| | |
|---|---|
| Interfaces | Web UI (Blazor Server) and MCP over Streamable HTTP at `/mcp`. No hosted or cloud endpoint. <!-- claim:RM-01 --> |
| Sign-in | Local accounts, Google, generic OIDC. `AUTH_DISABLED=true` (container variable `Auth__Disabled`) makes every request an Admin (trusted LAN trials only). <!-- claim:RM-02 --> |
| MCP keys | Read, Write or Admin scope, or an explicit tool list; checked on every call. Stored as SHA-256 hashes, shown once. <!-- claim:RM-03 --> |
| Guardrails | Per tool Allow / Confirm / Block, evaluated in code at the tool boundary. `Confirm` runs only the exact call a human approved. <!-- claim:RM-04 --> |
| Records | Agent History (every tool call) and a hash-chained Audit Log. Checkpoints are signed, and the trail is tamper-evident, only when a vault key (`VAULT_KEY` or `VAULT_KEY_FILE`) is set. <!-- claim:RM-05 --> |
| Host access | Onboarding uses SSH once. Onboarded hosts then run over a WireGuard mesh (Tailscale/NetBird), the Docker API over mTLS behind a verb-whitelisting socket proxy, and host commands via a one-shot `nsenter` container, with no standing SSH key. A server added as a plain SSH connection and never onboarded keeps using SSH (its key in `ssh-keys/`) until it is migrated. <!-- claim:RM-14 --> |
| Targets | Docker hosts, k3s/Kubernetes clusters (kubeconfig), Hetzner and Hostinger cloud APIs. <!-- claim:RM-15 --> |
| Storage | `/app/data` (`WHISKERS_DATA_DIR`). SQLite by default, PostgreSQL optional. Secrets vault AES-256-GCM with `VAULT_KEY` / `VAULT_KEY_FILE`; SSH and mTLS client keys are sealed with a key derived from it. <!-- claim:RM-06,RM-07 --> |
| Images | `ghcr.io/lupusmalusdeviant/whiskers`, amd64 + arm64, Trivy-gated, SBOM + provenance, keyless cosign signature. <!-- claim:RM-08 --> |
| Stack | C# / .NET 10, ASP.NET Core, MudBlazor, EF Core, SignalR. License Apache 2.0. <!-- claim:RM-16 --> |

The full tool list with permission levels is generated from the code: [docs/mcp-tool-catalog.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/mcp-tool-catalog.md).

## Install

```bash
VER=X.Y.Z   # a release from https://github.com/LupusMalusDeviant/Whiskers/releases
curl -fsSLO "https://github.com/LupusMalusDeviant/Whiskers/releases/download/v${VER}/install.sh"
curl -fsSLO "https://github.com/LupusMalusDeviant/Whiskers/releases/download/v${VER}/checksums.txt"
sha256sum --check --ignore-missing checksums.txt && bash install.sh
```
<!-- claim-free:advice -->

The installer installs exactly that release, generates the vault key on a new install (`VAULT_KEY_FILE`), verifies the
image signature if cosign is installed, writes `./whiskers/` and waits until healthy. The UI is at
`http://127.0.0.1:5100`. Flags: `install.sh --help`. <!-- claim:RM-09 -->

Without the installer:

```bash
VER=X.Y.Z
docker run -d --name whiskers -p 127.0.0.1:5100:8080 \
  -v whiskers-data:/app/data -v /var/run/docker.sock:/var/run/docker.sock \
  "ghcr.io/lupusmalusdeviant/whiskers:${VER}"
```
<!-- claim-free:advice -->

This has no vault key; set `VAULT_KEY_FILE` before you store secrets. Compose, Helm, reverse proxy, Tailscale and the
hardened profile: [docs/installation.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/installation.md). <!-- claim-free:advice -->

## First sign-in

```bash
docker exec whiskers cat /app/data/setup-token.txt      # installer or docker run
docker exec serverwatch cat /app/data/setup-token.txt   # release docker-compose.yml (container keeps its pre-rebrand name)
```
<!-- claim-free:advice -->

Enter the token in the setup wizard; it creates the first Admin. Unattended alternative: `WHISKERS_ADMIN_EMAIL` +
`WHISKERS_ADMIN_PASSWORD_FILE`. Local accounts have no MFA in 1.0; for production, sign in through your OIDC provider
with MFA. <!-- claim:RM-10 -->

## Connect an MCP client

Create a key under *Settings → MCP*, then:

```bash
export WHISKERS_MCP_API_KEY=…
claude mcp add --transport http --scope project whiskers http://localhost:5100/mcp --header 'Authorization: Bearer ${WHISKERS_MCP_API_KEY}'
```
<!-- claim-free:advice -->

Any other client:

```json
{
  "mcpServers": {
    "whiskers": {
      "type": "http",
      "url": "http://localhost:5100/mcp",
      "headers": { "Authorization": "Bearer <WHISKERS_MCP_API_KEY>" }
    }
  }
}
```
<!-- claim-free:advice -->

Crush and more: [docs/installation.md → MCP clients](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/installation.md#mcp-clients). Scopes, prompts and
resources: [docs/mcp.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/mcp.md).

## Verifying the image

```bash
VER=X.Y.Z
cosign verify "ghcr.io/lupusmalusdeviant/whiskers:${VER}" \
  --certificate-identity-regexp '^https://github.com/LupusMalusDeviant/Whiskers/.github/workflows/release.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```
<!-- claim-free:advice -->

Images before v0.12.1 are not cosign-signed. Supply-chain details:
[docs/security-architecture.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/security-architecture.md#supply-chain). <!-- claim-free:history -->

## Configuration

Everything is optional. Where a setting goes depends on the install path: <!-- claim-free:advice -->

- **Release `docker-compose.yml`:** put the `.env` names below in a `.env` beside it; the compose file maps them to the
  container variables. <!-- claim:RM-17 -->
- **Installer:** on a new install its compose file sets `TZ`, `VAULT_KEY_FILE` and, with `--data`,
  `WHISKERS_DATA_HOST_DIR`. Add container variables as `environment:` entries in `./whiskers/docker-compose.yml`. <!-- claim:RM-18 -->
- **`docker run`:** nothing is set; pass container variables with `-e`. <!-- claim-free:advice -->

| `.env` name (release compose) | Container variable | Effect |
|---|---|---|
| `VAULT_KEY_FILE` / `VAULT_KEY` | same | Encrypts stored secrets. Losing it makes them unreadable. The installer sets `VAULT_KEY_FILE` on a new install. <!-- claim:RM-11 --> |
| — (release or repository compose file: `deploy/docker-compose.postgres.yml` overlay from the repository; installer: `environment:` entries) | `WHISKERS_DB_PROVIDER`, `WHISKERS_DB_CONNECTION` (or `_FILE`) | `sqlite` (default) or `postgres`. The overlay needs `WHISKERS_DB_PASSWORD` in `.env`. <!-- claim:RM-19 --> |
| `OIDC_ENABLED`, `OIDC_AUTHORITY`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET` | `Oidc__Enabled`, `Oidc__Authority`, `Oidc__ClientId`, `Oidc__ClientSecret` | Sign-in through your identity provider. <!-- claim:RM-20 --> |
| `HOST_BIND`, `HOST_PORT` | — (port mapping; installer: `--bind`, `--port`) | Published address; default `127.0.0.1:5100`. <!-- claim:RM-21 --> |
| `PATH_BASE` | `PathBase` | Subpath behind a reverse proxy. <!-- claim:RM-22 --> |
| — (not mapped; add an `environment:` entry) | `ForwardedHeaders__TrustedNetworks__0` | Proxy whose `X-Forwarded-*` headers are believed; loopback only by default. <!-- claim:RM-12 --> |

All keys: [docs/configuration.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/configuration.md) and [.env.example](https://github.com/LupusMalusDeviant/Whiskers/blob/main/.env.example). <!-- claim-free:pointer -->

## Accepted limits (1.0)

Deliberate limits of Whiskers 1.0
([product contract §5](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/v1/PRODUCT_CONTRACT.md#5-scope-and-stated-limits)): <!-- claim-free:limit -->

- **Root-equivalent administration.** An Admin, an Admin-level key or a compromised instance is root on every managed host. Whiskers governs who reaches that power and records it; it does not make it smaller. A shell on the Whiskers host itself is outside the contract. <!-- claim-free:limit -->
- **Single instance.** One process and one data directory; no replica, no failover. Recovery comes from self-backups kept off the host. <!-- claim-free:limit -->
- **Global roles.** Viewer, Operator and Admin (and key levels) apply to every managed server; there is no per-server or tenant scoping. <!-- claim-free:limit -->
- **No terminal transcript.** Opening a terminal is authorized, kill-switch-checked and audited; what is typed inside is neither policy-checked nor recorded. <!-- claim-free:limit -->
- **Out-of-band changes are invisible.** Changes made directly on a host, in a cloud console or by another tool are not governed or attributed. <!-- claim-free:limit -->
- **No second factor for local accounts.** Production sign-in goes through your identity provider with MFA. <!-- claim-free:limit -->

## Compose exposure checks: remaining risks

Compose deployments are decided on their exposures (published ports, host network, privileges, route labels; contract
0006, INV-62 — details in [docs/features.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/features.md#compose-deployments-and-exposure-changes)). Not caught,
or caught only after `up`: <!-- claim-free:limit -->

- **Proxies and tunnels behind an unrecognisable entrypoint**: Traefik, cloudflared and Tailscale are recognised by
  image name, `argv[0]`, a word of a `sh -c` string or an environment key (contract 0013); a binary renamed or started
  some other way is not. <!-- claim-free:limit -->
- **Tailscale serve state** is read from configuration (`TS_SERVE_CONFIG`), never from tailscaled at run time; a
  sidecar with persistent state and no serve configuration counts as public and is decided. <!-- claim-free:limit -->
- **A Traefik on the host** outside the project is recognised by its image name (`traefik`) only (contract 0013,
  Amendment 2). <!-- claim-free:limit -->
- **The tag race**: putting a tag back after a refused run checks that it still points where this run left it; a tag
  moved by another run in between is left alone (and so not restored). <!-- claim-free:limit -->
- **`container.update` and `autopatch.run`** recreate containers from new images without this check — except a
  container a compose run pinned (label `whiskers.image.ref`), which they refuse: it is updated by redeploying its
  project (contract 0013). <!-- claim-free:limit -->
- **Pinned containers show an image id**: `up` starts each image by its id (contract 0013, D10 as amended), so the
  Image column of a container a compose run started shows `sha256:…`; the name the project uses is in the label
  `whiskers.image.ref`. <!-- claim-free:limit -->
- **Late daemon work after a timeout**: a pull, build or `up` the daemon finishes after Whiskers stopped waiting is not
  re-checked. <!-- claim-free:limit -->
- **The 5-minute budget** of a compose run (per command) can be too short for large builds; Git deploy keeps its own
  300 s / 600 s / 300 s. <!-- claim:RM-13 -->
- **Legacy containers of the old Deploy page** (plain containers without compose labels) are left alone; a deployment
  that would use one of their names is refused until the container is removed or another name is chosen (contract 0013,
  D15). <!-- claim:RM-25 -->

**Upgrade note (contract 0006).** The new policy defaults change every policy's version hash, so open approvals and
the policy snapshots of pinned runs expire at the upgrade. `docker compose pull --ignore-buildable` needs Docker
Compose v2.15 or newer on every managed host. A label key, a port, a network or a privilege field from an `.env`
variable that is not set now refuses the plan. Redeploys of projects with `build:` services rebuild with `--pull`, and
Git deploys now pull images as well. <!-- claim-free:history -->

**Upgrade note (contract 0013).** The default exposure label prefix `traefik.` replaces `traefik.http.routers.` and
`traefik.tcp.routers.` (a literal `traefik.enable=false` is no exposure), which changes every policy's version hash:
open approvals expire at the upgrade. A Traefik with `exposedByDefault`, cloudflared and Tailscale serve/Funnel in a
deployment — and a Traefik already running on the host — are decided as exposures; a proxy or tunnel whose configuration
cannot be read (a variable that is not set, a config file on a named volume, a file larger than 1 MiB) refuses the
plan. Containers a compose run starts are pinned to their image id; `container.update` and AutoPatch refuse a pinned
container (redeploy its project to update it). The Deploy page (form and compose tab) and MCP `deploy_app` now create
real compose projects in `/opt/deployments/<project>` instead of single containers: the form and `deploy_app` keep
`network_mode: bridge`, a redeploy with the same name updates the project, `deploy_app` refuses a malformed port or
environment entry instead of dropping it and answers with the project, its containers and the pinned image id. A plain
container the old page created blocks a deployment that would reuse its name. <!-- claim-free:history -->

## Operating it

[Production checklist](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/production-checklist.md) ·
[Upgrade and rollback](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/upgrade-and-rollback.md) ·
[Backup and recovery](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/backup-and-recovery.md) ·
[Troubleshooting](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/troubleshooting.md) ·
[all operations guides](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/README.md)

## Development

Requires the .NET SDK version in `global.json`. <!-- claim-free:advice -->

```bash
dotnet build Whiskers.slnx -c Release
dotnet test Whiskers.slnx -c Release
dotnet test src/Whiskers.Tests/Whiskers.Tests.csproj --filter Category=RequiresDocker   # PostgreSQL suites: need Docker, skipped locally without it, mandatory on CI
dotnet run --project src/Whiskers/Whiskers.csproj                                       # http://localhost:5041 (launchSettings.json)
```
<!-- claim-free:advice -->

After changing a package reference: `dotnet restore Whiskers.slnx --force-evaluate` and commit the lock files
([deploy/README.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/deploy/README.md#pinned-build-inputs-r1-52)). Pull requests: [CONTRIBUTING.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/CONTRIBUTING.md).
CI checks the build, the tests, the image, the PostgreSQL suites, dependencies, secrets and static analysis: [docs/operations/ci-and-release-checks.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/ci-and-release-checks.md). <!-- claim-free:advice -->

## Documentation

| Topic | File |
|---|---|
| Governance model, hero workflow, policy layer | [docs/governance.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/governance.md) |
| Features | [docs/features.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/features.md) |
| Installation (all paths) | [docs/installation.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/installation.md) <!-- claim-free:pointer --> |
| Configuration | [docs/configuration.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/configuration.md) |
| MCP server | [docs/mcp.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/mcp.md) |
| In-process AI agent | [docs/agent.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/agent.md) |
| Security architecture and supply chain | [docs/security-architecture.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/security-architecture.md) |
| Architecture | [docs/ARCHITECTURE.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/ARCHITECTURE.md) |
| Screenshots | [docs/screenshots.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/screenshots.md) |
| Roadmap | [docs/roadmap.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/roadmap.md) |
| Project structure | [docs/project-structure.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/project-structure.md) |
| Everything else | [docs/README.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/README.md) <!-- claim-free:pointer --> |

Security issues: report privately, see [SECURITY.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/SECURITY.md).

## License

Apache License 2.0, see [LICENSE](https://github.com/LupusMalusDeviant/Whiskers/blob/main/LICENSE). Copyright © 2026 Whiskers Contributors. <!-- claim:RM-23 -->
