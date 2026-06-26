# Architecture — Mesh + mTLS, SSH-key-free operation

ServerWatch manages a fleet of Docker hosts. This document describes how it does so
**without storing a standing SSH key** — the design that replaces "one private key on the
controller unlocks every server" with short, auditable, certificate-gated paths over a private
mesh.

> All addresses below are placeholders (`100.x.y.z` = a Tailscale/CGNAT mesh IP, `host-a` = a
> server id). Nothing here is environment-specific.

## The problem

A monitoring/control app that reaches into many servers typically holds an SSH private key per
host on the controller. If the controller is compromised, **every** server falls. The key is also
"god mode": an arbitrary-shell credential. That single stored key is the largest attack surface.

## The three planes

ServerWatch splits what it does into three independent planes, each moved off SSH:

| Plane | What | Transport |
|---|---|---|
| **Telemetry** | host CPU/RAM/disk, container stats | `node_exporter` → a Prometheus-compatible TSDB (VictoriaMetrics), pull/scrape over the mesh |
| **Docker control** | list / restart / deploy / inspect containers, images, networks | the Docker Engine API over **mTLS**, fronted by a verb-whitelisting proxy |
| **Shell** | `execute_command`, `systemctl`, `journalctl`, nginx/firewall edits | a one-shot privileged `nsenter` container launched over the **same mTLS Docker channel** |

None of the three uses SSH at steady state.

## Foundation: a private mesh

All hosts join a WireGuard-based mesh (Tailscale). Every management port below is bound **only** to
the host's mesh IP — never a public interface. The controller reaches hosts over the mesh; nothing
management-related is exposed to the internet.

## Telemetry plane (pull, no credential inbound)

Each host runs `node_exporter` bound to `<mesh-ip>:9100`. A central VictoriaMetrics instance scrapes
all hosts over the mesh. ServerWatch reads host metrics from the TSDB instead of `exec`-ing into the
host's `/proc` over SSH. Each scrape target carries a stable `server` label (the ServerWatch server
id) that queries filter on.

```
node_exporter (host-a, host-b, …)  ──scrape──▶  VictoriaMetrics  ──query──▶  ServerWatch
        (mesh-only :9100)                         (mesh-only :8428)
```

## Docker control plane (mTLS + verb whitelist)

On each host:

```
ServerWatch ──mTLS──▶ ghostunnel ──plaintext(local)──▶ docker-socket-proxy ──▶ /var/run/docker.sock
   (client cert)      (server cert, mesh-only :2376)     (HAProxy verb allow-list)
```

- **ghostunnel** terminates mutual TLS: it requires a client certificate whose CN is the controller,
  signed by the fleet CA. Bound to the mesh IP only.
- **docker-socket-proxy** allows only the API sections ServerWatch needs
  (`CONTAINERS`, `IMAGES`, `NETWORKS`, `VOLUMES`, `INFO`, `VERSION`, `POST`) and **denies the rest**
  (`EXEC=0`, `SWARM=0`, `SECRETS=0`, `CONFIGS=0`, …). Container **exec-start** (`/exec/*`) is blocked,
  so even though exec-create slips through the broad `/containers` rule, the exec can never run.
- **Residual, by design:** `POST /containers/create` stays allowed (deploy needs it) and is
  root-equivalent (a container can bind-mount `/`). This is the one acknowledged powerful verb.

The server config carries `ConnectionType=TCP`, `TcpUseTls=true`, and paths to the client cert/key +
CA bundle. The .NET Docker client presents the leaf certificate and validates the server chain
against the CA (custom root trust; server-presented intermediates are added to the chain builder).

## Shell plane (SSH-free, over the same mTLS channel)

Running an arbitrary host command without SSH: ServerWatch creates a **one-shot privileged
container** via the mTLS Docker API that enters the host namespaces and runs the command —

```
docker run --rm --privileged --pid=host alpine \
    nsenter -t 1 -m -u -i -n -p -- sh -c '<command>'
```

It then reads the container's logs (the command output) and removes it. This is the exact effect of
`nsenter -t 1` used for local host access, but remote and over mTLS. No SSH key is involved, so the
standing key can be deleted entirely.

Trade-off: the mTLS channel becomes the single management path (recovery is via the cloud provider's
console). Keeping SSH as an independent break-glass — using short-lived SSH certificates from the
same CA instead of a standing key — is a valid alternative; this design favours "no SSH at all".

## PKI

A small online CA (step-ca) issues:
- one **client** certificate for ServerWatch (CN = the controller),
- one **server** certificate per host (SAN = the host's mesh IP) for its ghostunnel,
all chaining to a shared root + intermediate. Certs are leaf+intermediate bundles; the CA bundle
(root+intermediate) is distributed to each ghostunnel so a leaf-only client still verifies. The same
CA can later issue short-lived SSH certificates if an independent SSH break-glass is wanted.

## Onboarding a new server

Adding a server starts from an SSH **bootstrap** connection (the only time SSH is used). The
onboarding orchestrator then runs, end to end, with live progress in the UI:

1. install Tailscale, bring it up, and **surface the interactive login link in the app** (the user
   clicks it to authorise the node), then wait until the node joins and gets a mesh IP;
2. deploy `node_exporter` (mesh-only);
3. issue a per-host server certificate from the CA;
4. deploy `docker-socket-proxy` + `ghostunnel` (mTLS);
5. add the host to the scrape config and reload the TSDB;
6. switch the server to `TCP+mTLS` + Prometheus metrics and verify over mTLS.

After that the bootstrap SSH key is no longer needed and can be removed — the server is fully
mesh + mTLS, SSH-free.

## What's where

- `deploy/telemetry/` — compose files for `node_exporter` (per host) and VictoriaMetrics + a scrape
  config template, plus a sample Tailscale ACL. All mesh-bound; bind addresses are templated.
- Certificates and per-host runtime config live **on the hosts** and in the controller's gitignored
  `data/` directory — never in this repository.
