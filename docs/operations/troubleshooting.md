# Troubleshooting

Start here when Whiskers does not start, does not answer, or shows a host as unreachable. Each entry says what you
see, what it means and what to do. The logs are described in [observability.md](observability.md); with JSON logs on,
one request can be followed to its operation and audit row.

First three looks, in this order:

1. The container log — `docker logs --tail 200 whiskers` for an installer install (container `whiskers`), `docker compose logs --tail 200 serverwatch` with the repository's compose file (service `serverwatch`), `kubectl logs` on Helm. A start that stops says why in its last lines.
2. `/healthz` and `/readyz` (below).
3. *Settings → Production-ready?* and the *Whiskers itself* page (`/self-status`) once you can sign in.

## /healthz and /readyz

Both are anonymous and answer only a status word.

| Endpoint | Means | `Healthy` | Otherwise |
|---|---|---|---|
| `/healthz` | liveness: the process is up | the web host runs | no answer: the process is not running or still starting (an upgrade's pre-upgrade backup and migration run before it answers) |
| `/readyz` | readiness: Whiskers can do its work | database reachable, server configuration loaded, no restore in progress, audit trail complete, sign-in bindings readable | `Unhealthy`: the database is unreachable, the server configuration is still loading, or a restore is staged and the app is about to restart (maintenance). `Degraded`: Whiskers keeps serving, but an audit outcome waits to be written (see *Audit failure* in [observability.md](observability.md#signals-to-alert-on)) or `identity-bindings.json` cannot be read (Google/OIDC sign-in refused, local sign-in works) |

## Whiskers does not start

| Symptom in the log | Meaning | Do |
|---|---|---|
| `VAULT KEY REJECTED` (the start goes on, but stored credentials cannot be read, webhooks refuse deliveries, rollback snapshots cannot be used, and sign-ins hold only until the next restart: the new session key is kept in memory) | The key given is not the one the vault was sealed with. The vault stays locked and `vault.json` unchanged. | Set the right key (`VAULT_KEY_FILE` or `VAULT_KEY`) and restart. A lost key cannot be recovered; see SECURITY.md, "VAULT_KEY loss, change and rotation". |
| the vault key file cannot be read (hardened profile, `userns-remap`) | The container's user (10001 in the hardened profile) cannot read the key file. Whiskers never starts without the key it was given. | `chown 10001 secrets/vault.key` (or the remapped id), keep mode 0600. |
| a pre-upgrade backup failed | The upgrade stopped before changing anything. | Fix the cause (disk space; on PostgreSQL the client tools — `WHISKERS_PG_BIN_DIR`), start again. To skip the backup deliberately: `WHISKERS_UPGRADE_SKIP_BACKUP=true` with a recovery point of your own. |
| a migration failed, naming a pre-upgrade backup | The database may be partly migrated. | [upgrade-and-rollback.md](upgrade-and-rollback.md#rollback). |
| a JSON store cannot be read and has no readable previous generation (`JsonStoreCorruptException`) | A store (roles, whitelist, vault, servers, permissions …) is damaged and Whiskers refuses to start with an empty one. | Neither the file nor its previous generation `<file>.bak` can be read (a readable `.bak` is used automatically and the readiness card names it). Restore the file from a self-backup; the unreadable copy is kept as `*.corrupt-*`. `roles.json`, `whitelist.json` and `mcp-permissions.json` grant access and are never replaced by their `.bak` automatically, because it may still hold an API key, role or address removed since: compare the `.bak` with what was revoked after it was written, then copy it into place by hand, or restore a self-backup. |
| a staged restore cannot be applied | The restore committed before the restart is retried at every start. | Read the reason. To give the restore up, remove `.restore-pending` from the data directory. |
| a PostgreSQL restore at start failed | The transaction was rolled back; the start stops. | See [backup-and-recovery.md](backup-and-recovery.md#postgresql-notes). On Kubernetes, raise `probes.startup.failureThreshold` so the restore is not killed half-way. |

## Signing in

- **The right password is refused with "Invalid email or password":** after five wrong passwords the account is
  locked for 15 minutes, and the sign-in page deliberately answers a locked account exactly like a wrong password. The
  audit log records the refusal with the reason (locked out). Wait, or have an Admin reset the account. No Admin left
  who can sign in: `docker exec -it whiskers dotnet Whiskers.dll --recover-local-account <email>` (installer install;
  with the repository's compose file `docker compose exec serverwatch …`) prints a one-time recovery code
  ([local-accounts.md](local-accounts.md)).
- **Google/OIDC sign-in refused** with a pending request: an Admin confirms the binding under *Settings → Sign-in
  identities*, or on the host with `--confirm-identity-binding <email>` ([identity-bindings.md](identity-bindings.md)).
- **Everyone is signed out after every restart:** the data-protection key ring could not be opened, usually because
  the vault is locked by a wrong key; sign-in works, but the new session key lives in memory only. Set the right
  `VAULT_KEY`.
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
| *Whiskers itself* (`/self-status`): a loop stalled, a circuit open (`whiskers_self_circuit_open`) | The host failed repeatedly and Whiskers backs off, or the server's checks are paused. | Open *Whiskers itself*: a skipped server says why. A paused server resumes under *Servers* ([observability.md](observability.md#signals-to-alert-on)). |
| Docker calls are slow everywhere (`whiskers_self_call_wait_seconds_max` rising) | Calls wait for a budget slot, usually behind one slow host. | Find the slow host on *Whiskers itself*; pause its checks while you fix it. |

## Actions are refused

- **"read-only" / kill switch:** the guardrails' read-only kill switch is on; every mutation is refused and no
  terminal opens. An Admin switches it off on the *Guardrails* page (audited). The exceptions are listed there
  (contract 0009): switching it off, the first-run setup and the host CLIs always; the own password change, the
  recovery page and binding a Google/OIDC identity at a first sign-in unless an Admin blocked them beforehand. A
  blocked one says "Blocked while the kill switch is on". With the guardrails fail-closed (next item) these three are
  blocked too; `--recover-local-account` then refuses without `--force`, because the code could not be redeemed.
- **Everything is refused and the guardrails page cannot save:** `guardrails.json` is unreadable or invalid, or it and
  `guardrails.json.bak` are missing although this instance had a guardrail policy (the database records that, contract
  0010), so the policy is fail-closed (kill switch, everything protected, own password changes, recovery codes and
  sign-in bindings blocked). The Guardrails page and *Settings > Production ready?* name the reason. The way back is on
  the host, not the recovery CLI: put a valid `guardrails.json` into the data directory (repair it, or restore it from a
  self-backup) and restart; or, with both files missing, run
  `docker compose exec whiskers dotnet Whiskers.dll --reset-guardrails`, restart, and let an Admin switch the kill switch
  off on the Guardrails page (the reset writes the safe default with the kill switch on). Deleting both files is no
  reset: it stays fail-closed.
- **"protected" / needs an Admin approval:** the target is a protected resource or Whiskers' own control plane.
  An Admin decides it under *Approvals* (whether the requester may approve their own request follows the approval rules, PC-D4).
- **An audit intent could not be written; operation refused:** the database cannot take the audit row. Check disk
  space and the database connection; Whiskers refuses every mutation until it can record it.
- **A webhook refuses every delivery:** its secret cannot be read (vault locked or key changed). Regenerate the secret
  with a usable vault.

## Backups

- **Self-backup fails on PostgreSQL:** `pg_dump` is missing or older than the server. The readiness line names the
  version. Set `WHISKERS_PG_BIN_DIR` to client tools that can restore what they dump — 17.6 or newer (or 16.10,
  15.14, 14.19 and later on those lines) — and at least as new as the server.
- **No self-backup can be created** while the vault rejects the key (no archive is sealed with a mistyped key).
- **A restore is refused:** the reason names the check (archive integrity, migration history, a store that does not
  read, too little room on the data volume). Nothing has changed at that point.

## Reporting a problem

Collect the start's log lines, the `/readyz` answer, the readiness card and, for one action, its operation id (shown
with the result and in the audit row). Never attach the data directory, `vault.key` or a self-backup to an issue.
Security problems go through SECURITY.md, not a public issue.
