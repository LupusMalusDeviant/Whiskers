# Services/Terminal

Interactive **web-terminal** sessions, a live shell in the browser, for both hosts and containers. The terminal Blazor pages open a session through the Mutation Gateway and stream it into xterm; this folder owns the session lifecycle, its audit and the underlying process.

## Files

| File | Purpose |
|---|---|
| `ITerminalSessionManager.cs` / `TerminalSessionManager.cs` | Opens, tracks and ends sessions (R1-17). `OpenContainerShellAsync` / `OpenHostShellAsync` are `[MutatingOperation]`s (`terminal.container_open` / `terminal.host_open`, Admin, root-equivalent): the pages call them inside `IUiMutationRunner.RunAsync`, so Admin, the kill switch and protected resources are decided and the open is audited with the session id. A server id that names no configured server is refused — never a local shell in its place; an empty one is the default server, as the gateway resolves it. `CloseAsync` ends a session exactly once and writes its `terminal.close` audit row (principal, target, server, duration, `TerminalCloseReason`); what is typed is never recorded (PC-D2). `SweepAsync` runs every 15 s (`SweepInterval`) and ends exited, idle and disabled sessions and those `ITerminalAuthority` says must end; an authority that throws ends the session (fail closed). Also `ITerminalAuthority`. |
| `TerminalAuthority.cs` | Whether a running session may go on: the kill switch ends every session (INV-21), an owner who no longer holds Admin — read like an automation's owner through `IAutomationOwnerResolver` — ends theirs (INV-09). |
| `TerminalSession.cs` | One session: owner (`TerminalOwner`, the gateway's principal id), target (`TerminalTarget`, host or container on a server), output streams and input. Also `TerminalLaunch` (the program and arguments of a local / SSH / SSH + `docker exec` shell, never through a shell on Whiskers' side), `ITerminalLauncher` / `ProcessTerminalLauncher` (starts it as an OS process; tests use a fake) and `TerminalCloseReason`. |

## Related

- UI: [`../../Components/Pages/TerminalView.razor`](../../Components/Pages/TerminalView.razor), [`ServerTerminalView.razor`](../../Components/Pages/ServerTerminalView.razor), the embedded terminal in [`ContainerDetail.razor`](../../Components/Pages/ContainerDetail.razor)
- Registration: [`../../Modules/Terminal/TerminalModule.cs`](../../Modules/Terminal/TerminalModule.cs)
- Gateway: [`../Mutations/`](../Mutations/) (`CapabilityCatalog`, `UiMutationRunner`)
