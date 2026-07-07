# Hubs

SignalR hubs, the real-time bridge between the server and the Blazor UI. They push live data to the browser and relay interactive input back.

## Files

| File | Purpose |
|---|---|
| `ContainerHub.cs` | Streams live container state / stats / health to the dashboard and detail pages. |

> The web terminal does **not** use a SignalR hub. The terminal pages drive [`Terminal/`](../Services/Terminal/) sessions directly; the former `TerminalHub` was removed (unused by any client and exposed an unauthenticated shell).

## Related

- Terminal sessions: [`../Services/Terminal/`](../Services/Terminal/)
- Container data: [`../Services/Docker/`](../Services/Docker/), [`../Services/HealthMonitor/`](../Services/HealthMonitor/)
- UI consumers: [`../Components/Pages/`](../Components/Pages/)
