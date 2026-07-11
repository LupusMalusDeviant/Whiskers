# Changelog

All notable changes to Whiskers are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow SemVer
(0.x = pre-1.0, minor bumps may contain breaking changes — noted explicitly).

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
- **SSH host keys are now verified** (trust-on-first-use, pinned in
  `<data>/ssh-keys/known_hosts`). *Behavior change:* an intentionally rebuilt server needs
  its line removed from that file before reconnecting.
- **Fail-closed authorization**: every endpoint/page requires authentication unless it
  explicitly opts out (login, setup wizard, health probes, HMAC webhooks, token-gated
  metrics). The SignalR hub is no longer reachable anonymously.
- Fresh installs no longer seed the "local" Docker server when no Docker socket is present
  (Kubernetes deployments start with an empty fleet).

### Fixed
- MCP bearer scheme is parsed case-insensitively (RFC 7235).
- The webhook UI test button now sends a genuinely signed request.
- `DockerService` split into focused internals (no behavior change); numerous smaller fixes.

## [0.11.0] and earlier

Pre-publication development (dashboard, MCP server + acting agent with guardrails and
approvals, CVE scanning with dedup/age tracking, zero-SSH mesh+mTLS onboarding, cloud
control for Hetzner/Hostinger, terminal, deployments, notifications, audit log, …).
History: `git log v0.12.0` and `docs/reviews/`.
