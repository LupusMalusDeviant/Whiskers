<p align="center">
  <img src="src/ServerWatch/wwwroot/brand.png" alt="ServerWatch logo" width="150" height="150" />
</p>

<h1 align="center">ServerWatch</h1>

<p align="center"><strong>A Docker &amp; server management dashboard with a built-in Model Context Protocol (MCP) server for AI-driven infrastructure operations.</strong></p>

ServerWatch gives you a live, web-based control plane for a fleet of Docker hosts, containers, images, networks, databases, firewalls, Nginx, systemd, SSL, metrics and logs, and exposes the same capabilities to AI agents (such as Claude Code) through an authenticated MCP endpoint with per-key Read/Write/Admin permissions.

Its headline design goal is **SSH-key-free operation**: hosts are managed over a private WireGuard mesh with mutual-TLS Docker access, so there is no standing private key for an attacker to steal. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full design.

[![Version](https://img.shields.io/badge/version-0.10.0-orange.svg)](#)
[![Status](https://img.shields.io/badge/status-beta-yellow.svg)](#)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Blazor](https://img.shields.io/badge/Blazor-Server-512BD4.svg)](https://learn.microsoft.com/aspnet/core/blazor/)

> ⚠️ **Beta (`0.10.0`).** ServerWatch is under active development and not yet API-stable.
> Run it on a trusted network, review the [security policy](SECURITY.md), and expect breaking
> changes before `1.0`.

---

## Table of contents

- [Features](#features)
- [Tech stack](#tech-stack)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [MCP server](#mcp-server)
- [AI agent](#ai-agent-optional)
- [Architecture](#architecture)
- [Project structure](#project-structure)
- [Development](#development)
- [Security](#security)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)

---

## Features

### Container management
- Live dashboard with CPU / memory / health status across all hosts
- Start, stop, restart and remove containers
- Image-update detection with one-click update
- Container logs, stats and health reports
- Grouping by server and by Docker Compose project

### Server & host management
- Multi-server support (local, SSH, and TCP/mTLS mesh)
- Firewall management (ufw) from the web UI
- Nginx site management with a config editor
- systemd service start/stop/monitor
- SSL certificate (Let's Encrypt) status and renewal
- Integrated web terminal (host and container)
- **Connection test on save**: adding/editing a server probes the connection before closing, so a broken host isn't saved silently
- **Resilient dashboard**: an unreachable host is marked *nicht erreichbar* instead of blanking the whole view (each server is time-boxed)

### Security: mesh + mTLS (SSH-key-free)
- Management over a private WireGuard mesh (Tailscale), no management ports exposed publicly
- Docker control over **mutual TLS** (ghostunnel + a verb-whitelisting socket-proxy) instead of SSH tunnels
- Host shell commands without SSH over the same mTLS channel (a one-shot privileged `nsenter` container)
- **No stored SSH key** in steady state, the central attack surface is removed
- Own PKI (step-ca) for client/server certificates
- Telemetry via `node_exporter` > VictoriaMetrics (Prometheus-compatible)
- **One-click onboarding** of new servers (from the add/edit dialog): bootstraps over a single SSH connection authenticated by an **SSH key _or_ a root password**, installs Docker if missing, brings up Tailscale (login link surfaced in the app), deploys telemetry + the mTLS proxy, switches the host to SSH-free mTLS, and **auto-deletes the bootstrap credentials** afterwards

Design details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)

### Cloud control (out-of-band)
- Provider-agnostic power / snapshot / metrics control (Hetzner, Hostinger) via the provider API, works even when SSH/Docker is temporarily unreachable

### Monitoring & alerting
- Historical metrics (CPU, RAM, disk) in SQLite
- Notifications (Mattermost / Matrix) on:
  - container unhealthy / stopped / OOM
  - restart loops
  - image updates available
- Health reports with history

### Security scanning
- OS and container CVE scanning (Trivy) with a findings dashboard and summaries

### Deployment
- Container deployment via a form (image, ports, env, volumes)
- Docker Compose upload and deployment
- Standardised app templates for fast deployment

### AI integration
- **MCP server** exposing the full toolset to external AI agents (see below)
- **Read-only advisor chat** in the UI (optional)
- **Acting agent** that plans and executes operations tasks under inescapable guardrails (optional, see below)

---

## Tech stack

| Layer | Technology |
|---|---|
| Backend | C# / .NET 10 / ASP.NET Core |
| Frontend | Blazor Server + [MudBlazor](https://mudblazor.com/) |
| Docker API | [Docker.DotNet](https://github.com/dotnet/Docker.DotNet) |
| Database | SQLite (Entity Framework Core) + JSON file stores |
| Auth | Google OAuth 2.0 or generic OIDC + roles & email whitelist |
| Real-time | SignalR |
| MCP | [ModelContextProtocol.AspNetCore](https://github.com/modelcontextprotocol) |
| Metrics | VictoriaMetrics (Prometheus-compatible) |

---

## Quick start

### Docker (recommended)

```bash
git clone https://github.com/LupusMalusDeviant/ServerWatch.git
cd ServerWatch
cp .env.example .env
# edit .env and fill in values
docker compose up -d
```

All configuration is set through `.env` (see [.env.example](.env.example)). `.env` is gitignored, secrets never land in the repository.

The app listens on `127.0.0.1:5100` by default (configurable via `HOST_BIND` / `HOST_PORT`).

### Behind an Nginx reverse proxy

```nginx
location /serverwatch/ {
    proxy_pass http://127.0.0.1:5100/serverwatch/;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Prefix /serverwatch;
    proxy_read_timeout 300s;
    proxy_buffer_size 128k;
    proxy_buffers 4 256k;
}
```

When serving under a subpath, set `PATH_BASE=/serverwatch` in `.env`.

---

## Configuration

ServerWatch is configured entirely through environment variables (`.env`). The most important groups:

| Group | Keys | Notes |
|---|---|---|
| Authentication | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `GOOGLE_ADMIN_EMAIL`, `AUTH_DISABLED` | Set `AUTH_DISABLED=true` for trusted LAN-only deployments where Google rejects private redirect URIs |
| OIDC (optional) | `OIDC_ENABLED`, `OIDC_AUTHORITY`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET`, ... | Generic OpenID Connect (Authentik, Keycloak, Authelia, Zitadel, ...) for real 2FA/passkeys from your IdP |
| Notifications | `MATTERMOST_WEBHOOK_URL`, `MATTERMOST_ENABLED` | Matrix is configured in the UI |
| Routing | `PATH_BASE` | Path prefix when reverse-proxied under a subpath |
| AI chat | `AICHAT_ENABLED`, `AICHAT_API_KEY`, `AICHAT_API_URL`, `AICHAT_MODEL`, `AICHAT_PROVIDER` | Read-only advisor chat |
| Agent | `AGENT_ENABLED`, `AGENT_PROVIDER`, `AGENT_MODEL`, `AGENT_API_KEY`, ... | Acting agent (see below) |
| Host binding | `HOST_BIND`, `HOST_PORT` | The container always listens on `8080` internally |

See [.env.example](.env.example) for the full, commented list.

### Notable runtime details
- **Email whitelist**: managed in the UI under *Settings > Authentication*; changes apply without a restart.
- **MCP API key**: auto-generated on first start and printed to the container logs; stored in `/app/data/api-keys.json` (persisted in the Docker volume).
- **Data persistence**: SQLite, JSON stores and certificates live under `/app/data` (a bind-mount / volume); never in the image.

---

## MCP server

ServerWatch ships an integrated MCP server so AI agents (e.g. Claude Code) can operate your infrastructure. Add it to your MCP client:

```json
{
  "mcpServers": {
    "serverwatch": {
      "url": "https://your-server.com/serverwatch/mcp",
      "headers": { "Authorization": "Bearer <API-KEY>" }
    }
  }
}
```

**Permissions are enforced per API key** as Read / Write / Admin. Tools span:

- **Containers**: list/inspect/logs/metrics/env, start/stop/restart/update
- **Server & host**: info, logs, metrics, health summary, `execute_command` (Admin)
- **Deployment**: `deploy_app`, `deploy_compose`
- **Infrastructure**: firewall, Nginx, systemd, SSL
- **Databases**: detect, list, schema, query, backup
- **Networks**: list/create/remove, connect/disconnect containers
- **Logs & alerts**: search, list/create log alerts
- **Scheduler**: list/create/delete/run scheduled tasks
- **CVEs & updates**: server/container CVE summaries, update status
- **Cloud (out-of-band)**: Hetzner & Hostinger power/snapshot/metrics
- **Agent**: `instruct_agent` (delegate a natural-language task to the in-process agent)

The complete, current list with permission levels is in the web UI under *Settings > MCP*.

---

## AI agent (optional)

Beyond the MCP server (which serves *external* agents), ServerWatch has an **in-process acting agent**: you describe an operations task in natural language and it plans and executes using ServerWatch's own tools. It supports multiple LLM providers (OpenAI, OpenRouter, Ollama, Gemini, Anthropic, and Claude Code) selectable in the UI.

Its safety model is enforced in code, not in the prompt:
- **Guardrails** (a separate, admin-only `guardrails.json`) define inescapable Allow/Confirm/Deny rules evaluated at the tool boundary, "most-restrictive wins".
- The agent **inherits the rights of whoever triggered it** (web user or MCP key) and can never exceed them.
- **Hybrid autonomy**: reads run autonomously; writes/admin actions require confirmation.

See [src/ServerWatch/Services/Agent/](src/ServerWatch/Services/Agent/) for the implementation.

---

## Architecture

ServerWatch manages hosts across three independent planes, each moved off SSH:

| Plane | What | Transport |
|---|---|---|
| **Telemetry** | host CPU/RAM/disk, container stats | `node_exporter` > VictoriaMetrics (pull over the mesh) |
| **Docker control** | list/restart/deploy/inspect | Docker Engine API over **mTLS** + verb-whitelisting proxy |
| **Shell** | `execute_command`, systemd, journald, edits | one-shot privileged `nsenter` container over the same mTLS channel |

The full design, PKI, onboarding flow, trade-offs, is documented in **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**.

---

## Project structure

Each source folder carries its own `README.md` describing the files within. High-level map:

```
ServerWatch/
├── src/
│   ├── ServerWatch/            # the application
│   │   ├── Components/         # Blazor UI (Pages, Layout, Shared)
│   │   ├── Configuration/      # strongly-typed settings classes
│   │   ├── Hubs/               # SignalR hubs (container + terminal streams)
│   │   ├── Mcp/                # MCP server tools + permission layer
│   │   ├── Models/             # data models (Agent, Cloud, Cve, Hetzner, Hostinger)
│   │   ├── Services/           # all business logic (see Services/README.md)
│   │   ├── Utils/              # small helpers (secret redaction, shell quoting)
│   │   ├── wwwroot/            # static assets
│   │   └── Program.cs          # composition root (DI, middleware, MCP, auth)
│   └── ServerWatch.Tests/      # xUnit test suite
├── deploy/telemetry/           # mesh/mTLS deploy templates (node_exporter, VictoriaMetrics, Tailscale ACL)
├── docs/ARCHITECTURE.md        # SSH-key-free architecture
├── Dockerfile
├── docker-compose.yml
└── README.md
```

The `Services/` tree is the heart of the app, see [src/ServerWatch/Services/README.md](src/ServerWatch/Services/README.md) for a guided tour of every service folder.

### Code conventions
- **Interface-first**: services are defined behind an `IFoo` interface and registered in DI; consumers depend on the interface.
- **English** in-code comments and XML docs throughout; user-facing strings may be localised (currently German).

---

## Development

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
# build
dotnet build src/ServerWatch/ServerWatch.csproj

# run tests
dotnet test src/ServerWatch.Tests/ServerWatch.Tests.csproj

# run locally (listens on :8080)
dotnet run --project src/ServerWatch/ServerWatch.csproj
```

---

## Security

- No standing SSH key is required for managed servers in steady state (see [Architecture](#architecture)).
- All management ports are mesh-bound; nothing management-related is exposed to the internet by design.
- Secrets live in `.env` and `/app/data` (both gitignored / volume-mounted), never in the image or repository.
- MCP access is gated by per-key Read/Write/Admin permissions; the acting agent is bounded by code-enforced guardrails.

If you discover a security issue, please report it privately, see [SECURITY.md](SECURITY.md).

---

## Roadmap

Beta is feature-rich but not finished. Planned / not-yet-implemented:

**Notification channels** (in addition to the in-app bell, Mattermost and Matrix)
- Email (SMTP), Telegram, Ntfy / Gotify, Discord, Slack, and a generic outbound webhook.

**In-app notifications**
- Persistent history (survives restarts) and a dedicated notifications page with filtering.

**Monitoring & triggers**
- Proper traffic / anomaly detection and a dedicated "extreme traffic" trigger (today: sustained
  CPU/RAM thresholds + a simple rolling-z-score outlier).
- Disk-usage alerts (e.g. > 90%).

**Settings**
- Bring the remaining env settings into the UI (Terminal, AI chat, agent, MCP throttle); surface the
  restart-only settings (auth providers, `PATH_BASE`, host binding) read-only with a clear note.

**Fleet & deployment**
- Server groups / tags; richer Compose templates.
- Lightweight Kubernetes (**k3s**) support, discover and operate k3s clusters alongside Docker hosts.

**Agent governance** (building on the shipped Agent-History, Freigaben/Human-in-the-Loop and rich
chat widgets)
- Extend approvals to **block external/direct MCP calls** too, today the Human-in-the-Loop gate
  covers the in-process agent; direct `tools/call` requests are recorded but not held for approval.
- Real per-tool **diffs** in the approval card (show exactly what a write would change).
- A fuller **rich-widget / MCP-Apps** catalog beyond the curated chart + status card.

**Hardening & resilience**
- Finish the zero-SSH-key migration tooling (Tailscale + step-ca + ghostunnel/socket-proxy).
- **Break-glass / disaster recovery**: short-lived SSH certificates issued via step-ca as an
  auditable emergency-access path when the normal control plane is unavailable.

Have a request? Open an issue.

---

## Contributing

Issues and pull requests are welcome. Please:
- keep changes interface-first and add/extend tests under `src/ServerWatch.Tests/`;
- run `dotnet build` (0 warnings) and `dotnet test` before opening a PR;
- write in-code comments and docs in English.

---

## License

Apache License 2.0, see [LICENSE](LICENSE).

Copyright © 2026 ServerWatch Contributors
