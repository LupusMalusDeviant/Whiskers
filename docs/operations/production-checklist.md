# Production checklist

Go through this list before Whiskers manages hosts that matter. Each item names where it is set and how to check it.
The in-app counterpart is *Settings → Production-ready?*: every line there should be OK or a decision you made on
purpose. That card is informational; it gates nothing.

Read [Accepted limits](#accepted-limits-10) at the end first: they say what Whiskers 1.0 does **not** protect, so the
rest of the list is not mistaken for more than it is.

## Install

- [ ] **One pinned release, signature verified.** The installer pins `X.Y.Z` and, with cosign installed, the verified
  digest (`image: …:X.Y.Z@sha256:…`). Use `--verify require` on production hosts. No `latest`.
- [ ] **The right profile.** The default `docker-compose.yml` is privileged (host PID namespace, root) so Whiskers can
  manage its own host. If it only manages remote hosts, use `docker-compose.hardened.yml` (non-root, read-only root
  file system, socket proxy) — [container-hardening.md](../container-hardening.md). The readiness line
  "Hardened-Profil (non-root)" shows which one runs.
- [ ] **Not on a public interface.** The installer binds to `127.0.0.1` by default (`--bind <addr>` or
  `WHISKERS_BIND`, for a new install only; afterwards edit the `ports:` line of the compose file it wrote). The
  repository's own `docker-compose.yml` takes `HOST_BIND` (also `127.0.0.1` by default). Reach Whiskers through a
  TLS-terminating reverse proxy or over the mesh. Management ports of managed hosts stay mesh-bound.
- [ ] **HTTPS at the proxy, and the proxy trusted.** The readiness line "HTTPS am Reverse Proxy" is OK when the page
  came over HTTPS. Name the proxy in `ForwardedHeaders__TrustedNetworks__0` (narrowly, `/32` or `/128`); until then the
  readiness card names it as an ignored peer, the login limit and the audit see the proxy, and cookies lose `Secure`.
- [ ] **Single replica.** Compose runs one container; Helm pins `replicas: 1` with `Recreate`. Do not scale it.

## Sign-in and access

- [ ] **`AUTH_DISABLED=false`.** `true` makes every visitor an Admin; it is for an isolated trial only, never behind
  a TCP forward or Tailscale Funnel.
- [ ] **Setup wizard completed** right after the install (it needs the one-time token from
  `/app/data/setup-token.txt` or `WHISKERS_SETUP_TOKEN`).
- [ ] **Sign-in through your identity provider with MFA.** Local accounts have no second factor in 1.0; keep them as
  break-glass with long, unique passwords ([local-accounts.md](local-accounts.md)). OIDC: `OIDC_REQUIRE_HTTPS=true`
  (the readiness card warns otherwise); IdP requirements in [identity-bindings.md](identity-bindings.md).
- [ ] **Few Admins, tight whitelist.** Review *Settings → Authentication* (whitelist) and *Settings → User roles*.
  Roles are global (see limits).
- [ ] **MCP keys with the least level** they need, one key per client, unused keys deleted (*Settings → MCP
  permissions*).
- [ ] **Guardrails restrictive.** Keep the agent presets narrow; set `RequireSecondApprover` in the policy where one
  person must not approve their own interactive request; AI triggers stay read-only unless granted capabilities.
- [ ] **Automation owners recorded.** AutoUpdate, AutoPatch and the in-app VPN bring-up run on the authority of the
  Admin who took them over (*Settings → Automation owners*). Without an owner AutoPatch and the VPN bring-up never
  run; AutoUpdate does not run either unless someone opted into its grace period — leave that off.

## Secrets

- [ ] **`VAULT_KEY` set as a file** (`VAULT_KEY_FILE`, mode 0600; the installer does this) and the readiness line
  "Secret-Vault aktiv (VAULT_KEY)" OK.
- [ ] **A copy of `VAULT_KEY` off the host** (password manager or offline media), kept apart from the backups.
  Without it the vault and every encrypted backup are unreadable.
- [ ] **No plaintext credentials left:** the readiness lines for cloud/DNS, notification/AI credentials, webhook
  secrets, rollback snapshots and session keys are OK.
- [ ] **Credentials in the environment kept to a minimum.** Settings such as `AICHAT_API_KEY`, `AGENT_API_KEY`,
  `MATTERMOST_WEBHOOK_URL` or `HARBOR_PASSWORD` are plain environment variables: they have **no** `…_FILE` variant,
  Whiskers does not put them into the vault, and they are visible in `docker inspect`. Prefer entering such
  credentials in the UI (they are then stored in the vault); where the environment is needed, keep the `.env` mode
  0600 and out of the image. File variants exist only for `VAULT_KEY_FILE`, `WHISKERS_DB_CONNECTION_FILE`,
  `WHISKERS_ADMIN_PASSWORD_FILE` and the VPN enrollment keys (`TAILSCALE_AUTHKEY_FILE` / `NETBIRD_SETUP_KEY_FILE`,
  read as `Vpn__Tailscale__AuthKeyFile` / `Vpn__Netbird__SetupKeyFile`); a `…_FILE` name invented for any other
  setting is ignored.
- [ ] **The data directory is protected** like a root credential: see the table below.

### What the data directory holds

| Encrypted with `VAULT_KEY` (vault) | Encrypted with a key derived from `VAULT_KEY` | Not encrypted — file permissions only |
|---|---|---|
| cloud API keys, the DNS token, notification channels' credentials, the AI chat's and the agent's API keys, kubeconfigs, registry and Git credentials | inbound webhooks' signing secrets and rollback snapshots (in the database), the data-protection key ring (`keys/`) | SSH private keys (`ssh-keys/`), all mTLS client keys (`mtls/`: the shared one and one per host), the database (metrics, audit trail, accounts with hashed passwords), the JSON stores (roles, whitelist, servers, MCP key hashes, settings), the VPN state (`tailscale/`, `netbird/`), the setup token until setup is complete |

Whoever can read the data directory, or run code in the Whiskers container, can act as Whiskers on every managed
host. Self-backups hold the same data; with `VAULT_KEY` set they are encrypted.

## Backups

- [ ] **A scheduled self-backup** (*Scheduled tasks*, type *SelfBackup*); the readiness line "Self-Backup eingeplant" is OK.
- [ ] **Copies off the volume and off the host**, on a schedule of their own
  ([backup-and-recovery.md](backup-and-recovery.md#keep-a-copy-off-the-volume)).
- [ ] **One restore tested** on a spare instance, including the off-volume recovery. A backup nobody restored is a
  hope, not a backup.
- [ ] **PostgreSQL:** a dedicated owner role that is not a superuser; client tools at least as new as the server (the
  readiness line "PostgreSQL-Client-Tools für das Self-Backup").
- [ ] **Upgrade procedure known:** [upgrade-and-rollback.md](upgrade-and-rollback.md).

## Monitoring

- [ ] **`/healthz` and `/readyz` probed** by the orchestrator or your monitor ([troubleshooting.md](troubleshooting.md#healthz-and-readyz)).
- [ ] **`/metrics` scraped with a token** (`Metrics__ScrapeToken`; without one the endpoint is off) and the alert rules
  for loop stalls, missing operation outcomes and audit-write failures in place ([observability.md](observability.md#signals-to-alert-on)).
- [ ] **Structured logs** if you ship logs: `Logging__Console__FormatterName=json` with scopes
  ([observability.md](observability.md)).
- [ ] **A notification channel** that reaches a person, tested.
- [ ] **Audit retention** chosen (`Audit__RetentionDays`, default 365, at least 30) and the audit export reachable for
  Admins.

## Managed hosts

- [ ] **Onboarded hosts run over the mesh and mTLS**, not over a standing SSH key ([ARCHITECTURE.md](../ARCHITECTURE.md)).
- [ ] **Every mTLS host on its own client identity**, the shared one retired once no host admits it
  ([per-host-mtls-migration.md](per-host-mtls-migration.md)).
- [ ] **Critical containers listed as protected resources** in the guardrails. Whiskers' own control plane is
  protected by a built-in rule (INV-59); your databases and proxies are not, until you list them.

## Accepted limits (1.0)

These are deliberate limits of Whiskers 1.0 (product contract §5). They are not bugs to be found later.

- **Root-equivalent administration.** The Docker API and the host-control plane are root on every managed host. An
  Admin, an Admin-level key or a compromised Whiskers instance is root everywhere Whiskers manages. Whiskers decides
  *who* reaches that power and records it; it does not make it smaller.
- **Host access is outside the contract.** Someone with a shell on the Whiskers host can change its database, stores
  and binaries. With `VAULT_KEY` set, database-only edits of the audit trail are detectable, except that the events
  written **after** the newest signed checkpoint (at most about a minute's worth) can be cut off unnoticed. Without
  `VAULT_KEY` nothing is signed, and someone who can write the database can rewrite the trail consistently; someone
  who holds `VAULT_KEY`, the checkpoint file and the database can rewrite all three (ADR-0006).
- **Single instance.** One process, one data directory, no replica and no failover. Availability and the recovery
  point come from your backups ([backup-and-recovery.md](backup-and-recovery.md#recovery-point-and-time)).
- **Global roles.** Viewer, Operator and Admin, and key levels, apply to every managed server. There is no per-server or
  per-tenant scoping.
- **No terminal transcript.** Opening and closing a terminal is authorized, checked against the kill switch and
  audited; what is typed inside a session is neither policy-checked nor recorded.
- **Out-of-band changes are invisible.** Changes made directly on a host, in a cloud console or by another tool are not
  governed. Whiskers may see their effects; it does not attribute them.
- **No second factor for local accounts.** Use your identity provider's MFA.
