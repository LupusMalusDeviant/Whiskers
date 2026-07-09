# Services/Templates

The built-in **app deployment templates** that power the App Store, standardised image/port/env/volume presets so common apps deploy in one click.

## Files

| File | Purpose |
|---|---|
| `ITemplateService.cs` / `TemplateService.cs` | Provides the built-in app deployment templates. |
| `NoopTemplateService.cs` | Core default `ITemplateService` for when the **Deployment module** is off — advertises no templates (so a `deploy_app` by template id fails cleanly). Real service wins by last-registration when on (RoadToSAP Phase 1). |

## Wiring

Registered by the opt-in **Deployment module** ([`../../Modules/Deployment/`](../../Modules/Deployment/),
toggle `Features:deployment:Enabled`). The Core, mixed `ContainerTools` (`deploy_app`) resolves this per call,
so Core keeps the `NoopTemplateService` default for when the module is off.

## Related

- Deployment: [`../Deployment/`](../Deployment/)
- UI: [`../../Components/Pages/AppStore.razor`](../../Components/Pages/AppStore.razor)
