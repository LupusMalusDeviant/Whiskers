# Modules/Terminal

The interactive web terminal (container shell + host/server shell) — the first feature extracted from the
transitional `AllInOnePseudoModule` into its own `IWhiskersModule` (RoadToSAP Phase 1 pilot).

- `TerminalModule.cs` — `Id = "terminal"`, enabled by default. `ConfigureServices` binds `TerminalSettings`
  and registers `ITerminalSessionManager` (moved **verbatim** from `Program.cs`). No nav entry (the terminal
  is opened contextually from a container/server) and no MCP tools.

**Toggle:** `Features:terminal:Enabled` (`Features__terminal__Enabled=false`), restart-only. When off, the
service isn't registered and the two pages show a "module disabled" notice via `ModuleGuard` instead of
failing — and the Terminal panel in Settings is hidden.

**Structure of the pages** (the DI-safe guard pattern): the `@page` files
[`Terminal.razor`](../../Components/Pages/Terminal.razor) and
[`ServerTerminal.razor`](../../Components/Pages/ServerTerminal.razor) are thin route wrappers —
`<ModuleGuard ModuleId="terminal"><…View/></ModuleGuard>`. The interactive logic that injects
`ITerminalSessionManager` lives in the child components `TerminalView` / `ServerTerminalView`, so a disabled
module never instantiates them and never hits a missing-service DI error. The session/service code stays in
[`../../Services/Terminal/`](../../Services/Terminal/).

See [RoadToSAP](../../../../docs/roadmap/RoadToSAP.md) and [`docs/modules/terminal.md`](../../../../docs/modules/terminal.md).
