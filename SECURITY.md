# Security Policy

Whiskers is infrastructure-management software: it reaches into Docker hosts, runs host
commands, manages firewalls/Nginx/systemd, and exposes an MCP server to AI agents. Security is a
first-class concern of the design, please treat findings accordingly.

## Reporting a vulnerability

**Please report vulnerabilities privately, do not open a public issue.** <!-- claim-free:policy -->

Use GitHub's private reporting: **Security > Advisories > "Report a vulnerability"** on this
repository. Include:

- a description and impact assessment,
- steps to reproduce (a minimal PoC if possible),
- affected version / commit,
- any suggested remediation.

### What you can expect

Whiskers has a single maintainer, so these are commitments sized to that — not a staffed SOC: <!-- claim-free:policy -->

| Step | Target |
|---|---|
| Acknowledgement of your report | within **72 hours** <!-- claim-free:policy --> |
| Triage: confirmed or not, severity (CVSS) | within **7 days** <!-- claim-free:policy --> |
| Fix released for **critical / high** | within **14 days** of confirmation <!-- claim-free:policy --> |
| Fix released for **medium / low** | within **30 days**, or with the next release <!-- claim-free:policy --> |
| Public advisory (GitHub Security Advisory, credit if you want it) | with the fixed release |

If a target slips, you will hear so — with a reason and a new date — rather than silence. Please allow
until the fixed release before any public disclosure (coordinated disclosure); after 90 days you are free
to disclose regardless. <!-- claim-free:policy -->

## Supported versions

Whiskers is currently **stable** (`1.0.x`). Only the latest release / `main` receives security
fixes. There is no long-term-support branch yet. <!-- claim:SEC-01 -->

| Version | Supported |
|---|---|
| `1.0.x` (stable) | ✅ latest only <!-- claim-free:policy --> |
| `< 1.0` (beta) | ❌ <!-- claim-free:policy --> |

## Security model (summary)

### Architecture in one paragraph

Whiskers is a **single-replica appliance**: one Blazor Server process with its background loops and its
state (SQLite by default, PostgreSQL optional) in one data directory. Compose runs one container and the
Helm chart pins `replicas: 1` with a `Recreate` strategy; there is no clustering and no shared session
store. Availability and backup of that one instance are the operator's responsibility (self-backup is
built in). <!-- claim:SEC-02 -->

### Transport: SSH for bootstrap, mTLS planes for operation

- **SSH is used only to bootstrap a host.** Onboarding opens one SSH connection, with host-key
  verification (trust-on-first-use, then pinned), to join the host to a private WireGuard mesh
  (Tailscale/NetBird) and roll out the mTLS certificates, the Docker proxy and telemetry. The bootstrap
  credential is deleted afterwards. A server added as a plain SSH connection and not yet onboarded keeps
  using SSH until it is migrated. <!-- claim:SEC-15 -->
- **Steady-state operation runs key-free over three separate planes:** telemetry (`node_exporter` →
  VictoriaMetrics over the mesh), the Docker API (Docker Engine API over mutual TLS, client CN pinned,
  behind a verb-whitelisting socket proxy) and host control (a one-shot `nsenter` container driven over
  the same mTLS channel). Whiskers holds **no standing SSH key** for an onboarded host. See
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). <!-- claim:SEC-16 -->
- **Management ports are bound to the mesh address**: onboarding binds the mTLS Docker proxy (port 2376) and
  `node_exporter` to the host's mesh (WireGuard) address, not to a public interface. <!-- claim:SEC-17 -->

### Policy enforcement (scopes, guardrails, approvals)

- **Deterministic policy-enforcement layer (Read/Write/Admin scopes, action classification and
  human-in-the-loop approvals)**, evaluated in code at the tool boundary — not in a prompt. <!-- claim:SEC-18 -->
- **Scopes** apply identically to every MCP tool call: a signed-in web user is mapped by role
  (Viewer → Read, Operator → Write, Admin → Admin), an MCP API key by its scope (or explicit tool list),
  the in-process agent by the scope of whoever triggered it — it can never exceed that. <!-- claim:SEC-03 -->
- **Guardrails** (Allow / Confirm / Block, protected resources, forbidden arguments, read-only kill switch;
  most-restrictive wins) apply identically to every tool call: those the in-process agent executes
  (started from the web UI or via the MCP `instruct_agent` tool) and **direct tool calls from external MCP
  clients**. For the agent, `Confirm` pauses the run until an authorized human approves the exact action
  and parameters. For a direct call it raises the same approval and refuses the call; once approved, the
  identical call (same caller, tool and arguments) runs exactly once within 10 minutes. Approving requires
  at least the level the action needs. The guardrail engine and approvals are core: they also govern
  direct MCP calls when the agent module is switched off. <!-- claim:SEC-04 -->
- **Read-only kill switch:** while it is on, every mutation on every path is refused and no terminal opens. The
  exceptions are fixed in code and listed on the *Guardrails* page (contract 0009): switching it off (an Admin in the
  UI, audited), the first-run setup while no Admin exists and the host CLIs always; a user's own password change,
  recovery-code redemption and binding a verified Google/OIDC identity at a first sign-in unless an Admin blocked them
  beforehand. None can be added; while the guardrail policy is fail-closed the last three are blocked. <!-- claim:SEC-05 -->
- **MCP API keys** are created in the web UI (*Settings → MCP*) with an explicit scope and can be disabled
  or deleted there. A key is 256 bits from a CSPRNG, shown once at creation, and **stored only as its
  SHA-256 hash**; presented keys are hashed and compared in constant time. The ServerWatch-era flat key file
  `api-keys.json` (unscoped, plaintext) has been removed completely; leftover copies are deleted on start. <!-- claim:SEC-06,SEC-19 -->
- **Accountability:** user actions land in the Audit Log; every agent and direct MCP tool call lands in
  Agent History (actor/key, redacted parameters, decision, result). <!-- claim:SEC-20 -->

### Threat model: what is and is not protected

- **The Docker-API and host-control planes are root-equivalent on the managed host.** Creating a container
  (needed for deploys) or running `nsenter` is root by construction; the socket proxy's verb whitelist
  narrows the API surface but does not change that. Consequently, **an Admin-scoped key, an Admin user or
  a compromised Whiskers instance is root on every managed host.** The controls above decide who reaches
  that capability; they do not reduce what it is. <!-- claim-free:limit -->
- **The Whiskers instance is the crown jewel.** Its data directory holds the mTLS client keys (the shared one and one per host), the vault,
  the MCP key hashes (`mcp-permissions.json`), provider tokens and the database. Vault-stored secrets are
  encrypted at rest (AES-256-GCM, key derived from `VAULT_KEY` or `VAULT_KEY_FILE`): cloud API keys, the DNS token,
  the notification channels' credentials (SMTP password, chat and bot tokens, webhook URLs and secrets, the ntfy topic
  and token), the AI chat's and the agent's API keys, kubeconfigs, registry and Git credentials. Encrypted with a key
  derived from the vault's: the inbound webhooks' signing secrets and the rollback snapshots (a container's full
  configuration, its environment included) in the database, and the data-protection key ring (`keys/`, sign-in cookies
  and antiforgery). Sealed with a key derived from the vault's (contract 0015): the SSH private keys (`ssh-keys/`) and
  every mTLS client key (`mtls/`, shared and per host); keys stored in plaintext before are sealed at the next start.
  Whiskers writes the SSH keys and the per-host mTLS keys owner-only; the readiness line "mTLS client keys protected"
  checks the mTLS keys' modes. mTLS keys are opened in memory only; an SSH key is unsealed for one use into a 0600 file
  on `/dev/shm` that is removed afterwards. A new key needs `VAULT_KEY`; keys outside the data directory are
  operator-managed and not sealed. Consequence: with a wrong or missing `VAULT_KEY` no mTLS host is reachable.
  `VAULT_KEY` can be replaced in place (`docs/operations/vault-key-rotation.md`). The other files in the data directory
  rely on file-system and volume protection. Whoever can read that directory, or run code in the Whiskers container,
  can act as Whiskers. <!-- claim:SEC-07,SEC-08,SEC-29,SEC-30,SEC-31,SEC-32 -->
- **Not covered in 1.0, by design** (product contract §5): roles and key levels are global (no per-server or tenant
  scoping); commands typed inside a terminal session are neither policy-checked nor recorded (the session's opening
  and closing are); changes made on a host, in a cloud console or by another tool outside Whiskers are not governed or
  attributed; a shell on the Whiskers host is outside the contract. The operator view of these limits is in
  [docs/operations/production-checklist.md](docs/operations/production-checklist.md#accepted-limits-10). <!-- claim-free:limit -->
- **The default `docker-compose.yml` profile is privileged** (host PID namespace) so it can manage its own
  host. Use `docker-compose.hardened.yml` when Whiskers only manages remote hosts. <!-- claim:SEC-09 -->
- **Secrets** (`.env`, API keys, tokens, certificates, the encrypted vault, the SQLite DB) live under
  `/app/data` / `.env`, both gitignored and volume-mounted, never baked into the image or committed. <!-- claim:SEC-21 -->

## VAULT_KEY loss, change and rotation

The vault's key also derives the keys for the webhook secrets, the rollback snapshots and the data-protection key ring
(R1-31). There is no in-place rotation of `VAULT_KEY` yet; what a change or loss does: <!-- claim:SEC-22 -->

- **Changed or lost key:** the vault is locked (it is left unchanged until the right key is set again). Stored
  credentials cannot be read; webhooks refuse every delivery until their secret is regenerated with a usable vault;
  rollback snapshots cannot be rolled back to; **everybody is signed out** — the key ring cannot be opened, so a new
  data-protection key is created and, while the vault is locked, kept in memory only (sign-in works, sessions end at
  the next restart, no new key is written in plaintext; key files that were plaintext already stay so until the right
  key is back, and the readiness check counts them). Setting the right key again restores all of it. <!-- claim:SEC-10 -->
- **Upgrade to R1-31:** nobody is signed out. Key files, webhook secrets and snapshots written in plaintext before are
  encrypted in place at the first start with a usable vault, and keep working. <!-- claim:SEC-23 -->
- **No `VAULT_KEY`:** the data-protection keys are written in plaintext as before; webhooks cannot be created or have
  their secret regenerated; updates keep no rollback snapshot. The readiness check names what is still in plaintext. <!-- claim:SEC-24 -->
- **Backups:** a self-backup holds the encrypted values; restoring it needs the same `VAULT_KEY`. With `VAULT_KEY`
  set only an encrypted archive is restored (R1-32): an unencrypted one cannot be checked for changes. <!-- claim:SEC-25 -->

## Self-backup restore

A restore (R1-32) writes only Whiskers' own stores and directories (`SelfBackupLayout`): an archive with anything a
restore may never write — `backups/`, restore state, database journals, temp files, `vault.key` — or with duplicate,
conflicting, non-canonical (`a/../b`) or overlong names, entry types other than file and directory, oversized headers
(checked before the tar reader allocates them), JSON stores or key files, or more than 100,000 entries or
32 GiB of content, is refused; other unknown entries are left out, and a restore that would not fit on the data volume
is refused. Before it is committed the extracted database must pass SQLite's integrity check, hold exactly the
migrations its manifest names and migrate cleanly on a copy (the start's own migration, run first), the stores a service reads at start must read as what it loads, and the
data-protection keys must parse; only then is the pre-restore safety copy taken
and the restore committed. At the next start each store is made equal to the archive (a store the archive lacks is
removed; the safety copy keeps it), the database and the VPN state (`tailscale/`, `netbird/`) are only replaced, the
audit outbox merged, `backups/` never touched,
and `keys/`, `ssh-keys/`, `mtls/` and `vault.json` are left readable by the app's user only. An interrupted swap is
repeated at the next start; so is one that cannot make a key directory or `vault.json` owner-only (a mount that
refuses `chmod` on a path that is too open) — remove `.restore-pending` from the data directory to give the restore up
(the start's log says so). Without `VAULT_KEY` archives are neither encrypted nor authenticated: keep them where only
administrators can reach them. <!-- claim:SEC-11 -->

On PostgreSQL (R1-33) the archive carries a `pg_dump` of the `public` schema instead of a database file. Before the
restore is committed the pre-restore safety backup is taken, then the dump is applied to the live database in a
transaction that is rolled back: the migration histories must be the manifest's, the start's migrations must run on
them and every mapped table and column must be readable, or the archive is refused. The database is locked for that
dry run. The dry run executes the archive's SQL with the role's rights, so a crafted archive can change the database
before it is refused: the guarantee is the safety backup taken first, which holds the state from before. On top of
that, a script that ends the transaction on its own (its own `COMMIT`) is detected by comparing the transaction id
after the script, with a pinned `search_path` and schema-qualified operators, and the archive is refused; the recovery
from the safety backup is described in `docs/operations/backup-and-recovery.md` ("After a restore refused for an early
commit"). A restore that leaves functions, triggers, rules, views, row policies or operators in `public`, or that
created, dropped or changed role or database settings (which could point the app's search path at another schema) or
schemas, relations, functions, triggers, rules, policies or operators in other schemas (a schema named like the role
would put its tables in front of Whiskers'), is refused, as are grants, unlogged or foreign tables in `public`,
default privileges and publications. A restore counts as done only when psql printed a marker, unknown to the archive,
after the checks and the `COMMIT`, so a script that leaves psql reading data cannot end the run as a success. At the
next start the schema is dropped and recreated from the dump in one transaction, before the files are swapped; any
error rolls it back and stops the start. The restore script is piped from `pg_restore` to `psql` and never written to
disk; it runs only when it opens with `\restrict` (client tools 17.6 or newer), so a crafted dump cannot run psql
meta-commands such as `\!`. The connecting role must own the `public` schema (on PostgreSQL 15 and later the database
owner does) and hold `CREATE` on the database. Use a dedicated role that is not a superuser, since a restore runs the
archive's SQL with its rights (a superuser could be made to run `COPY … FROM PROGRAM` on the database host); the
PostgreSQL overlay's `POSTGRES_USER` role is a superuser, and the guide shows how to switch to an owner role without
superuser rights. The password reaches the tools through their environment, and only the error lines of their output
are logged (lines that quote rows are left out). <!-- claim:SEC-12 -->

## Hardening checklist for operators

- Set `AUTH_DISABLED=false` and put a real IdP (Google OAuth or OIDC) in front, or keep the app on a
  trusted private network only. With `AUTH_DISABLED=true` Whiskers itself refuses public client addresses,
  non-IP host names (DNS rebinding) and cross-site origins; narrow `Auth:DisabledAllowedNetworks` further. <!-- claim:SEC-13 -->
- **Local accounts have no MFA in 1.0.** For production, sign in through your identity provider (OIDC) and require MFA
  there; keep local accounts as a break-glass fallback with long, unique passwords, and delete the ones nobody needs.
  A suspected password leak: reset the account under *Settings > Local accounts* (its sessions end at once); with no
  Admin left who can sign in, issue a one-time recovery code on the host with
  `docker compose exec serverwatch dotnet Whiskers.dll --recover-local-account <email>`
  ([docs/operations/local-accounts.md](docs/operations/local-accounts.md)). <!-- claim-free:advice -->
- Complete the setup wizard right after installation. It requires the one-time token from
  `/app/data/setup-token.txt` (or `WHISKERS_SETUP_TOKEN`), so an exposed port alone is not enough to claim Admin. <!-- claim:SEC-14 -->
- Keep the email whitelist tight; review roles under *Settings > Authentication*.
- Issue MCP API keys with the **least** permission level needed.
- Keep the agent's guardrail presets restrictive; only widen autonomy deliberately. <!-- claim-free:advice -->
- Set a strong `VAULT_KEY` (or mount it as a file via `VAULT_KEY_FILE`) and protect the `/app/data` volume (it still holds plaintext SSH and
  mTLS client keys). Without a usable vault Whiskers creates no webhook and keeps no rollback snapshot (an update then
  goes ahead without a way back), and writes the data-protection keys as before. Credentials entered in the UI need the vault;
  credentials set by environment variables (`AICHAT_API_KEY`, `AGENT_API_KEY`, `MATTERMOST_WEBHOOK_URL`, …) are not
  stored by Whiskers — keep them in a secret store or a mode-0600 `.env`, never in the image. <!-- claim:SEC-26 -->
- Keep dependencies current, Dependabot config ships in `.github/dependabot.yml`. <!-- claim:SEC-27 -->
- **Run the least-privileged container profile that fits.** The default `docker-compose.yml` is
  privileged because it can manage its own host (nsenter host-shell). If Whiskers only monitors
  *remote* hosts, deploy `docker-compose.hardened.yml` instead (non-root, no `privileged`, dropped
  capabilities, read-only rootfs, Docker via a verb-restricted socket-proxy). See
  [docs/container-hardening.md](docs/container-hardening.md). <!-- claim:SEC-28 -->
