# Troubleshooting

Start here when Whiskers does not start, does not answer, or shows a host as unreachable. Each entry says what you
see, what it means and what to do. The logs are described in [observability.md](observability.md); with JSON logs on,
one request can be followed to its operation and audit row.

First three looks, in this order:

1. `docker compose logs --tail 200 serverwatch` (Helm: `kubectl logs`) — a start that stops says why in its last lines.
2. `/healthz` and `/readyz` (below).
3. *Settings → Production-ready?* and *Self status* (`/self-status`) once you can sign in.

## /healthz and /readyz

Both are anonymous and answer only a status word.

| Endpoint | Means | `Healthy` | Otherwise |
|---|---|---|---|
| `/healthz` | liveness: the process is up | the web host runs | no answer: the process is not running or still starting (an upgrade's pre-upgrade backup and migration run before it answers) |
| `/readyz` | readiness: Whiskers can do its work | database reachable, server configuration loaded, no restore in progress, audit trail complete, sign-in bindings readable | `Unhealthy`: the database is unreachable, the server configuration is still loading, or a restore is staged and the app is about to restart (maintenance). `Degraded`: Whiskers keeps serving, but an audit outcome waits to be written (see *Audit failure* in [observability.md](observability.md#signals-to-alert-on)) or `identity-bindings.json` cannot be read (Google/OIDC sign-in refused, local sign-in works) |

## Whiskers does not start

| Symptom in the log | Meaning | Do |
|---|---|---|
| `VAULT KEY REJECTED` (the start goes on, but stored credentials, webhooks, rollback snapshots and sessions do not work) | The key given is not the one the vault was sealed with. The vault stays locked and `vault.json` unchanged. | Set the right key (`VAULT_KEY_FILE` or `VAULT_KEY`) and restart. A lost key cannot be recovered; see SECURITY.md, "VAULT_KEY loss, change and rotation". |
| the vault key file cannot be read (hardened profile, `userns-remap`) | The container's user (10001 in the hardened profile) cannot read the key file. Whiskers never starts without the key it was given. | `chown 10001 secrets/vault.key` (or the remapped id), keep mode 0600. |
| a pre-upgrade backup failed | The upgrade stopped before changing anything. | Fix the cause (disk space; on PostgreSQL the client tools — `WHISKERS_PG_BIN_DIR`), start again. To skip the backup deliberately: `WHISKERS_UPGRADE_SKIP_BACKUP=true` with a recovery point of your own. |
| a migration failed, naming a pre-upgrade backup | The database may be partly migrated. | [upgrade-and-rollback.md](upgrade-and-rollback.md#rollback). |
| a JSON store cannot be read and has no readable previous generation (`JsonStoreCorruptException`) | A store (roles, whitelist, vault, servers, permissions …) is damaged and Whiskers refuses to start with an empty one. | Restore the file from a self-backup, or from its `<file>.bak` after checking it; the unreadable copy is kept as `*.corrupt-*`. |
| a staged restore cannot be applied | The restore committed before the restart is retried at every start. | Read the reason. To give the restore up, remove `.restore-pending` from the data directory. |
| a PostgreSQL restore at start failed | The transaction was rolled back; the start stops. | See [backup-and-recovery.md](backup-and-recovery.md#postgresql-notes). On Kubernetes, raise `probes.startup.failureThreshold` so the restore is not killed half-way. |

## Signing in

- **"Account locked"** after five wrong passwords: wait 15 minutes, or have an Admin reset the account. No Admin
  left who can sign in: `docker compose exec serverwatch dotnet Whiskers.dll --recover-local-account <email>` prints
  a one-time recovery code ([local-accounts.md](local-accounts.md)).
- **Google/OIDC sign-in refused** with a pending request: an Admin confirms the binding under *Settings → Sign-in
  identities*, or on the host with `--confirm-identity-binding <email>` ([identity-bindings.md](identity-bindings.md)).
- **Everyone is signed out after a restart:** the data-protection key ring could not be opened, usually because the
  vault is locked by a wrong key. Set the right `VAULT_KEY`.
- **Behind a reverse proxy, HTTPS is seen as HTTP, the login limit hits everyone at once, or the audit shows the
  proxy's address:** the proxy is not trusted. The readiness card names the ignored peer; add its address (as narrow as
  possible, `/32` or `/128`) to `ForwardedHeaders__TrustedNetworks__0`. A public address there is more likely a client
  trying to spoof one — do not add it.
- **`AUTH_DISABLED=true` and requests are refused:** that mode answers only private client addresses, an IP or
  `localhost` as host name and no foreign `Origin`. Use a real sign-in instead of widening it.

## A managed host is unreachable

| Shows | Meaning | Do |
|---|---|---|
| every SSH-connected server "Unreachable" at once | The SSH tunnels died (network change, Whiskers host suspended). The connection manager rebuilds dead tunnels. | Wait one check interval. If it stays, check the Whiskers host's network and the mesh (`tailscale status`). |
| one SSH server fails with a host-key error | Its host key changed since Whiskers pinned it (ADR-0002). After an **intended** rebuild this is expected; otherwise it may be a man in the middle. | After an intended rebuild: remove the host's line from `<data>/ssh-keys/known_hosts`, then reconnect. Otherwise investigate first. |
| readiness: "mTLS-Proxy (ghostunnel) läuft" not OK for a host | ghostunnel is not running there, or its arguments cannot be read. The mTLS channel is the only management path. | Break-glass re-onboarding over SSH ([per-host-mtls-migration.md](per-host-mtls-migration.md)). |
| readiness: a host still admits the shared client identity | A stolen shared key would open it. | *Migrate* the host, or narrow `--allow-cn` by hand for hosts with your own certificates ([per-host-mtls-migration.md](per-host-mtls-migration.md)). |
| *Self status*: a loop stalled, a circuit open (`whiskers_self_circuit_open`) | The host failed repeatedly and Whiskers backs off, or the server's checks are paused. | Open *Self status*: a skipped server says why. A paused server resumes under *Servers* ([observability.md](observability.md#signals-to-alert-on)). |
| Docker calls are slow everywhere (`whiskers_self_call_wait_seconds_max` rising) | Calls wait for a budget slot, usually behind one slow host. | Find the slow host on *Self status*; pause its checks while you fix it. |

## Actions are refused

- **"read-only" / kill switch:** the guardrails' read-only kill switch is on; every mutation is refused and no
  terminal opens. An Admin switches it off on the *Guardrails* page (audited).
- **Everything is refused and the guardrails page cannot save:** `guardrails.json` is unreadable or invalid, so the
  policy is fail-closed (kill switch, everything protected). Repair the file on the host (or remove it together with
  `guardrails.json.bak`) and restart.
- **"protected" / needs an Admin approval:** the target is a protected resource or Whiskers' own control plane.
  Another eligible person decides it under *Approvals*.
- **An audit intent could not be written; operation refused:** the database cannot take the audit row. Check disk
  space and the database connection; Whiskers refuses every mutation until it can record it.
- **A webhook refuses every delivery:** its secret cannot be read (vault locked or key changed). Regenerate the secret
  with a usable vault.

## Backups

- **Self-backup fails on PostgreSQL:** `pg_dump` is missing or older than the server. The readiness line names the
  version; set `WHISKERS_PG_BIN_DIR` to client tools 17.6 or newer and at least as new as the server.
- **No self-backup can be created** while the vault rejects the key (no archive is sealed with a mistyped key).
- **A restore is refused:** the reason names the check (archive integrity, migration history, a store that does not
  read, too little room on the data volume). Nothing has changed at that point.

## Reporting a problem

Collect the start's log lines, the `/readyz` answer, the readiness card and, for one action, its operation id (shown
with the result and in the audit row). Never attach the data directory, `vault.key` or a self-backup to an issue.
Security problems go through SECURITY.md, not a public issue.
