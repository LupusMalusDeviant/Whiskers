# Modules/HelloWorld

The **minimal example module** — living documentation for the RoadToSAP module system (§6 DoD). It's the
smallest thing that exercises the whole `IWhiskersModule` contract, and it's **disabled by default**
(`EnabledByDefault = false`), so it does nothing in production until someone opts in.

## What it demonstrates

| Part | Here | Where a real module puts it |
|---|---|---|
| Id + feature flag | `Id => "hello-world"` → `Features:hello-world:Enabled` | same |
| A service | `IHelloWorldGreeter` / `HelloWorldGreeter` (in this folder) | under `Services/<Area>/` |
| `ConfigureServices` | registers the greeter | move registrations here **verbatim** from `Program.cs` |
| A nav entry | `NavItems` → `hello-world` | `NavItems` (merged into the sidebar registry) |
| A gated page | [`HelloWorld.razor`](../../Components/Pages/HelloWorld.razor) wrapper → [`HelloWorldView.razor`](../../Components/Pages/HelloWorldView.razor) | thin `ModuleGuard` wrapper + child view |
| MCP tools | none (`McpToolTypes` empty) | list `[McpServerToolType]` classes in `McpToolTypes` |

## Enable it

Set `Features:hello-world:Enabled=true` (env `Features__hello-world__Enabled=true`) and restart. A "Hello
World" entry appears in the *Übersicht* sidebar group; `/hello-world` renders the greeter. Turn it off and the
nav entry disappears and `/hello-world` shows a "module disabled" notice.

## Start a new module from this

1. Copy `Modules/HelloWorld/` to `Modules/<YourName>/`, rename the class + `Id`, set `EnabledByDefault = true`.
2. Move its registrations **verbatim** from `Program.cs` into `ConfigureServices`; add its nav + MCP tool types.
3. If a **Core** page/service or a mixed MCP tool class (that must stay Core) consumes one of the module's
   services, add a **no-op default** for it in Core before the module loop (see e.g. `NoopNotificationService`).
4. Wrap the module's pages in `ModuleGuard` (or gate a `Settings.razor` panel with `@if IsEnabled(...)`).
5. Register the module in [`../ModuleCatalog.cs`](../ModuleCatalog.cs); add a per-folder README +
   `docs/modules/<id>.md`; prove `Features:<id>:Enabled=false` boots cleanly (Development + `ValidateOnBuild`).

See the [module system chapter in ARCHITECTURE.md](../../../../docs/ARCHITECTURE.md) and
[`docs/modules/`](../../../../docs/modules/).
