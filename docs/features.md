# Features

What Whiskers does, grouped by area. Docker, Kubernetes and the rest are targets the control plane governs.

## Governance

- **Per-key permissions** — MCP API keys are scoped Read / Write / Admin, or to an explicit tool list.
- **Code-enforced guardrails** — per-tool **Allow / Confirm / Block**, evaluated at the tool boundary (not in the
  prompt), "most-restrictive wins".
- **Human-in-the-loop approvals** — a `Confirm` tool pauses execution until an authorized user approves the exact
  action + parameters.
- **Agent History** — every agent tool call recorded: actor/key, redacted parameters, decision, result, duration,
  target.
- **Audit Log** — every user action recorded, with a filterable history.

Details: [governance.md](governance.md).

## Operations

- **Containers** — live dashboard (CPU / memory / health) across all hosts; start / stop / restart / remove; logs,
  stats, health reports; grouping by server, Compose project and free-form **tags**.
- **Kubernetes** — add a k3s/Kubernetes cluster via kubeconfig (stored encrypted in the vault); pods appear next to
  containers with owner grouping, logs and scale/rollout actions.
- **Host management** — firewall (ufw), Nginx sites, systemd services, SSL certificates and an integrated web terminal
  (host and container).
- **Deployments** — a deploy form, Compose upload, curated app templates, image search across registries, and **git
  deploy** (repo → cloned/built/composed on the target host; push-triggered redeploys via HMAC-signed webhooks).

### Compose deployments and exposure changes

A compose deployment that changes how a service is reachable or what it may do on its host is decided **as such**
(contract 0006, INV-62): a published port, the host network, `privileged`, added capabilities, host namespaces
(`pid`/`ipc`/`uts`/`userns_mode`/`cgroup`), devices, unconfined security options, device cgroup rules, a namespace
shared with another container, and route labels — Tailscale Funnel/serve (DockTail), Traefik routers and
caddy-docker-proxy (`ExposureLabelPrefixes` in the guardrail policy). This holds for the Compose editor, the app
templates, MCP `deploy_compose`, `set_container_env`, the container page's `.env` save, compose webhooks and Git deploy.

- **Agents and API keys** need a confirmation for the classes in `ExposureConfirmClasses` (default `public` and
  `host`) — also under a policy that lets Admin act autonomously. An unattended run owned by a key cannot get one and
  is refused. **Persons** are not asked to confirm their own action; their decision is audited with every exposure,
  and a `public` or `host` exposure sends an `exposure_change` notification.
- **Every run** writes the files, pulls (`--ignore-buildable`), builds (`--pull`), inspects every image it is about to
  start (a label of a confirm class that nobody decided refuses an agent's run before anything starts; the previous
  image tags are put back), starts with `up -d --pull never --no-build`, and then checks the containers it created
  against what was decided. Under the agent rule set an undecided exposure is stopped and its restart policy switched
  off; every such finding sends `exposure_violation`.
- Values that come from the `.env`, and label values that could be secrets, are never written to the audit, an
  approval card, a reason or a notification — only the service, the kind and the key.

**Upgrade note.** The new policy defaults change every policy's version hash, so open approvals and the policy
snapshots of pinned runs expire at the upgrade. `docker compose pull --ignore-buildable` needs **Docker Compose v2.15
or newer** on every managed host. A label key, a port, a network or a privilege field from an `.env` variable that is
not set now refuses the plan (compose would read it from the host's environment). Redeploys of projects with `build:`
services rebuild with `--pull`, and Git deploys now pull images as well. With sign-in turned off, the readiness card
checks whether Whiskers' own container carries a route label.

The remaining risks of this check are listed in the [README](../README.md#compose-exposure-checks-remaining-risks).

## Security

- **CVE scanning** — OS and container images (Trivy) with a deduplicated findings dashboard, severity and age.
- **Secrets vault** — stored secrets encrypted at rest (AES-256-GCM); sensitive values redacted before logging and
  persistence.
- **Mandatory HMAC webhook secrets** (`X-Hub-Signature-256`-compatible) and **fail-closed authorization** — every
  endpoint requires authentication unless it explicitly opts out. A webhook delivery runs at most once: one Whiskers
  accepted but had to give up (no turn on its target in time, its database unavailable, a stop) ends failed, is in the
  webhook's log and the audit log and raises an `automation_refused` notification, and needs a new delivery (a new
  delivery id, or for GitHub a new push — "Redeliver" repeats the same delivery and is answered as a duplicate).
- **SSH host-key verification** (trust-on-first-use) on every ssh path.
- **Mesh + mTLS** steady-state operation (see [security-architecture.md](security-architecture.md)).

## Resilience

- **Self-backup & restore** of Whiskers' own data, the database included (SQLite snapshot or PostgreSQL `pg_dump`) —
  optionally VAULT_KEY-encrypted (AES-256-GCM), schedulable, crash-safe deferred-swap restore
  ([backup guide](operations/backup-and-recovery.md)).
- **Image-update rollback** — a snapshot before each update; one-click revert on the dashboard.
- **Monitoring & alerting** — historical CPU/RAM/disk metrics, health reports, and notifications (Mattermost, Matrix,
  Telegram, ntfy, Discord, Slack, Email/SMTP, generic webhook) on unhealthy/stopped/OOM containers, restart loops,
  sustained-load anomalies, new CVEs and available image updates, with a persistent in-app feed. Whiskers' own logs can
  be JSON, every request carries an `X-Request-Id` that leads to its operation and audit row, and `/metrics` exposes its
  loop, operation and audit health ([logs, correlation and alerts](operations/observability.md)).

## Integrations

- **MCP server** — the authenticated interface for external AI clients ([mcp.md](mcp.md)).
- **In-process AI agent** — optional ([agent.md](agent.md)).
- **Cloud control (out-of-band)** — provider-agnostic power / snapshot / metrics (Hetzner, Hostinger) via the provider
  API, even when SSH/Docker is temporarily unreachable.
- **Registries** — private-registry credentials in the vault, used automatically for authenticated pulls.
- **Database** — SQLite (zero-config) or PostgreSQL via Entity Framework Core.
- **UI** — light / dark / system theme; English (default) and German.
