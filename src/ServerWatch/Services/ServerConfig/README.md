# Services/ServerConfig

The **fleet registry**: which Docker hosts exist and how to reach each one. Every connection ([`../Docker/`](../Docker/)) and host operation ([`../Server/`](../Server/)) starts from a server config record stored here.

A server config carries its connection type (`Local`, `SSH`, or `TCP`), transport details (SSH host/user/key material, or TCP + mTLS certificate paths with `TcpUseTls`), and its metrics source. Records are persisted as JSON under `/app/data`.

## Files

| File | Purpose |
|---|---|
| `IServerConfigService.cs` / `ServerConfigService.cs` | Stores and serves the configured Docker hosts plus their SSH key material; the single source of truth consumers resolve a server by id. |

## Related

- Connection transport selection: [`../Docker/DockerConnectionFactory.cs`](../Docker/DockerConnectionFactory.cs)
- Server models: [`../../Models/`](../../Models/)
- New servers are provisioned (mesh + mTLS) by [`../Onboarding/`](../Onboarding/).
