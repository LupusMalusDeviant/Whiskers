# Services/Terminal

Interactive **web-terminal** sessions — a live shell in the browser, for both hosts and containers. The UI streams keystrokes and output over SignalR ([`../../Hubs/TerminalHub.cs`](../../Hubs/TerminalHub.cs)); this folder owns the session lifecycle and the underlying PTY/exec stream.

## Files

| File | Purpose |
|---|---|
| `ITerminalSessionManager.cs` / `TerminalSessionManager.cs` | Creates, tracks and tears down terminal sessions; routes input/output between the SignalR hub and the live session. |
| `TerminalSession.cs` | A single terminal session — wraps the underlying shell/exec stream and buffers I/O. |

## Related

- SignalR transport: [`../../Hubs/TerminalHub.cs`](../../Hubs/TerminalHub.cs)
- UI: [`../../Components/Pages/Terminal.razor`](../../Components/Pages/Terminal.razor), [`ServerTerminal.razor`](../../Components/Pages/ServerTerminal.razor)
- Container exec / host shell primitives: [`../Docker/DockerService.cs`](../Docker/DockerService.cs)
