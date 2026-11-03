# Services

All of Whiskers's business logic lives here. Each subfolder is one cohesive capability, defined **interface-first** (`IFoo` + `Foo`) and registered from the composition root in [`../Startup/`](../Startup/) (Core services + each enabled module's `ConfigureServices`). UI components ([`../Components`](../Components)) and MCP tools ([`../Mcp`](../Mcp)) depend on these interfaces.

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
| [`Maintenance/`](Maintenance/) | Process-wide maintenance flag: gates requests with 503 and drains `/readyz` while a self-restore is staged |

## Monitoring & security

| Folder | Responsibility |
|---|---|
| [`Metrics/`](Metrics/) | Metric collection and querying (Docker stats + Prometheus/VictoriaMetrics sources) |
| [`HealthMonitor/`](HealthMonitor/) | Background container health watching and health reports |
| [`LogMonitor/`](LogMonitor/) | Log search and pattern-based log alerts |
| [`Cve/`](Cve/) | OS and container CVE scanning (Trivy) and findings storage |
| [`AuditLog/`](AuditLog/) | Append-only audit trail of privileged actions |
| [`Mutations/`](Mutations/) | The Mutation Gateway (ADR-0005): capability catalog, decide → execute → verify → audit pipeline, scope guard around mutating services |
| [`Guardrails/`](Guardrails/) | The guardrail policy engine, its store and the direct-MCP guard (core since ADR-0005 step 2.1) |
| [`Approvals/`](Approvals/) | Human-in-the-Loop approvals: store and the gate that binds an approval to exactly one call |

## Deployment & updates

| Folder | Responsibility |
|---|---|
| [`Deployment/`](Deployment/) | Container and Docker Compose deployment |
| [`Templates/`](Templates/) | Standardised app templates for one-click deployment |
| [`ImageSearch/`](ImageSearch/) | Search container images across multiple registries ("marketplaces": Docker Hub, GHCR, Harbor) |
| [`ImageUpdate/`](ImageUpdate/) | Detect newer image tags/digests in registries |
| [`AutoUpdate/`](AutoUpdate/) | Scheduled automatic container updates |
| [`AutoPatch/`](AutoPatch/) | Nightly same-tag security patching of containers with fixable CVEs |
| [`Backup/`](Backup/) | Docker **volume** backups, plus Whiskers' own **self-backup & restore** of `/app/data` (VAULT_KEY-encrypted archives, crash-safe deferred-swap restore) |
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
| [`AiChat/`](AiChat/) | Read-only advisor chat (guidance only, no actions) |
| [`Agent/`](Agent/) | The acting agent: multi-provider LLM loop with inescapable guardrails (presets) + AI triggers for autonomous runs |

## Conventions

- **Interface-first**: every service is consumed through its `IFoo` interface; the concrete `Foo` is bound in DI. Hosted/background services are dual-registered (concrete singleton + interface forwarder + `AddHostedService`) — see the [`AddSingletonWithInterface`](../Configuration/ServiceCollectionExtensions.cs) helpers.
- **Async init**: a singleton that needs startup warm-up (loading a JSON store, seeding defaults) implements [`IInitializable`](IInitializable.cs) (`int Order` + `InitializeAsync(ct)`); `Program.cs` runs them all in ascending `Order` after `Build()`, rather than hand-wiring each call.
- **Loop failures** (R1-41): every loop derives from [`FleetBackgroundService`](FleetBackgroundService.cs) and catches a cycle's failure in one of the two shapes the ratchet accepts: `catch (Exception ex) when (!IsHostStopping(ex, stoppingToken))`, or `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)` followed by an unfiltered `catch (Exception)`. Either way only a cancellation while the host stops ends the loop; a dependency's own timeout or cancellation is a failed cycle and the next one runs. `BackgroundServiceFailureTests` holds every loop's `RunAsync` to it by reading its syntax tree (the catch clause, its type and filter, that it sits inside the outermost loop and that its body neither breaks out, returns nor throws) — comments and strings do not count. A `RunAsync` without a loop passes only for a service on the test's `OneShotServices` allow-list (today `InterruptedWorkReconciler`); anywhere else it would hide the loop in a helper the ratchet cannot see.
- **Graceful shutdown** (R1-41): a loop **owns** its cycle — it awaits it, it does not abandon it with `WaitAsync(ct)`. An abandoned cycle went on after the stop and, once its listing answered, recorded metrics, sent notifications and changed state. Instead the stopping token reaches every dependency a cycle waits on (the fleet and per-server listings and log reads including their waits for a Docker client and a budget slot, inspects and digest reads, the registry, the notification delivery and its retries — each has a token overload, `IDockerService`, `IRegistryClient.GetRemoteDigestAsync(image, ct)`, `INotificationService.SendAsync(evt, ct)`), and the cycle checks the token before each effect, so a dependency that answers after the stop changes nothing. The host's shutdown timeout still bounds the wait. `LoopShutdownTests` holds the image-update checker and the health monitor to it; the health, log, log-hygiene, image-update and auto-update loops were converted (the auto-updater finishes and records a recreate that already ran rather than leaving it unobserved). Idle waits pass the token as before.
- **Persistence**: durable state goes through [`Persistence/`](Persistence/) (SQLite for time-series/structured data, JSON file stores for config) under the data directory (`WHISKERS_DATA_DIR`, default `/app/data`), resolved centrally by [`../Configuration/DataPathOptions.cs`](../Configuration/DataPathOptions.cs).
- **English** comments and XML docs throughout.
