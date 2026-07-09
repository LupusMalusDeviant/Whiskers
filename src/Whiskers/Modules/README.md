# Modules

The module-framework scaffold from [RoadToSAP](../../../docs/roadmap/RoadToSAP.md). The end goal is a
lean **Core** plus **modules** that contribute features and can be switched on/off. This folder holds
**Phase 0** only — the preparatory types. It is **inert**: nothing consumes the registry yet and
behaviour is unchanged.

## Files

| File | Purpose |
|---|---|
| `NavItem.cs` | A navigation entry a module contributes (`Href`, `LocKey`, `Icon`, `Group`, `MinRole`, `Order`). |
| `IModuleRegistry.cs` / `ModuleRegistry.cs` | Aggregates module metadata — for now just the nav entries. |
| `AllInOnePseudoModule.cs` | Placeholder that supplies today's hard-coded navigation so the registry can exist before real modules do. Kept in sync with [`../Components/Layout/NavMenu.razor`](../Components/Layout/NavMenu.razor). |

Registered in [`Program.cs`](../Program.cs) as a singleton `IModuleRegistry`.

## What's next (not in Phase 0)

- **Phase 1** flips `NavMenu.razor` to render from `IModuleRegistry.NavItems`, introduces the real
  `IWhiskersModule` contract (`ConfigureServices`, `McpToolTypes`, `InitializeAsync`, …), migrates each
  feature into `Modules/<Name>/`, and adds `Features:<id>:Enabled` flags. As features move, their
  entries leave `AllInOnePseudoModule` and it eventually retires.
- **F2 (i18n)** replaces each `NavItem.LocKey` (currently the German label) with a real localization key.

See RoadToSAP §2–3 for the full design and phasing.
