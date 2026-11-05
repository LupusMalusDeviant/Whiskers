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
| `ContainerOperations.cs` | List/inspect containers, stats (with 3 s cache), logs, start/stop/restart/remove, env inspection; also hosts the internal Docker-stats JSON DTOs. R1-41: the fleet listing's per-server timeout (`PerServerTimeout`, 8 s) bounds the wait for the client and for a budget slot as well as the request — running out in the request is that server's timeout, running out while queued for a slot is reported as `FleetServerFailure.NotAsked` (Whiskers' own saturation, no evidence about the server), the caller's own cancellation stays a cancellation; the state and configured-image inspects take the caller's token. Follow-up (2026-11-03): a fleet listing failure after the per-server deadline reads "Timed out" only when the deadline caused it (a cancellation or timeout in the chain); another error keeps its own reason. |
| `ContainerLifecycleOperations.cs` | Create from a `DeploymentRequest`, recreate-with-rename-fail-safe, and the C12 update-rollback (snapshot capture + rollback to the old image). |
| `ImageOperations.cs` | Pull images (with robust repo/tag/digest reference parsing) and read image digests. R1-41: `GetImageDigestAsync(…, ct)` and `GetImageContractAsync(…, ct)` pass the caller's token to the inspect and the slot wait; a cancelled caller gets the cancellation, not `null`. |
| `NetworkOperations.cs` | List/create/remove Docker networks, connect/disconnect containers. |
| `HostShellOperations.cs` | SSH-free host shell via one-shot privileged `nsenter` helper containers, including the labelled-leftover sweep. The timeout and the caller's token (R1-38) both kill the helper; it is always removed afterwards. R1-57: the timeout is one deadline from getting the client through the image check/pull, create, start, wait and log read; every Docker call is awaited against it (`Within`), also one that ignores its token. Kill and remove get a clean-up budget of their own (5 s) after it, so a caller has its answer at most timeout + 5 s after the call; what the clean-up leaves, the sweep (own 30 s budget) removes. A stop before the start request reads "did not start", from it on "effect unknown", after the exit "exit code N, output not read". |
| `HostShellDocker.cs` | `IHostShellDocker`: the Docker calls of the host shell, one per step, and `DockerClientHostShell`, the production implementation over `DockerClient`. A seam so the deadline logic does not depend on the client library and tests can stall any single step (`HostShellDeadlineTests`). |
| `SystemInfoOperations.cs` | Per-server system info/reachability, host CPU/memory usage (Prometheus, /proc exec, or container-stats fallback). |
