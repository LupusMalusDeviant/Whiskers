# Per-host mTLS client credentials (R1-44)

Before R1-44 Whiskers connected to every managed host with one client certificate, `serverwatch-client`
(`{MtlsDir}/client.crt` / `client.key`), and every host's ghostunnel admitted that one name. A stolen key opened the whole
fleet. Since R1-44 every host gets its own client identity `whiskers-client-{slug}-g{n}` and its ghostunnel admits only
that name. New hosts get it at onboarding. Existing hosts are migrated one at a time with the steps below.

The guarantee holds **per host after its migration**. A host that still admits the shared name is exposed as before.

## What changes where

| Where | Before | After |
|---|---|---|
| Whiskers' data directory | `{MtlsDir}/client.{crt,key}`, `{MtlsDir}/ca.crt` | additionally `{MtlsDir}/hosts/{serverId}/g{n}-s{k}/client.{crt,key}` (key 0600, directories 0700), `identity` and `rollout.json` per host; `ca.crt` is unchanged and shared |
| Server record | `TcpClientCertPath` / `TcpClientKeyPath` → the shared files | → the host's active slot |
| The host's `/opt/dockerproxy/docker-compose.yml` | `- --allow-cn=serverwatch-client` | `- --allow-cn=whiskers-client-{slug}-g{n}` |

The steps change only the `--allow-cn` items of ghostunnel's `command:`. They do not touch the image, ports, certificates,
the listen or target flags, or the socket proxy.

## Before you start

- Have a way back to every host that does not go through Whiskers: the cloud provider's console, or SSH for the
  break-glass re-onboarding. The mTLS channel is the only management path. If a host's ghostunnel stops, Whiskers cannot
  reach that host.
- Run a self-backup. The backup holds the new keys.
- Look at the readiness checklist (Settings → *Produktionsreif?*). It has one line per mTLS host, a line for hosts whose
  ghostunnel is not running or cannot be read, and a line for narrowed hosts whose running `--allow-cn` list is not
  exactly their own name.

## Order

1. **Bifröst** first.
2. Then **the whiskers.site host (hetzner-apps)**.
3. Then the rest of the fleet, **one host at a time**. Wait for each host to show *Eigene Identität … der Host nimmt nur
   sie an* before you start the next.

Do not run a migration on the host that runs Whiskers itself while you have no other way to reach it.

## Migrating one host

On the server page, an Admin clicks the key icon (*Migrieren*) on the host's row. The same step is available over MCP
as `migrate_mtls_client_credential`, Admin only. Both run under the protected control plane, so each call needs an
approval.

The migration runs five phases and reports each one:

1. **Issue.** Whiskers makes a key in its own process. step-ca signs only the CSR. The certificate must chain to
   `ca.crt`, carry the expected CN and the clientAuth usage, and have at most one DNS SAN, which must equal the CN.
2. **Widen.** The host admits the shared name and the new one.
3. **Switch.** The server record moves to the new slot in one save. The cached Docker client is rebuilt and the
   connection is checked. If the check fails, the record is put back. The host still admits both names, so nothing
   breaks.
4. **Narrow.** The host admits only the new name.
5. **Record.** The journal `rollout.json` stores phase 5.

Each host step (2 and 4) works like this:

- The step keeps the compose file as `.prev`.
- It arms a dead-man timer `whiskers-allow-guard-{run}` (3 minutes).
- It swaps the file and recreates only ghostunnel with
  `docker compose -p <project> --project-directory <dir> -f <file> up -d --pull never --no-deps --force-recreate ghostunnel`,
  detached through `systemd-run`.
- Whiskers then reads `docker inspect`. The running list must be exactly the new list.
- Whiskers probes the new credential, which must be admitted.
- For a narrowing, Whiskers also probes the shared credential. It must be refused with a **TLS alert**. A timeout or a
  reset does not count. It means "not checked", and the step is not confirmed.
- Only then does Whiskers send the confirmation, through a one-off connection that uses the new credential. That
  connection runs one script under `timeout 20` while holding the run's lock. The script requires the run's guard timer
  to be loaded and still waiting, stops the timer and writes the run's marker.
- Whiskers reads the host and runs the checks again before it records the phase. The marker is removed after the phase
  is recorded.

If anything fails, nothing is recorded. The guard waits for the lock without a time limit and restores the previous file
unless its own run's marker is present. It then recreates ghostunnel, checks that ghostunnel runs with the restored list,
recreates once more if it does not, and writes the result to the system journal:

```sh
journalctl -t whiskers-allow-guard
```

Start the step again after the guard has fired. A new call resumes from what ghostunnel actually runs with (`docker
inspect`), not from the last recorded phase. A done phase is checked again rather than repeated.

## Why the preflight refuses a host

The step refuses to start, and changes nothing, when:

- ghostunnel runs with arguments other than the approved ones, apart from `--allow-cn`;
- the running allow list names a client that is neither the shared one nor this host's own;
- ghostunnel is not managed by exactly one compose file (label `com.docker.compose.project.config_files`), or ghostunnel
  and socket-proxy are not both services of that one project;
- the compose file and the running containers differ:
  - image: when the file pins a digest (`image: …@sha256:…`), that digest must be among the running image's repo
    digests. When the file has **no digest** (older hand-made hosts), the image reference string in the file must equal
    the one the container was created from;
  - the environment the file declares must be set in the container;
  - port bindings and mounts must be equal;
  - ghostunnel's command must equal its running arguments, apart from `--allow-cn`;
- the file mentions `--allow-cn` anywhere other than in ghostunnel's command items;
- a guard of another run is active on the host (`whiskers-*-guard*`). Wait at most 3 minutes and try again.

To fix a hand-made host, bring its compose file in line with what runs (or the other way round, with
`docker compose up -d`) and start again.

## Renewing and rotating

- **Renew** (`renew_mtls_client_credential`, `rotate=false`) issues a new key and certificate under the same name in a
  new slot and switches the record. The host is not touched. The old slot is deleted once the new connection is
  confirmed.
- **Rotate** (`rotate=true`) moves to the next generation `-g{n+1}`: issue, widen, switch, narrow, record. Narrowing
  requires the previous generation to be refused with a TLS alert. The previous slot is deleted only after that.

Neither one overwrites the live key. A slot that any server record names is never deleted.

## Retiring the shared credential

Once every host is narrowed, an Admin clicks *Stilllegen* on the server page. This is UI only; there is no MCP tool.
Whiskers deletes `{MtlsDir}/client.crt` and `client.key`. `ca.crt` stays.

Retiring is refused while any considered host does one of these:

- still presents the shared certificate;
- is not recorded as narrowed;
- presents a name other than the one its journal records;
- runs ghostunnel with a different live `--allow-cn` list, or a stopped ghostunnel;
- cannot be read.

Considered hosts are those on the shared certificate and those with a managed identity. Hosts with operator-supplied
credentials are listed but do not block.

## Removing a server

Removing a server deletes its directory under `{MtlsDir}/hosts/`. For a host that was **never migrated**, its ghostunnel
still admits the shared name. Narrow it, or stop and remove its ghostunnel, before you remove the server. Otherwise the
shared key still opens it.

## Undo

- **During a step**, do nothing. The guard restores the previous file within 3 minutes.
- **After phase 2 (widened)**, the host admits both names. Whiskers still uses the shared credential, so nothing needs to
  be undone.
- **After phase 3 (switched)**: in the server dialog, set the client certificate and key back to `{MtlsDir}/client.crt` /
  `client.key`. The host still admits the shared name.
- **After phase 4 (narrowed)**, the host refuses the shared name. To go back, put
  `- --allow-cn=serverwatch-client` into the host's compose file on the host (provider console or SSH) and run
  `docker compose -p dockerproxy --project-directory /opt/dockerproxy -f /opt/dockerproxy/docker-compose.yml up -d --pull never --no-deps --force-recreate ghostunnel`.
  Then point the record back as above.
- **An older backup ("Älteres Backup").** Self-backups taken before a host's migration still contain the shared key
  `{MtlsDir}/client.key`. Keep them as safely as the key itself, or delete them after retiring.
  - **What a restore does:** restoring such a backup, or one taken before a rotation, brings back server records that
    name the shared credential or an older generation. The host admits only its newer name, so Whiskers can no longer
    reach it. Rotating again does not help, because a rotation needs a working connection.
  - **What readiness shows:** a restore brings back the whole `{MtlsDir}` (rollout journals included) together with the
    records, so the restored record and journal agree with each other. Only the live read tells the host apart: its
    ghostunnel arguments cannot be read. The line *mTLS-Proxy (ghostunnel) läuft* names it with *(Argumente nicht
    lesbar)*, and the host's own *mTLS-Client-Identität* line points to the break-glass re-onboarding, not to
    „Migrieren“, which cannot reach it either.
  - **The way back** is the break-glass re-onboarding over SSH:
    1. In the server dialog, set the connection back to SSH and give it a bootstrap key or root password.
    2. Run *Speichern & Onboarden*. The run writes the proxy's compose file again with the client name Whiskers holds
       for the host, and switches the record back to TCP+mTLS.
    3. Alternatively, on the host (provider console or SSH), put the restored record's name back into `--allow-cn` and
       recreate ghostunnel as under phase 4 above.
- **A wrong or lost `VAULT_KEY` (contract 0015 D10).** Client keys under `{MtlsDir}` are sealed with the vault key
  (`f1:` content in the same file). With a wrong key Whiskers cannot open them: every mTLS host is unreachable, the
  readiness check names the vault key, and nothing falls back to a plain key (fail-closed). Set the right key. If it is
  lost, the break-glass re-onboarding above (step 1 and 2) issues a new client credential per host; it needs only a
  transient SSH password or bootstrap key. Rotating the key is described in
  [vault-key-rotation.md](vault-key-rotation.md).

## Verifying a host

- **Readiness.** The host's line reads *Eigene Identität whiskers-client-…-g{n} … der Host nimmt nur sie an*, and the
  `--allow-cn` line is green.
- **On the host.** Run this; it must list only the host's own name:

  ```sh
  docker inspect ghostunnel --format '{{range .Args}}{{println .}}{{end}}' | grep allow-cn
  ```

- **Negative check.** From a machine in the mesh, try the shared credential. The handshake must be refused:

  ```sh
  curl --cacert ca.crt --cert client.crt --key client.key https://<mesh-ip>:2376/_ping
  ```
