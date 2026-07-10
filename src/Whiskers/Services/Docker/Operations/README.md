# Services/Docker/Operations

Internal collaborator classes behind the `DockerService` facade (see [`../README.md`](../README.md)). Each class owns one slice of the Docker operations surface; all are `internal sealed`, constructed by `DockerService` itself (no DI registrations), and share the facade's `ILogger<DockerService>` category plus a single `MemoryCache` instance so runtime behaviour is identical to the pre-split god class.

## Files

| File | Purpose |
|---|---|
| `ContainerOperations.cs` | List/inspect containers, stats (with 3 s cache), logs, start/stop/restart/remove, env inspection; also hosts the internal Docker-stats JSON DTOs. |
| `ContainerLifecycleOperations.cs` | Create from a `DeploymentRequest`, recreate-with-rename-fail-safe, and the C12 update-rollback (snapshot capture + rollback to the old image). |
| `ImageOperations.cs` | Pull images (with robust repo/tag/digest reference parsing) and read image digests. |
| `NetworkOperations.cs` | List/create/remove Docker networks, connect/disconnect containers. |
| `HostShellOperations.cs` | SSH-free host shell via one-shot privileged `nsenter` helper containers, including the labelled-leftover sweep. |
| `SystemInfoOperations.cs` | Per-server system info/reachability, host CPU/memory usage (Prometheus, /proc exec, or container-stats fallback). |
