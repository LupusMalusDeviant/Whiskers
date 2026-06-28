# Container hardening

ServerWatch ships in two deployment profiles. Which one is appropriate depends on **what the
container is allowed to do to the host it runs on** — not on a generic "more secure" toggle.

| | `docker-compose.yml` (full) | `docker-compose.hardened.yml` (locked-down) |
|---|---|---|
| Runs as | root | non-root (uid 10001) |
| `privileged` | yes | no |
| `pid: host` | yes | no |
| Linux caps | all (privileged) | all dropped + `no-new-privileges` |
| Root filesystem | writable | read-only (+ tmpfs, `/app/data` volume) |
| Docker access | raw `/var/run/docker.sock` | restricted socket-proxy (TCP, verb whitelist) |
| VPN | in-container daemon | on the host / a sidecar (`Vpn__Provider=none`) |
| Local host-shell (nsenter) | ✅ firewall/nginx/systemd/`execute_command` | ❌ disabled |
| Remote hosts (mesh + mTLS) | ✅ | ✅ |

## Why "full" needs the privileges it asks for

ServerWatch can manage **the host it runs on** through the Linux "host-shell" plane: it runs
`nsenter -t 1 -m -u -i -n -p -- …` to enter PID 1's namespaces and execute on the host. That is
what powers firewall rules, Nginx config, systemd control and `execute_command` for the *local*
server. Entering another process's namespaces requires `privileged` (or at least `SYS_ADMIN` +
`SYS_PTRACE`) **and** `pid: host`. The in-container VPN daemon additionally wants `NET_ADMIN` +
`/dev/net/tun`.

In short: **the Local connection mode is privileged by nature.** No base-image change (distroless,
chiseled, non-root) removes that — it is inherent to managing your own host from inside a container.
The deployment that manages the box it runs on (e.g. the maintainer's own primary node) stays on the
full profile by design.

## What the hardened profile changes — and what it costs

The locked-down profile is for the common case where ServerWatch is a **monitoring/control plane for
remote hosts** and is not expected to reach into its own host's shell.

- **Non-root (uid 10001).** The image already contains this user (`Dockerfile`); the hardened compose
  selects it with `user: "10001:10001"`. A fresh named `serverwatch-data` volume inherits the right
  ownership from the image. **Bind-mounts do not** — if you bind-mount `/app/data`, `chown -R 10001:10001`
  it on the host first.
- **No `privileged`, no `pid: host`, `cap_drop: ALL`, `no-new-privileges`.** Removes the host-shell
  (nsenter) capability. Local firewall/nginx/systemd/`execute_command` are unavailable; manage the
  local host another way. The Docker and remote-host features are unaffected.
- **Read-only root filesystem.** Only `/app/data` (volume) and a couple of tmpfs mounts are writable.
  If a future feature needs another writable path, add a tmpfs or relax `read_only`.
- **Socket-proxy instead of the raw socket.** See below.
- **VPN on the host.** Set `Vpn__Provider=none` (the default) and run Tailscale/NetBird on the host or
  in a dedicated sidecar. The container shares host networking to reach the mesh. Keyless
  Tailscale-SSH for the web terminal still works because the host provides the connectivity. See
  [`../src/ServerWatch/Services/Vpn/README.md`](../src/ServerWatch/Services/Vpn/README.md).

## The Docker socket-proxy

Mounting `/var/run/docker.sock` into a container is root-equivalent on the host: anyone who can talk
to it can start a privileged container and own the box. The hardened profile never gives the app the
raw socket. Instead a [`tecnativa/docker-socket-proxy`](https://github.com/Tecnativa/docker-socket-proxy)
sidecar holds the socket (read-only) and exposes a **verb-whitelisted** HTTP API; the app talks to it
over loopback TCP.

The whitelist keeps `exec`, `swarm`, `secrets` and `configs` **off** while allowing the
container/image/network/volume operations ServerWatch needs. Point the built-in **local** server at
the proxy (Settings → Servers, or `servers.json`): set its `SocketPath` to `http://127.0.0.1:2375`.

> **Residual risk, stated honestly:** container *create* must stay enabled so deploys work, and the
> proxy does not filter the `privileged` flag on creation — so a fully compromised app could still
> create a privileged container. The proxy raises the bar (no `exec` into existing containers, no
> swarm/secret access) but is not a complete sandbox. For ServerWatch's own fleet this is mitigated
> further by the mTLS + mesh design — see [ARCHITECTURE.md](ARCHITECTURE.md).

This is the same socket-proxy + ghostunnel/mTLS pattern ServerWatch already uses to reach **remote**
Docker daemons without SSH; the hardened profile applies it to the **local** daemon too.

## Roadmap: towards a distroless image

The hardened profile is the stepping stone to a minimal (chiseled/distroless) image. Remaining
blockers and the order to remove them:

1. **Now:** non-root + dropped privileges + socket-proxy (this document).
2. Replace the `ssh`/`sshpass` shell-outs (already behind `ISshTunnelManager` / `IHostCommandExecutor`)
   with a managed SSH library, and keep the VPN out of the image (`Vpn__Provider=none`).
3. Drop the shell entrypoint and the `docker compose` CLI dependency (compose deploys via the API).
4. Switch the runtime base to `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` — non-root, no
   shell, no package manager. Docker access is already library-based (`Docker.DotNet`), so no CLI is
   needed for the core.

The end state is **two images**: a "full" one for self-host-managing nodes and a chiseled "remote"
one for the locked-down profile.
