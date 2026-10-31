# Services/ServerConfig

The **fleet registry**: which Docker hosts exist and how to reach each one. Every connection ([`../Docker/`](../Docker/)) and host operation ([`../Server/`](../Server/)) starts from a server config record stored here.

A server config carries its connection type (`Local`, `SSH`, or `TCP`), transport details (SSH host/user/key material, or TCP + mTLS certificate paths with `TcpUseTls`), and its metrics source. Records are persisted as JSON under `/app/data`.

**Concurrency:** reads are lock-free over an immutable snapshot; every write (add/update/remove and the SSH-key changes) builds a new list under a lock — working on a `Clone()` of the target record — and swaps the reference atomically, so a reader never observes a half-applied change.

## Files

| File | Purpose |
|---|---|
| `IServerConfigService.cs` / `ServerConfigService.cs` | Stores and serves the configured Docker hosts plus their SSH key material; the single source of truth consumers resolve a server by id. Exposes `IsInitialized` (true once the registry has loaded) for the `/readyz` readiness probe. `UpdateServerAsync` keeps the stored `KubeconfigRef` under the write lock; only `UpdateServerAsync(server, newKubeconfigRef)` replaces it and returns the replaced record. `TakeServerAsync` removes a server and returns the record as stored then. R1-29: the cloud API key is decided under the same lock (`UpdateServerAsync(server, newKubeconfigRef, CloudKeyChange)`): keep the stored one, set a new vault reference, or clear it — an older copy can neither point back nor write plaintext; `AddServerAsync` refuses a plaintext `CloudApiKey`. `AdoptCloudKeyRefAsync` is the start-up migration's narrow call: it replaces exactly the given plaintext by a vault reference. |
| `CloudKeyChange.cs` | R1-29: keep / set / clear for a server's cloud API key; the plaintext field is only ever kept or removed, never set. |
| `ServerRemoval.cs` | The execution of `server.remove` (`[MutationHandler]`, R1-11): writes the server list first, then removes a Kubernetes server's kubeconfig and its cloud API key (R1-29, unless another server names the same key) from the vault; a failed removal keeps both, a failed cleanup leaves only an unused secret. |

## Related

- Connection transport selection: [`../Docker/DockerConnectionFactory.cs`](../Docker/DockerConnectionFactory.cs)
- Server models: [`../../Models/`](../../Models/)
- New servers are provisioned (mesh + mTLS) by [`../Onboarding/`](../Onboarding/).
- Audit (R1-22): add/update/take record the server's versioned diff ([`../Mutations/ConfigChange.cs`](../Mutations/ConfigChange.cs)), compared with the stored state, not
  the cached object; SSH key replacements are serialized and recorded as section `ssh-key:<server>` (fingerprints only).
