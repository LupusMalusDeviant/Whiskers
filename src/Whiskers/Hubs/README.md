# Hubs

SignalR hubs, the real-time bridge between the server and the Blazor UI. They push live data to the browser and relay interactive input back.

## Files

| File | Purpose |
|---|---|
| `ContainerHub.cs` | Streams live container state / stats / health to the dashboard and detail pages. |
| `SessionHubFilter.cs` | R1-26 (INV-09): an `IHubFilter` on `ContainerHub` only (registered with `AddHubOptions<ContainerHub>`). Every hub call re-checks the connection's principal through `ISessionValidator`; a revoked session (deleted account, removed from the whitelist, changed stamp, unreadable store) gets a `HubException` and the connection is closed and audited (`auth.session_revoked`, surface `hub`). It also registers each open connection with `HubSessionSweep`. Hub-level — the HTTP middleware order is unchanged. |
| `HubSessionSweep.cs` | R1-26: the open `ContainerHub` connections, re-checked every 15 s (hosted service, `TimeProvider` timer). The server broadcasts to every connection (`ContainerListUpdated` from the health monitor, CVE and image-update events) without the client calling anything, so a silent revoked connection is closed here and audited (surface `hub`); a check that throws closes it too. |

> The web terminal does **not** use a SignalR hub. The terminal pages drive [`Terminal/`](../Services/Terminal/) sessions directly; the former `TerminalHub` was removed (unused by any client and exposed an unauthenticated shell).

## Related

- Terminal sessions: [`../Services/Terminal/`](../Services/Terminal/)
- Container data: [`../Services/Docker/`](../Services/Docker/), [`../Services/HealthMonitor/`](../Services/HealthMonitor/)
- UI consumers: [`../Components/Pages/`](../Components/Pages/)
