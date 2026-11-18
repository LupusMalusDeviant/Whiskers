![Whiskers logo](https://raw.githubusercontent.com/LupusMalusDeviant/Whiskers/main/src/Whiskers/wwwroot/brand.png)

# Whiskers

Self-hosted MCP control plane for Docker hosts and Kubernetes clusters. Humans and AI agents call scoped tools instead
of holding SSH keys or the Docker socket. Every call is authenticated, scope-checked, guardrail-evaluated and logged. <!-- claim:RM-24 -->

[![Release](https://img.shields.io/github/v/release/LupusMalusDeviant/Whiskers?color=orange)](https://github.com/LupusMalusDeviant/Whiskers/releases)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://github.com/LupusMalusDeviant/Whiskers/blob/main/LICENSE)
[![MCP Badge](https://lobehub.com/badge/mcp/lupusmalusdeviant-whiskers)](https://lobehub.com/mcp/lupusmalusdeviant-whiskers)

**Status: 1.0.0 in preparation.** No 1.0 release is published yet (an earlier 1.0.0 and 1.0.1 were withdrawn); until
it is, `main` is the release candidate. Run it on a trusted network and read [SECURITY.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/SECURITY.md). <!-- claim-free:policy -->

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

What it does: [features](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/guide/features.md) ·
[governance and approvals](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/guide/governance.md) ·
[MCP tool list](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/guide/mcp-tool-catalog.md) (generated from the code).

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

`docker run`, Compose, Helm, PostgreSQL, a reverse proxy, Tailscale, the hardened profile and verifying the image
signature: [installation guide](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/installation.md). <!-- claim-free:pointer -->

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

Create a key under *Settings → MCP*, then (Claude Code; other clients in the
[installation guide](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/installation.md#mcp-clients)):

```bash
export WHISKERS_MCP_API_KEY=…
claude mcp add --transport http --scope project whiskers http://localhost:5100/mcp --header 'Authorization: Bearer ${WHISKERS_MCP_API_KEY}'
```
<!-- claim-free:advice -->

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

All keys: [configuration](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/configuration.md) and [.env.example](https://github.com/LupusMalusDeviant/Whiskers/blob/main/.env.example). <!-- claim-free:pointer -->

## Before production

Whiskers 1.0 has deliberate limits: an Admin is root on every managed host, it runs as a single instance, roles are
global, terminal sessions are not transcribed, changes made outside Whiskers are not attributed, and local accounts
have no second factor. Read them, and the remaining risks of the compose exposure checks, in the
[production checklist](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/production-checklist.md#accepted-limits-10). <!-- claim-free:limit -->

[Upgrade and rollback](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/upgrade-and-rollback.md) ·
[Backup and recovery](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/backup-and-recovery.md) ·
[Troubleshooting](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/troubleshooting.md) ·
[all operations guides](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/README.md)

## Development

Requires the .NET SDK version in `global.json`. <!-- claim-free:advice -->

```bash
dotnet build Whiskers.slnx -c Release
dotnet test Whiskers.slnx -c Release
dotnet run --project src/Whiskers/Whiskers.csproj   # http://localhost:5041 (launchSettings.json)
```
<!-- claim-free:advice -->

Pull requests: [CONTRIBUTING.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/CONTRIBUTING.md). All
documentation, by reader: [docs/README.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/README.md).
Security issues: report privately, see [SECURITY.md](https://github.com/LupusMalusDeviant/Whiskers/blob/main/SECURITY.md). <!-- claim-free:pointer -->

## License

Apache License 2.0, see [LICENSE](https://github.com/LupusMalusDeviant/Whiskers/blob/main/LICENSE). Copyright © 2026 Whiskers Contributors. <!-- claim:RM-23 -->
