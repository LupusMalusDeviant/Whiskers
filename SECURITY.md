# Security Policy

Whiskers is infrastructure-management software: it reaches into Docker hosts, runs host
commands, manages firewalls/Nginx/systemd, and exposes an MCP server to AI agents. Security is a
first-class concern of the design, please treat findings accordingly.

## Reporting a vulnerability

**Please report vulnerabilities privately, do not open a public issue.**

Use GitHub's private reporting: **Security > Advisories > "Report a vulnerability"** on this
repository. Include:

- a description and impact assessment,
- steps to reproduce (a minimal PoC if possible),
- affected version / commit,
- any suggested remediation.

### What you can expect

Whiskers has a single maintainer, so these are commitments sized to that — not a staffed SOC:

| Step | Target |
|---|---|
| Acknowledgement of your report | within **72 hours** |
| Triage: confirmed or not, severity (CVSS) | within **7 days** |
| Fix released for **critical / high** | within **14 days** of confirmation |
| Fix released for **medium / low** | within **30 days**, or with the next release |
| Public advisory (GitHub Security Advisory, credit if you want it) | with the fixed release |

If a target slips, you will hear so — with a reason and a new date — rather than silence. Please allow
until the fixed release before any public disclosure (coordinated disclosure); after 90 days you are free
to disclose regardless.

## Supported versions

Whiskers is currently **beta** (`0.15.x`). Only the latest release / `main` receives security
fixes. There is no long-term-support branch yet.

| Version | Supported |
|---|---|
| `0.15.x` (beta) | ✅ latest only |
| `< 0.15` | ❌ |

## Security model (summary)

### Architecture in one paragraph

Whiskers is a **single-replica appliance**: one Blazor Server process with its background loops and its
state (SQLite by default, PostgreSQL optional) in one data directory. Compose runs one container and the
Helm chart pins `replicas: 1` with a `Recreate` strategy; there is no clustering and no shared session
store. Availability and backup of that one instance are the operator's responsibility (self-backup is
built in).

### Transport: SSH for bootstrap, mTLS planes for operation

- **SSH is used only to bootstrap a host.** Onboarding opens one SSH connection, with host-key
  verification (trust-on-first-use, then pinned), to join the host to a private WireGuard mesh
  (Tailscale/NetBird) and roll out the mTLS certificates, the Docker proxy and telemetry. The bootstrap
  credential is deleted afterwards. A server added as a plain SSH connection and not yet onboarded keeps
  using SSH until it is migrated.
- **Steady-state operation runs key-free over three separate planes:** telemetry (`node_exporter` →
  VictoriaMetrics over the mesh), the Docker API (Docker Engine API over mutual TLS, client CN pinned,
  behind a verb-whitelisting socket proxy) and host control (a one-shot `nsenter` container driven over
  the same mTLS channel). Whiskers holds **no standing SSH key** for an onboarded host. See
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- **Management ports are mesh-bound**: nothing management-related is exposed to the public internet by
  design.

### Policy enforcement (scopes, guardrails, approvals)

- **Deterministic policy-enforcement layer (Read/Write/Admin scopes, action classification and
  human-in-the-loop approvals)**, evaluated in code at the tool boundary — not in a prompt.
- **Scopes** apply identically to every MCP tool call: a signed-in web user is mapped by role
  (Viewer → Read, Operator → Write, Admin → Admin), an MCP API key by its scope (or explicit tool list),
  the in-process agent by the scope of whoever triggered it — it can never exceed that.
- **Guardrails** (Allow / Confirm / Block, protected resources, forbidden arguments, read-only kill switch;
  most-restrictive wins) apply identically to every tool call: those the in-process agent executes
  (started from the web UI or via the MCP `instruct_agent` tool) and **direct tool calls from external MCP
  clients**. For the agent, `Confirm` pauses the run until an authorized human approves the exact action
  and parameters. For a direct call it raises the same approval and refuses the call; once approved, the
  identical call (same caller, tool and arguments) runs exactly once within 10 minutes. Approving requires
  at least the level the action needs. The engine ships with the agent module (on by default); with that
  module disabled, direct calls are bounded by their scope alone.
- **MCP API keys** are created in the web UI (*Settings → MCP*) with an explicit scope and can be disabled
  or deleted there. A key is 256 bits from a CSPRNG, shown once at creation, and **stored only as its
  SHA-256 hash**; presented keys are hashed and compared in constant time. The ServerWatch-era flat key file
  `api-keys.json` (unscoped, plaintext) has been removed completely; leftover copies are deleted on start.
- **Accountability:** user actions land in the Audit Log; every agent and direct MCP tool call lands in
  Agent History (actor/key, redacted parameters, decision, result).

### Threat model: what is and is not protected

- **The Docker-API and host-control planes are root-equivalent on the managed host.** Creating a container
  (needed for deploys) or running `nsenter` is root by construction; the socket proxy's verb whitelist
  narrows the API surface but does not change that. Consequently, **an Admin-scoped key, an Admin user or
  a compromised Whiskers instance is root on every managed host.** The controls above decide who reaches
  that capability; they do not reduce what it is.
- **The Whiskers instance is the crown jewel.** Its data directory holds the mTLS client key, the vault,
  the MCP key hashes (`mcp-permissions.json`), provider tokens and the database. Vault-stored secrets are
  encrypted at rest (AES-256-GCM, key derived from `VAULT_KEY` or `VAULT_KEY_FILE`): cloud API keys, the DNS token,
  the notification channels' credentials (SMTP password, chat and bot tokens, webhook URLs and secrets, the ntfy topic
  and token), the AI chat's and the agent's API keys, kubeconfigs, registry and Git credentials. Encrypted with a key
  derived from the vault's: the inbound webhooks' signing secrets and the rollback snapshots (a container's full
  configuration, its environment included) in the database, and the data-protection key ring (`keys/`, sign-in cookies
  and antiforgery). The SSH private keys (`ssh-keys/`) and the mTLS client key are still stored in plaintext; those and
  the other files in the data directory rely on file-system and volume protection. Whoever can read that directory, or run code in the Whiskers
  container, can act as Whiskers.
- **The default `docker-compose.yml` profile is privileged** (host PID namespace) so it can manage its own
  host. Use `docker-compose.hardened.yml` when Whiskers only manages remote hosts.
- **Secrets** (`.env`, API keys, tokens, certificates, the encrypted vault, the SQLite DB) live under
  `/app/data` / `.env`, both gitignored and volume-mounted, never baked into the image or committed.

## VAULT_KEY loss, change and rotation

The vault's key also derives the keys for the webhook secrets, the rollback snapshots and the data-protection key ring
(R1-31). There is no in-place rotation of `VAULT_KEY` yet; what a change or loss does:

- **Changed or lost key:** the vault is locked (it is left unchanged until the right key is set again). Stored
  credentials cannot be read; webhooks refuse every delivery until their secret is regenerated with a usable vault;
  rollback snapshots cannot be rolled back to; **everybody is signed out** — the key ring cannot be opened, so a new
  data-protection key is created and, while the vault is locked, kept in memory only (sign-in works, sessions end at
  the next restart, no new key is written in plaintext; key files that were plaintext already stay so until the right
  key is back, and the readiness check counts them). Setting the right key again restores all of it.
- **Upgrade to R1-31:** nobody is signed out. Key files, webhook secrets and snapshots written in plaintext before are
  encrypted in place at the first start with a usable vault, and keep working.
- **No `VAULT_KEY`:** the data-protection keys are written in plaintext as before; webhooks cannot be created or have
  their secret regenerated; updates keep no rollback snapshot. The readiness check names what is still in plaintext.
- **Backups:** a self-backup holds the encrypted values; restoring it needs the same `VAULT_KEY`.

## Hardening checklist for operators

- Set `AUTH_DISABLED=false` and put a real IdP (Google OAuth or OIDC) in front, or keep the app on a
  trusted private network only. With `AUTH_DISABLED=true` Whiskers itself refuses public client addresses,
  non-IP host names (DNS rebinding) and cross-site origins; narrow `Auth:DisabledAllowedNetworks` further.
- Complete the setup wizard right after installation. It requires the one-time token from
  `/app/data/setup-token.txt` (or `WHISKERS_SETUP_TOKEN`), so an exposed port alone is not enough to claim Admin.
- Keep the email whitelist tight; review roles under *Settings > Authentication*.
- Issue MCP API keys with the **least** permission level needed.
- Keep the agent's guardrail presets restrictive; only widen autonomy deliberately.
- Set a strong `VAULT_KEY` (or mount it as a file via `VAULT_KEY_FILE`) and protect the `/app/data` volume (it still holds plaintext SSH and
  mTLS client keys). Without a usable vault Whiskers creates no webhook and keeps no rollback snapshot (an update then
  goes ahead without a way back), and writes the data-protection keys as before. Credentials entered in the UI need the vault;
  credentials set by environment variables (`AICHAT_API_KEY`, `AGENT_API_KEY`, `MATTERMOST_WEBHOOK_URL`, …) are not
  stored by Whiskers — keep them in a secret store or a mode-0600 `.env`, never in the image.
- Keep dependencies current, Dependabot config ships in `.github/dependabot.yml`.
- **Run the least-privileged container profile that fits.** The default `docker-compose.yml` is
  privileged because it can manage its own host (nsenter host-shell). If Whiskers only monitors
  *remote* hosts, deploy `docker-compose.hardened.yml` instead (non-root, no `privileged`, dropped
  capabilities, read-only rootfs, Docker via a verb-restricted socket-proxy). See
  [docs/container-hardening.md](docs/container-hardening.md).
