![Whiskers logo](https://raw.githubusercontent.com/LupusMalusDeviant/Whiskers/main/src/Whiskers/wwwroot/brand.png)

# Whiskers

Self-hosted MCP control plane for Docker hosts and Kubernetes clusters. Humans and AI agents call scoped tools instead
of holding SSH keys or the Docker socket. Every call is authenticated, scope-checked, guardrail-evaluated and logged.

[![Release](https://img.shields.io/github/v/release/LupusMalusDeviant/Whiskers?color=orange)](https://github.com/LupusMalusDeviant/Whiskers/releases)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://github.com/LupusMalusDeviant/Whiskers/blob/main/LICENSE)
[![MCP Badge](https://lobehub.com/badge/mcp/lupusmalusdeviant-whiskers)](https://lobehub.com/mcp/lupusmalusdeviant-whiskers)

**Status: beta.** Not API-stable before 1.0. Run it on a trusted network and read [SECURITY.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/SECURITY.md).

## Facts

| | |
|---|---|
| Interfaces | Web UI (Blazor Server) and MCP over Streamable HTTP at `/mcp`. No hosted or cloud endpoint. |
| Sign-in | Local accounts, Google, generic OIDC. `AUTH_DISABLED=true` makes every request an Admin (trusted LAN trials only). |
| MCP keys | Read, Write or Admin scope, or an explicit tool list; checked on every call. Stored as SHA-256 hashes, shown once. |
| Guardrails | Per tool Allow / Confirm / Block, evaluated in code at the tool boundary. `Confirm` runs only the exact call a human approved. |
| Records | Agent History (every tool call) and a hash-chained Audit Log. Checkpoints are signed, and the trail is tamper-evident, only when `VAULT_KEY` is set. |
| Host access | Onboarding uses SSH once. Onboarded hosts then run over a WireGuard mesh (Tailscale/NetBird), the Docker API over mTLS behind a verb-whitelisting socket proxy, and host commands via a one-shot `nsenter` container, with no standing SSH key. A server added as a plain SSH connection and never onboarded keeps using SSH (its key in `ssh-keys/`) until it is migrated. |
| Targets | Docker hosts, k3s/Kubernetes clusters (kubeconfig), Hetzner and Hostinger cloud APIs. |
| Storage | `/app/data` (`WHISKERS_DATA_DIR`). SQLite by default, PostgreSQL optional. Secrets vault AES-256-GCM with `VAULT_KEY`; SSH and mTLS client keys are protected by file permissions only. |
| Images | `ghcr.io/lupusmalusdeviant/whiskers`, amd64 + arm64, Trivy-gated, SBOM + provenance, keyless cosign signature. |
| Stack | C# / .NET 10, ASP.NET Core, MudBlazor, EF Core, SignalR. License Apache 2.0. |

The full tool list with permission levels is generated from the code: [docs/mcp-tool-catalog.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/mcp-tool-catalog.md).

## Install

```bash
VER=X.Y.Z   # a release from https://github.com/LupusMalusDeviant/Whiskers/releases
curl -fsSLO "https://github.com/LupusMalusDeviant/Whiskers/releases/download/v${VER}/install.sh"
curl -fsSLO "https://github.com/LupusMalusDeviant/Whiskers/releases/download/v${VER}/checksums.txt"
sha256sum --check --ignore-missing checksums.txt && bash install.sh
```

The installer generates the vault key, verifies the image signature if cosign is installed, writes `./whiskers/` and
waits until healthy. Flags: `install.sh --help`. The UI is at `http://127.0.0.1:5100`.

Installers attached to releases up to 0.15.0 default to `latest`. Run those with
`bash install.sh --image "ghcr.io/lupusmalusdeviant/whiskers:${VER}"`. From the next release on, the installer of a
release installs exactly that release.

Without the installer:

```bash
VER=X.Y.Z
docker run -d --name whiskers -p 127.0.0.1:5100:8080 \
  -v whiskers-data:/app/data -v /var/run/docker.sock:/var/run/docker.sock \
  "ghcr.io/lupusmalusdeviant/whiskers:${VER}"
```

This has no vault key; set `VAULT_KEY_FILE` before you store secrets. Compose, Helm, reverse proxy, Tailscale and the
hardened profile: [docs/installation.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/installation.md).

## First sign-in

```bash
docker exec whiskers cat /app/data/setup-token.txt
```

Enter the token in the setup wizard; it creates the first Admin. Unattended alternative: `WHISKERS_ADMIN_EMAIL` +
`WHISKERS_ADMIN_PASSWORD_FILE`. Local accounts have no MFA in 1.0; for production, sign in through your OIDC provider
with MFA.

## Connect an MCP client

Create a key under *Settings → MCP*, then:

```bash
export WHISKERS_MCP_API_KEY=…
claude mcp add --transport http --scope project whiskers http://localhost:5100/mcp --header 'Authorization: Bearer ${WHISKERS_MCP_API_KEY}'
```

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

Crush and more: [docs/installation.md → MCP clients](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/installation.md#mcp-clients). Scopes, prompts and
resources: [docs/mcp.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/mcp.md).

## Verifying the image

```bash
VER=X.Y.Z
cosign verify "ghcr.io/lupusmalusdeviant/whiskers:${VER}" \
  --certificate-identity-regexp '^https://github.com/LupusMalusDeviant/Whiskers/.github/workflows/release.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

Images before v0.12.1 are not cosign-signed. Supply-chain details:
[docs/security-architecture.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/security-architecture.md#supply-chain).

## Configuration

Everything is optional. Where a setting goes depends on the install path:

- **Release `docker-compose.yml`:** put the `.env` names below in a `.env` beside it; the compose file maps them to the
  container variables.
- **Installer or `docker run`:** the generated compose file passes only `TZ` and `VAULT_KEY_FILE`. Add the container
  variables as `environment:` entries in `./whiskers/docker-compose.yml`, or as `-e` flags.

| `.env` name (release compose) | Container variable | Effect |
|---|---|---|
| `VAULT_KEY_FILE` / `VAULT_KEY` | same | Encrypts stored secrets. Losing it makes them unreadable. The installer sets `VAULT_KEY_FILE`. |
| — (use the `deploy/docker-compose.postgres.yml` overlay) | `WHISKERS_DB_PROVIDER`, `WHISKERS_DB_CONNECTION_FILE` | `sqlite` (default) or `postgres`. |
| `OIDC_ENABLED`, `OIDC_AUTHORITY`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET` | `Oidc__Enabled`, `Oidc__Authority`, `Oidc__ClientId`, `Oidc__ClientSecret` | Sign-in through your identity provider. |
| `HOST_BIND`, `HOST_PORT` | — (port mapping; installer: `--bind`, `--port`) | Published address; default `127.0.0.1:5100`. |
| `PATH_BASE` | `PathBase` | Subpath behind a reverse proxy. |
| — (not mapped; add an `environment:` entry) | `ForwardedHeaders__TrustedNetworks__0` | Proxy whose `X-Forwarded-*` headers are believed; loopback only by default. |

All keys: [docs/configuration.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/configuration.md) and [.env.example](https://github.com/LupusMalusDeviant/Whiskers/blob/main/.env.example).

## Accepted limits (1.0)

Deliberate limits of Whiskers 1.0
([product contract §5](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/v1/PRODUCT_CONTRACT.md#5-scope-and-stated-limits)):

- **Root-equivalent administration.** An Admin, an Admin-level key or a compromised instance is root on every managed host. Whiskers governs who reaches that power and records it; it does not make it smaller. A shell on the Whiskers host itself is outside the contract.
- **Single instance.** One process and one data directory; no replica, no failover. Recovery comes from self-backups kept off the host.
- **Global roles.** Viewer, Operator and Admin (and key levels) apply to every managed server; there is no per-server or tenant scoping.
- **No terminal transcript.** Opening a terminal is authorized, kill-switch-checked and audited; what is typed inside is neither policy-checked nor recorded.
- **Out-of-band changes are invisible.** Changes made directly on a host, in a cloud console or by another tool are not governed or attributed.
- **No second factor for local accounts.** Production sign-in goes through your identity provider with MFA.

## Compose exposure checks: remaining risks

Compose deployments are decided on their exposures (published ports, host network, privileges, route labels; contract
0006, INV-62 — details in [docs/features.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/features.md#compose-deployments-and-exposure-changes)). Not caught,
or caught only after `up`:

- **Traefik `exposedByDefault`**: a Traefik that routes every container without a label publishes services this check
  never sees.
- **Tailscale sidecar `serve`/Funnel** configured inside a sidecar's own state or `serve.json`, and **cloudflared**
  tunnels, publish without a label on the deployed service.
- **The re-tag window**: an image re-tagged by someone else between the image inspection and `up --pull never` is caught
  only by the re-check after `up`.
- **The tag race**: putting a tag back after a refused run checks that it still points where this run left it; a tag
  moved by another run in between is left alone (and so not restored).
- **`container.update` and `autopatch.run`** recreate containers from new images without this check.
- **Late daemon work after a timeout**: a pull, build or `up` the daemon finishes after Whiskers stopped waiting is not
  re-checked.
- **The 5-minute budget** of a compose run (per command) can be too short for large builds; Git deploy keeps its own
  300 s / 600 s / 300 s.
- **Out of scope**: the Deploy page's compose mode (`ComposeFileParser`) and `DeploymentRequest` / `deploy_app` are not
  decided on their exposures.

**Upgrade note (contract 0006).** The new policy defaults change every policy's version hash, so open approvals and
the policy snapshots of pinned runs expire at the upgrade. `docker compose pull --ignore-buildable` needs Docker
Compose v2.15 or newer on every managed host. A label key, a port, a network or a privilege field from an `.env`
variable that is not set now refuses the plan. Redeploys of projects with `build:` services rebuild with `--pull`, and
Git deploys now pull images as well.

## Operating it

[Production checklist](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/production-checklist.md) ·
[Upgrade and rollback](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/upgrade-and-rollback.md) ·
[Backup and recovery](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/backup-and-recovery.md) ·
[Troubleshooting](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/troubleshooting.md) ·
[all operations guides](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/README.md)

## Development

Requires the .NET SDK version in `global.json`.

```bash
dotnet build Whiskers.slnx -c Release
dotnet test Whiskers.slnx -c Release
dotnet test src/Whiskers.Tests/Whiskers.Tests.csproj --filter Category=RequiresDocker   # PostgreSQL suites: need Docker, skipped locally without it, mandatory on CI
dotnet run --project src/Whiskers/Whiskers.csproj                                       # http://localhost:5041 (launchSettings.json)
```

After changing a package reference: `dotnet restore Whiskers.slnx --force-evaluate` and commit the lock files
([deploy/README.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/deploy/README.md#pinned-build-inputs-r1-52)). Pull requests: [CONTRIBUTING.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/CONTRIBUTING.md).
CI checks the build, the tests, the image, the PostgreSQL suites, dependencies, secrets and static analysis: [docs/operations/ci-and-release-checks.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/ci-and-release-checks.md).

## Documentation

| Topic | File |
|---|---|
| Governance model, hero workflow, policy layer | [docs/governance.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/governance.md) |
| Features | [docs/features.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/features.md) |
| Installation (all paths) | [docs/installation.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/installation.md) |
| Configuration | [docs/configuration.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/configuration.md) |
| MCP server | [docs/mcp.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/mcp.md) |
| In-process AI agent | [docs/agent.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/agent.md) |
| Security architecture and supply chain | [docs/security-architecture.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/security-architecture.md) |
| Architecture | [docs/ARCHITECTURE.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/ARCHITECTURE.md) |
| Screenshots | [docs/screenshots.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/screenshots.md) |
| Roadmap | [docs/roadmap.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/roadmap.md) |
| Project structure | [docs/project-structure.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/project-structure.md) |
| Everything else | [docs/README.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/README.md) |

Security issues: report privately, see [SECURITY.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/SECURITY.md).

## License

Apache License 2.0, see [LICENSE](https://github.com/LupusMalusDeviant/Whiskers/blob/main/LICENSE). Copyright © 2026 Whiskers Contributors.
