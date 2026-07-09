# Services

All of Whiskers's business logic lives here. Each subfolder is one cohesive capability, defined **interface-first** (`IFoo` + `Foo`) and registered in [`Program.cs`](../Program.cs). UI components ([`../Components`](../Components)) and MCP tools ([`../Mcp`](../Mcp)) depend on these interfaces.

Every subfolder carries its own `README.md` with a per-file breakdown. Tour:

## Core infrastructure

| Folder | Responsibility |
|---|---|
| [`Docker/`](Docker/) | Docker Engine connections (local, SSH-tunnel, TCP/mTLS) and all container/image/network operations |
| [`Server/`](Server/) | Host-level operations, shell command execution (SSH-free over mTLS), firewall, Nginx, systemd, SSL |
| [`ServerConfig/`](ServerConfig/) | The fleet registry: which servers exist and how to reach each one |
| [`Terminal/`](Terminal/) | Interactive web-terminal sessions (host and container) |
| [`Persistence/`](Persistence/) | SQLite (EF Core) database context and generic JSON file stores |
| [`Database/`](Database/) | Detect and query databases running inside managed containers |

## Monitoring & security

| Folder | Responsibility |
|---|---|
| [`Metrics/`](Metrics/) | Metric collection and querying (Docker stats + Prometheus/VictoriaMetrics sources) |
| [`HealthMonitor/`](HealthMonitor/) | Background container health watching and health reports |
| [`LogMonitor/`](LogMonitor/) | Log search and pattern-based log alerts |
| [`Cve/`](Cve/) | OS and container CVE scanning (Trivy) and findings storage |
| [`AuditLog/`](AuditLog/) | Append-only audit trail of privileged actions |

## Deployment & updates

| Folder | Responsibility |
|---|---|
| [`Deployment/`](Deployment/) | Container and Docker Compose deployment |
| [`Templates/`](Templates/) | Standardised app templates for one-click deployment |
| [`ImageSearch/`](ImageSearch/) | Search container images across multiple registries ("marketplaces": Docker Hub, GHCR, Harbor) |
| [`ImageUpdate/`](ImageUpdate/) | Detect newer image tags/digests in registries |
| [`AutoUpdate/`](AutoUpdate/) | Scheduled automatic container updates |
| [`Backup/`](Backup/) | Volume backup operations |
| [`Onboarding/`](Onboarding/) | One-click mesh + mTLS server onboarding (Tailscale, step-ca, ghostunnel) |
| [`Vpn/`](Vpn/) | Pluggable mesh-VPN bring-up (Tailscale / NetBird / none) decoupled from the app image |

## Integrations

| Folder | Responsibility |
|---|---|
| [`Cloud/`](Cloud/) | Provider-agnostic out-of-band cloud control (dispatches to Hetzner/Hostinger) |
| [`Hetzner/`](Hetzner/) | Hetzner Cloud API client |
| [`Hostinger/`](Hostinger/) | Hostinger API client |
| [`Notifications/`](Notifications/) | Mattermost / Matrix notification dispatch + container notification prefs |
| [`Webhooks/`](Webhooks/) | Inbound/outbound webhook handling |
| [`Scheduler/`](Scheduler/) | Cron-style scheduled task execution |

## Auth, config & AI

| Folder | Responsibility |
|---|---|
| [`Auth/`](Auth/) | Roles, email whitelist, and the current-user accessor |
| [`Mcp/`](Mcp/) | MCP permission service and API-key store (the authorization layer for MCP tools) |
| [`Vault/`](Vault/) | Encrypted-at-rest storage for secrets (API keys, tokens) |
| [`ConfigExport/`](ConfigExport/) | Export non-secret app configuration as JSON |
| [`AiChat/`](AiChat/) | Read-only advisor chat (guidance only, no actions) |
| [`Agent/`](Agent/) | The acting agent: multi-provider LLM loop with inescapable guardrails (presets) + AI triggers for autonomous runs |

## Conventions

- **Interface-first**: every service is consumed through its `IFoo` interface; the concrete `Foo` is bound in DI. Hosted/background services are dual-registered (concrete singleton + interface forwarder + `AddHostedService`).
- **Persistence**: durable state goes through [`Persistence/`](Persistence/) (SQLite for time-series/structured data, JSON file stores for config) under the data directory (`WHISKERS_DATA_DIR`, default `/app/data`), resolved centrally by [`../Configuration/DataPathOptions.cs`](../Configuration/DataPathOptions.cs).
- **English** comments and XML docs throughout.
