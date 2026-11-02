# Services/Docker/Operations

Internal collaborator classes behind the `DockerService` facade (see [`../README.md`](../README.md)). Each class owns one slice of the Docker operations surface; all are `internal sealed`, constructed by `DockerService` itself (no DI registrations), and share the facade's `ILogger<DockerService>` category plus a single `MemoryCache` instance so runtime behaviour is identical to the pre-split god class.

## Fleet-wide listing

`ListAllContainersDetailedAsync` returns the containers of every enabled Docker server **plus which servers
answered** (`FleetContainerListing`). The plain `ListAllContainersAsync` is a thin wrapper over it. Callers
that keep per-server state across cycles (log watermarks, previous container states, cooldowns) must use the
detailed variant: an unreachable host and an empty host both produce an empty list, and dropping state for
the unreachable one silently re-baselines it on recovery.

## Files

| File | Purpose |
|---|---|
| `ContainerOperations.cs` | List/inspect containers, stats (with 3 s cache), logs, start/stop/restart/remove, env inspection; also hosts the internal Docker-stats JSON DTOs. |
| `ContainerLifecycleOperations.cs` | Create from a `DeploymentRequest`, recreate-with-rename-fail-safe, and the C12 update-rollback (snapshot capture + rollback to the old image). |
| `ImageOperations.cs` | Pull images (with robust repo/tag/digest reference parsing) and read image digests. |
| `NetworkOperations.cs` | List/create/remove Docker networks, connect/disconnect containers. |
| `HostShellOperations.cs` | SSH-free host shell via one-shot privileged `nsenter` helper containers, including the labelled-leftover sweep. The timeout and the caller's token (R1-38) both kill the helper; it is always removed afterwards. |
| `SystemInfoOperations.cs` | Per-server system info/reachability, host CPU/memory usage (Prometheus, /proc exec, or container-stats fallback). |
