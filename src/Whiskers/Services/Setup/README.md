# Services/Setup

First-run and operational-readiness state, consumed by the setup wizard (W1) and the Settings page.

## Files

| File | Purpose |
|---|---|
| `ISetupStateService.cs` / `SetupStateService.cs` | Whether setup is complete (= at least one admin exists; admin-role-authoritative with a cached flag file). Drives the setup-redirect middleware. |
| `SetupCompletion.cs` | The atomic, race-guarded wizard completion (create admin → mark complete). |
| `ProductionReadinessService.cs` | W3.4: the static "Produktionsreif?" checklist shown in Settings — read-only checks (auth on, VAULT_KEY set, self-backup scheduled, update policy, non-root, HTTPS). Informational only, gates nothing. |

## Related

- Wizard UI: [`../../Components/Pages/Setup.razor`](../../Components/Pages/Setup.razor)
- Checklist panel: [`../../Components/Shared/ProductionReadinessPanel.razor`](../../Components/Shared/ProductionReadinessPanel.razor)
- Redirect middleware: [`../../Startup/WhiskersPipelineExtensions.cs`](../../Startup/WhiskersPipelineExtensions.cs)
