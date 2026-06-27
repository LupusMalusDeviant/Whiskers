# Services/Onboarding

**One-click server onboarding** into the zero-SSH-key managed stack. Starting from a single SSH bootstrap connection (the only time SSH is used), the orchestrator installs Tailscale, deploys telemetry and the mTLS Docker proxy, issues certificates, and switches the server to mesh + mTLS — with live progress streamed to the UI.

See [docs/ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) → *Onboarding a new server* for the full step sequence.

## Files

| File | Purpose |
|---|---|
| `IOnboardingService.cs` / `OnboardingService.cs` | Orchestrates onboarding into the Tailscale + mTLS-proxy + telemetry stack, end to end, reporting progress (incl. surfacing the Tailscale login link in the app). |

## Related

- Architecture: [`../../../docs/ARCHITECTURE.md`](../../../docs/ARCHITECTURE.md)
- Deploy templates: [`../../../deploy/telemetry/`](../../../deploy/telemetry/)
- Server registry: [`../ServerConfig/`](../ServerConfig/)
