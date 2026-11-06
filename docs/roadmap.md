# Roadmap

Planned, not yet implemented. Requests: open an issue.

## Accounts & roles

- 2FA (TOTP) and passkeys for the local login; finer-grained per-server roles / teams.

## Monitoring & triggers

- Traffic / anomaly detection and a dedicated "extreme traffic" trigger (today: sustained CPU/RAM/disk thresholds + a
  simple rolling-z-score outlier).

## Fleet & deployment

- Live Docker events (vs. polling); editing resource limits in place; richer Compose templates.
- Kubernetes track B.3: pod **exec terminal**, MCP tools for pods, metrics-server stats (managing k3s clusters and
  running on Kubernetes via Helm shipped in 0.12).

## Agent governance

Building on the shipped Agent History, approvals and chat widgets:

- Per-tool **diffs** in the approval card (show exactly what a write would change).
- A fuller **rich-widget / MCP-Apps** catalog beyond the curated chart + status card.

## Hardening & resilience

- **Break-glass / disaster recovery**: short-lived SSH certificates issued via step-ca as an auditable emergency-access
  path when the normal control plane is unavailable.
- **Distroless/chiseled "remote" image** for the hardened profile (a locked-down container profile and a pluggable
  Tailscale/NetBird VPN already ship — see [container-hardening.md](container-hardening.md)).
