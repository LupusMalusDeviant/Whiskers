# Screenshots

Canonical product screenshots, mapped to the three pillars of the positioning
([POSITIONING.md](POSITIONING.md)). All images live in [`screenshots/`](screenshots/) and are embedded
in [docs/guide/screenshots.md](../guide/screenshots.md) and on the project website.

Capture conventions: **1440×900**, device-scale 1, **English UI** (the product default), dark theme,
against an isolated demo Docker daemon that runs only demo containers.

| File | Route | Pillar | Shows |
|---|---|---|---|
| `governance-ai-operations.png` | `/ai-operations` | Governed setup | Connect a client, mint a least-privilege key, activate a starter guardrail preset. |
| `governance-guardrails.png` | `/guardrails` | Enforced governance | Code-enforced policy at the tool boundary: read-only kill switch and its fixed exceptions, max autonomous level, writes need confirmation, second-approver switch, protected resources, forbidden argument patterns. |
| `governance-approvals.png` | `/approvals` | Controlled access | A *pending* `update_container` (write) from the `Demo agent` key, held by the *Safe operations* preset, with risk, target and a collapsed panel of redacted parameters; below it the approved `restart_container`. |
| `governance-agent-history.png` | `/agent-history` | Controlled access | Every MCP tool call — actor, level (read/write/admin), verdict (Allow/Confirm/Deny), duration, status — including an admin call denied by policy. |
| `governance-audit-log.png` | `/audit-log` | Complete accountability | Every change with actor and outcome: the held and the denied calls as failed (`Container updated`, `host.exec`, first `Container restarted`), the approved restart as succeeded, plus key creation, guardrail save and login. A held call is logged under its action name with a failed status — it did not run. |
| `dashboard-overview.png` | `/` | Reach | Container overview (total/running/stopped/unhealthy), the "add your first server" card, and the expanded summary of the local Docker host. |
| `topology.png` | `/graph` | Reach | The containers of the local host as nodes; edges appear for shared networks and links (none in the demo). |

## Reproducing

The history on these images is not seeded: it is produced by real MCP calls against a fresh instance,
so every row went through the guardrails exactly as it would in production.

1. **Demo Docker daemon.** Start a throwaway `docker:29-dind` container with its API on
   `tcp://127.0.0.1:23750` and load demo images into it (`nginx:alpine`, `busybox`, `postgres:17-alpine`,
   `alpine`). Create the containers `web`, `cache`, `postgres`, `worker` and `payments-api`, the last one
   with a healthcheck that always fails. Never point a capture instance at a daemon that runs anything
   else.
2. **Fresh instance.** Boot Whiskers with an empty `WHISKERS_DATA_DIR`, `Docker__SocketPath` set to the
   demo daemon, and a seeded local admin (`WHISKERS_ADMIN_EMAIL=admin@acme.example`,
   `WHISKERS_ADMIN_PASSWORD_FILE`, `VAULT_KEY_FILE`, all throwaway).
3. **Browser.** Drive headless Chrome over CDP with the viewport fixed at 1440×900, device-scale 1 and
   `prefers-color-scheme: dark`. Log in as the admin.
4. **Story**, in this order:
   1. Capture `/ai-operations`, then activate the *Safe operations* preset.
   2. Settings → MCP permissions: create the key `Demo agent` (level Admin; the preset decides what runs).
   3. With that key over `/mcp`: `list_containers`, `get_container_logs` and `get_container_metrics` for
      `payments-api` (read, allowed), then `restart_container` (write, held for confirmation).
   4. Approve it under `/approvals`, call `restart_container` again (runs), then
      `get_container_details`.
   5. `execute_command` with `docker system prune -af --volumes` (admin, denied), then
      `update_container` for `web` (write, left pending).
5. Capture `/approvals`, `/agent-history`, `/audit-log`, `/guardrails` (scrolled to the kill switch),
   `/graph`, and `/` with the local host's summary expanded.

## Anonymization

- Only demo actors and resources appear: `Demo agent`, `admin@acme.example`, `local`, and the five demo
  containers. No real hosts, users or secrets.
- The host summary shows the capture machine's kernel and the demo daemon's container ID as hostname;
  neither identifies real infrastructure.
- Re-check every image for real hostnames, IPs, emails or tokens before committing.

## Not yet captured

- **CVE monitor** with findings and the **deploy / exposure plan** — both run host commands (`nsenter`,
  the deployment directory) and need Whiskers on a Linux host, e.g. the container image.
- A pending approval under the **second-approver** rule (the switch itself is on the Guardrails image).
- **Light-theme variants** and a short **demo video** of the full hero loop.
