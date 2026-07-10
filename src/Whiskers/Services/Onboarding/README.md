# Services/Onboarding

**One-click server onboarding** into the zero-SSH-key managed stack. Starting from a single SSH bootstrap connection (the only time SSH is used), the orchestrator installs Tailscale, deploys telemetry and the mTLS Docker proxy, issues certificates, and switches the server to mesh + mTLS, with live progress streamed to the UI.

The bootstrap authenticates with **either an uploaded SSH key or a transient root/SSH password** (fed to `sshpass` via the `SSHPASS` env var, never persisted, `ServerConfig.SshPassword` is `[JsonIgnore]`). On success both are dropped: the password is cleared from memory and the key deleted from disk (`IServerConfigService.DeleteSshKeyAsync`), so **no standing credential remains**. Reachable from the server add/edit dialog (**„Speichern & Onboarden"**) or the per-row onboarding button.

See [docs/ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) > *Onboarding a new server* for the full step sequence.

## Files

| File | Purpose |
|---|---|
| `IOnboardingService.cs` / `OnboardingService.cs` | Orchestrates onboarding into the Tailscale + mTLS-proxy + telemetry stack, end to end, reporting progress (incl. surfacing the Tailscale login link in the app). Returns a step-tracked `OnboardingResult`. |
| `OnboardingResult.cs` | The `OnboardingStep` enum + result record (completed steps, failed step, actionable German hint per step). Every step is idempotent → a re-run after failure resumes safely (W3). |
| `OnboardingCommands.cs` | Pure, unit-tested command/config builders (`OnboardingCommandsTests`): user input only reaches the shell through the strict `Slug` allow-list or base64; the tailnet IP is validated with `IPAddress.TryParse` before use. |

## Related

- Architecture: [`../../../docs/ARCHITECTURE.md`](../../../docs/ARCHITECTURE.md)
- Deploy templates: [`../../../deploy/telemetry/`](../../../deploy/telemetry/)
- Server registry: [`../ServerConfig/`](../ServerConfig/)
