# Modules

The module framework from [RoadToSAP](../../../docs/roadmap/RoadToSAP.md): a lean **Core** plus **modules**
that contribute features and can be switched on/off via `Features:{id}:Enabled`. **Phase 1** is live — the
pipeline (discovery → services → MCP tools → navigation) is wired, and features are extracted into real
`Modules/<Name>/` modules one PR at a time (Terminal was first, then Notifications). The transitional
`AllInOnePseudoModule` still carries everything not yet extracted and shrinks with each module PR.

## Files

| File | Purpose |
|---|---|
| `IWhiskersModule.cs` | The module contract: `Id`, `DisplayName`, `EnabledByDefault`, `DependsOn`, `ConfigureServices`, `NavItems`, `McpToolTypes`, `InitializeAsync`. Modules consume Core interfaces, never the reverse. |
| `ModuleCatalog.cs` | The single **explicit** list of modules (no assembly scanning) + `DiscoverEnabled(config)`: filters by `Features:{id}:Enabled` (overrides `EnabledByDefault`) and fails fast on an unmet `DependsOn`. |
| `AllInOnePseudoModule.cs` | Transitional "everything not yet a real module" bucket — carries the not-yet-extracted nav entries + MCP tool classes with a **no-op** `ConfigureServices` (registrations stay inline in `Program.cs`). Shrinks with each module PR (Scheduler took the `tasks` nav + `SchedulerTools`); retired once empty. Kept in sync with [`../Components/Layout/NavMenu.razor`](../Components/Layout/NavMenu.razor). |
| `NavItem.cs` | A navigation entry a module contributes (`Href`, `LocKey`, `Icon`, `Group`, `MinRole`, `Order`). |
| `NavLayout.cs` | Pure, unit-tested helper that turns the flat `NavItem` list into ordered sidebar groups + top-level entries (`NavGroup`). |
| `IModuleRegistry.cs` / `ModuleRegistry.cs` | Exposes the enabled modules' merged `NavItems` to `NavMenu.razor`. |

## How it wires up (`Program.cs`)

```csharp
var modules = ModuleCatalog.DiscoverEnabled(builder.Configuration); // Features:<id>:Enabled gate
foreach (var m in modules) m.ConfigureServices(builder.Services, builder.Configuration);
// MCP host registers modules.SelectMany(m => m.McpToolTypes)
// IModuleRegistry is built from modules.SelectMany(m => m.NavItems)
```

A disabled module contributes no services (its hosted services never run), no nav entry, and no MCP tools.
Toggling a module is **restart-only** (no hot-toggle) — `Features:{id}:Enabled` lives in config/`app-settings.json`.

## What's next

- **Extract features into real modules**, one PR each. ✅ **Terminal** (the pilot), ✅ **Notifications**,
  ✅ **Scheduler** (first with a nav entry + MCP tools), ✅ **LogMonitor** (first needing a no-op Core default
  for a cross-feature consumer), ✅ **VolumeBackups** (no-op for a cross-**module** consumer — the Scheduler
  task executor), and ✅ **Webhooks** (no-op for a Core `Program.cs` endpoint that can't move) are done — each
  has its own README + `docs/modules/<id>.md`. ✅ **HostManagement** (Nginx/Systemd/Firewall/SslCerts as one
  module — needs 4 Core no-ops because the mixed `ServerTools` MCP class stays Core) and ✅ **Deployment**
  (`/deploy` + `/apps` — 3 Core no-ops because the mixed `ContainerTools` stays Core) and ✅ **Cve** (§3.5 —
  dedicated `CveTools` moves with it; no-ops for the Core Dashboard/ContainerDetail/Settings consumers; the C8
  service-locator removal is deferred), and ✅ **CloudControl** (§3.6 — clean extraction, no no-ops; C10
  `ICloudProvider` seam deferred) are done. **Remaining §3 modules (in order):** ImageUpdate/AutoUpdate (§3.7),
  Agent+AiChat (§3.8, +the `AgentToolRegistry`→`ModuleRegistry` change). Each module PR moves its registrations here verbatim, wraps its
  pages in `ModuleGuard` / gates its `Settings.razor` section when off, and proves `Features:<id>:Enabled=false`
  boots cleanly. What remains in the transitional `AllInOnePseudoModule` is the
  lean Core surface (dashboard, health, CVE, graph, diff, notifications feed, audit log, compose editor,
  servers/cloud/networks, agent/guardrails/approvals/ai-triggers, settings, help) plus the mixed MCP tool
  classes (`ContainerTools`, `ServerTools`) — these are Core, not pending extraction.
- A `docs/modules/` index + an `ARCHITECTURE.md` "Module System" chapter (RoadToSAP §6 DoD).
- **F2 (i18n):** each `NavItem.LocKey` (a German label today) becomes a real `IStringLocalizer` key.

See RoadToSAP §2–3 for the full design and phasing.
