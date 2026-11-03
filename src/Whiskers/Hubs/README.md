# Hubs

SignalR hubs, the real-time bridge between the server and the Blazor UI. They push live data to the browser and relay interactive input back.

## Files

| File | Purpose |
|---|---|
| `ContainerHub.cs` | Streams live container state / stats / health to the dashboard and detail pages. |
| `SessionHubFilter.cs` | R1-26 (INV-09): an `IHubFilter` on `ContainerHub` only (registered with `AddHubOptions<ContainerHub>`). Every hub call re-checks the connection's principal through `ISessionValidator`; a revoked session (deleted account, removed from the whitelist, changed stamp, unreadable store) gets a `HubException` and the connection is closed. Hub-level — the HTTP middleware order is unchanged. |

> The web terminal does **not** use a SignalR hub. The terminal pages drive [`Terminal/`](../Services/Terminal/) sessions directly; the former `TerminalHub` was removed (unused by any client and exposed an unauthenticated shell).

## Related

- Terminal sessions: [`../Services/Terminal/`](../Services/Terminal/)
- Container data: [`../Services/Docker/`](../Services/Docker/), [`../Services/HealthMonitor/`](../Services/HealthMonitor/)
- UI consumers: [`../Components/Pages/`](../Components/Pages/)
