# Modules

The module framework from [RoadToSAP](../../../docs/roadmap/RoadToSAP.md): a lean **Core** plus **modules**
that contribute features and can be switched on/off via `Features:{id}:Enabled`. **Phase 1** is live — the
pipeline (discovery → services → MCP tools → navigation) is wired, and features are extracted into real
`Modules/<Name>/` modules one PR at a time (Terminal was first, then Notifications). The transitional
`AllInOnePseudoModule` still carries everything not yet extracted and shrinks with each module PR.

## Files

| File | Purpose |
|---|---|
| `IWhiskersModule.cs` | The module contract: `Id`, `DisplayName`, `EnabledByDefault`, `DependsOn`, `ConfigureServices`, `NavItems`, `McpToolTypes`, `InitializeAsync`. Modules consume Core interfaces, never the reverse. `InitializeAsync` runs at startup **after** the DB migration (`RunWhiskersStartupAsync`), enabled modules only — first consumer: the Webhooks module's F11 secret-mandatory upgrade. |
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
  `ICloudProvider` seam deferred), and ✅ **ImageUpdate/AutoUpdate** (§3.7 — one module for both; no-op store
  for the Core Dashboard + `ContainerTools` consumers; C12 rollback deferred) are done. ✅ **Agent+AiChat**
  (§3.8, the last and largest §3 module — the acting agent, guardrails, approvals, the AI-chat advisor + AI
  triggers; a Core `NoopAiTriggerDispatcher` for the notification composite's lazy dispatch, the global
  `<AiChat/>` widget gated in `MainLayout`, and `agent-history` kept in Core as MCP-call observability; the
  `AgentToolRegistry`→`ModuleRegistry` change stays deferred) is done — **all §3 modules are now extracted.**
  Each module PR moved its registrations here verbatim, wrapped its
  pages in `ModuleGuard` / gated its `Settings.razor` section when off, and proved `Features:<id>:Enabled=false`
  boots cleanly. What remains in the transitional `AllInOnePseudoModule` is the
  lean Core surface (dashboard, health, graph, diff, notifications feed, audit log, compose editor,
  servers/networks, agent-history, settings, help) plus the mixed MCP tool
  classes (`ContainerTools`, `ServerTools`) — these are Core, not pending extraction.
- **§6 DoD (nearly complete):** a `docs/modules/` index ✅ + an `ARCHITECTURE.md` "Module System" chapter ✅ +
  a DI-level all-on/all-off/only-core matrix test ✅ + a full-app `WebApplicationFactory` boot-matrix test ✅
  (`BootMatrixTests`, boots the real app under ValidateOnBuild in each configuration and pings `/healthz`) +
  a `Modules/HelloWorld` example ✅; **still open — only `Program.cs` <150 lines** (needs the auth/OIDC/endpoint
  setup extracted into extension methods — a separate cleanup PR, not a module move).
- **F2 (i18n):** each `NavItem.LocKey` (a German label today) becomes a real `IStringLocalizer` key.

See RoadToSAP §2–3 for the full design and phasing.
