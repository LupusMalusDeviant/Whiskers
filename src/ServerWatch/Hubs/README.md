# Hubs

SignalR hubs, the real-time bridge between the server and the Blazor UI. They push live data to the browser and relay interactive input back.

## Files

| File | Purpose |
|---|---|
| `ContainerHub.cs` | Streams live container state / stats / health to the dashboard and detail pages. |
| `TerminalHub.cs` | Bidirectional terminal I/O, relays keystrokes and output between the browser and a [`Terminal/`](../Services/Terminal/) session. |

## Related

- Terminal sessions: [`../Services/Terminal/`](../Services/Terminal/)
- Container data: [`../Services/Docker/`](../Services/Docker/), [`../Services/HealthMonitor/`](../Services/HealthMonitor/)
- UI consumers: [`../Components/Pages/`](../Components/Pages/)
