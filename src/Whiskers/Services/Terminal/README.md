# Services/Terminal

Interactive **web-terminal** sessions, a live shell in the browser, for both hosts and containers. The terminal Blazor pages drive these sessions directly (server-side streaming + JS interop into xterm); this folder owns the session lifecycle and the underlying PTY/exec stream.

## Files

| File | Purpose |
|---|---|
| `ITerminalSessionManager.cs` / `TerminalSessionManager.cs` | Creates, tracks and tears down terminal sessions; routes input/output between the terminal pages and the live session. |
| `TerminalSession.cs` | A single terminal session, wraps the underlying shell/exec stream and buffers I/O. |

## Related

- UI: [`../../Components/Pages/Terminal.razor`](../../Components/Pages/Terminal.razor), [`ServerTerminal.razor`](../../Components/Pages/ServerTerminal.razor)
- Container exec / host shell primitives: [`../Docker/DockerService.cs`](../Docker/DockerService.cs)
