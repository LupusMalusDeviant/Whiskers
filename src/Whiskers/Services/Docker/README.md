# Services/Docker

Everything that talks to the Docker Engine. This folder owns the **connection layer** (how Whiskers reaches each host's Docker daemon, local socket, SSH tunnel, or TCP/mTLS) and the **operations layer** (containers, images, networks, stats).

Connections are established per configured server and cached; for SSH-tunnelled hosts the manager self-heals dead tunnels. For TCP/mTLS hosts there is no SSH at all (see [docs/ARCHITECTURE.md](../../../docs/ARCHITECTURE.md)).

## Files

| File | Purpose |
|---|---|
| `IDockerService.cs` / `DockerService.cs` | The main Docker operations surface: list/inspect/start/stop/restart/remove containers, images, networks, stats, logs. `DockerService` is a thin **facade** — every member delegates to an internal collaborator class in [`Operations/`](Operations/) (containers, lifecycle/rollback, images, networks, host shell, system info), which the facade constructs itself (no extra DI registrations). Also runs **host shell commands SSH-free** via a one-shot privileged `nsenter` container over the mTLS Docker channel. Each such helper is labelled `serverwatch.hostshell=1` and removed in a `finally`; if a run is interrupted before that (app rebuilt mid-command, mTLS drop) the next call sweeps any leftover labelled, non-running helpers older than 30 s, so they never pile up. |
| `ContainerVerifiers.cs` | R1-36 (INV-43): the gateway's read-backs of the container capabilities — start/restart/update/create/rollback must leave the container running (not OOM-killed; a start or restart of a one-shot container that exited 0 also counts), stop must leave it exited/created/dead, remove must leave it gone. Update and create read the container the call returned (an update through a tool: the container of the same name); `ContainerCreateVerifier` also serves `app.deploy` (the Deploy page returns its container id; a deployment that names none, such as the `deploy_app` tool, is `NotVerifiable`); a rollback named only by its snapshot id is `NotVerifiable`. The docker service is resolved per verification (it is guarded by the gateway that holds the verifiers); a read the server budget turned away is tried again. |
| `Operations/` | The internal collaborator classes behind the `DockerService` facade — one file per concern; see [`Operations/README.md`](Operations/README.md). |
| `Budget/` | The **per-server load cap** every Docker call passes through, so the sum of what all the loops do against one host is bounded — the thing that was missing on 2026-08-26. Separate lanes for background work and for anything a human is waiting for; see [`Budget/README.md`](Budget/README.md). |
| `IDockerConnectionManager.cs` / `DockerConnectionManager.cs` | Provides and caches a `DockerClient` per server, with self-healing reconnect for dead SSH tunnels (instance-aware invalidation, so a retry never tears down a client another caller just rebuilt). Also builds each client (local / SSH-tunnel / TCP+mTLS transport, client certs + CA trust for mTLS); for TCP/mTLS it validates the server certificate's **chain and hostname** (`ValidateMtlsServerCert`) so a valid-but-wrong cert can't impersonate the host. |
| `ISshTunnelManager.cs` / `SshTunnelManager.cs` | Manages local SSH tunnels to remote Docker hosts (one local forward port per server); picks a free port with a bind-race retry, waits until the port actually accepts connections, and drains the ssh stderr so a full pipe can't freeze the tunnel. |

## Related

- Server registry & transport config: [`../ServerConfig/`](../ServerConfig/)
- Host-level (non-Docker) operations: [`../Server/`](../Server/)
- Real-time container updates to the UI: [`../../Hubs/ContainerHub.cs`](../../Hubs/ContainerHub.cs)
