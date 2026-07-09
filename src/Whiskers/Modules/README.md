# Modules

The module framework from [RoadToSAP](../../../docs/roadmap/RoadToSAP.md): a lean **Core** plus **modules**
that contribute features and can be switched on/off via `Features:{id}:Enabled`. **Phase 1** is now live —
the pipeline (discovery → services → MCP tools → navigation) is wired and **behaviour-neutral**: today the
only module is the transitional `AllInOnePseudoModule`, so nothing changes yet. Features are extracted into
real `Modules/<Name>/` modules one PR at a time (Terminal first).

## Files

| File | Purpose |
|---|---|
| `IWhiskersModule.cs` | The module contract: `Id`, `DisplayName`, `EnabledByDefault`, `DependsOn`, `ConfigureServices`, `NavItems`, `McpToolTypes`, `InitializeAsync`. Modules consume Core interfaces, never the reverse. |
| `ModuleCatalog.cs` | The single **explicit** list of modules (no assembly scanning) + `DiscoverEnabled(config)`: filters by `Features:{id}:Enabled` (overrides `EnabledByDefault`) and fails fast on an unmet `DependsOn`. |
| `AllInOnePseudoModule.cs` | Transitional "everything not yet a real module" bucket — carries today's 24 nav entries + 11 MCP tool classes with a **no-op** `ConfigureServices` (registrations stay inline in `Program.cs`). Shrinks as features are extracted; retired once empty. Kept in sync with [`../Components/Layout/NavMenu.razor`](../Components/Layout/NavMenu.razor). |
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

- **Extract features into real modules**, one PR each — **Terminal** is the pilot (`Modules/Terminal/`). Each
  module PR moves its registrations here verbatim, adds a `ModuleGuard` around its pages, extracts its
  `Settings.razor` section, and proves `Features:<id>:Enabled=false` boots cleanly.
- A `docs/modules/` index + an `ARCHITECTURE.md` "Module System" chapter (RoadToSAP §6 DoD).
- **F2 (i18n):** each `NavItem.LocKey` (a German label today) becomes a real `IStringLocalizer` key.

See RoadToSAP §2–3 for the full design and phasing.
