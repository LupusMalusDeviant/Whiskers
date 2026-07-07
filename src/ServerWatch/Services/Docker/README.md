# Services/Docker

Everything that talks to the Docker Engine. This folder owns the **connection layer** (how ServerWatch reaches each host's Docker daemon, local socket, SSH tunnel, or TCP/mTLS) and the **operations layer** (containers, images, networks, stats).

Connections are established per configured server and cached; for SSH-tunnelled hosts the manager self-heals dead tunnels. For TCP/mTLS hosts there is no SSH at all (see [docs/ARCHITECTURE.md](../../../docs/ARCHITECTURE.md)).

## Files

| File | Purpose |
|---|---|
| `IDockerService.cs` / `DockerService.cs` | The main Docker operations surface: list/inspect/start/stop/restart/remove containers, images, networks, stats, logs. Also runs **host shell commands SSH-free** via a one-shot privileged `nsenter` container over the mTLS Docker channel. Each such helper is labelled `serverwatch.hostshell=1` and removed in a `finally`; if a run is interrupted before that (app rebuilt mid-command, mTLS drop) the next call sweeps any leftover labelled, non-running helpers older than 30 s, so they never pile up. |
| `IDockerConnectionManager.cs` / `DockerConnectionManager.cs` | Provides and caches a `DockerClient` per server, with self-healing reconnect for dead SSH tunnels (instance-aware invalidation, so a retry never tears down a client another caller just rebuilt). For TCP/mTLS it validates the server certificate's **chain and hostname** (`ValidateMtlsServerCert`) so a valid-but-wrong cert can't impersonate the host. |
| `DockerConnectionFactory.cs` | Builds a `DockerClient` for a given server config, picks local / SSH-tunnel / TCP+mTLS transport and wires up client certificates and CA trust for mTLS. |
| `ISshTunnelManager.cs` / `SshTunnelManager.cs` | Manages local SSH tunnels to remote Docker hosts (one local forward port per server); picks a free port with a bind-race retry, waits until the port actually accepts connections, and drains the ssh stderr so a full pipe can't freeze the tunnel. |

## Related

- Server registry & transport config: [`../ServerConfig/`](../ServerConfig/)
- Host-level (non-Docker) operations: [`../Server/`](../Server/)
- Real-time container updates to the UI: [`../../Hubs/ContainerHub.cs`](../../Hubs/ContainerHub.cs)
