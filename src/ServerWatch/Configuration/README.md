# Configuration

Strongly-typed settings classes bound from configuration (env vars / `.env` / `appsettings` / the writable `agent-settings.json` and `app-settings.json`). Each maps to a section and is injected via `IOptions<T>` / `IOptionsMonitor<T>`. Binding is wired in [`Program.cs`](../Program.cs); the env-var names are documented in [`.env.example`](../../../.env.example). UI edits are written to `app-settings.json` (the last config layer, reload-on-change) by [`../Services/Persistence/AppSettingsStore.cs`](../Services/Persistence/AppSettingsStore.cs) so they apply live.

## Files

| File | Section / purpose |
|---|---|
| `GoogleAuthSettings.cs` | Google OAuth 2.0 credentials. |
| `OidcSettings.cs` | Generic OpenID Connect provider settings. |
| `DockerSettings.cs` | Docker connection defaults. |
| `MetricsSettings.cs` | Metric collection cadence / retention. |
| `HealthMonitorSettings.cs` | Health-monitor thresholds. |
| `ImageUpdateSettings.cs` | Image-update check cadence. |
| `CveMonitorSettings.cs` | CVE scan cadence. |
| `MattermostSettings.cs` / `MatrixSettings.cs` | Notification channel settings. |
| `TerminalSettings.cs` | Web-terminal settings. |
| `AiChatSettings.cs` | Read-only advisor chat (provider, model, key). |
| `AgentSettings.cs` | Acting-agent provider access + system prompt (UI-editable, reload-on-change). |
| `GuardrailPolicy.cs` | A single guardrail policy (restrictive-by-default), incl. the per-policy tool mode (deny/allow). |
| `GuardrailConfig.cs` | The multi-preset wrapper: several named `GuardrailPreset`s + the active one (persisted in admin-only `guardrails.json`). |
| `MetricAlertSettings.cs` | Thresholds for `high_cpu` / `high_memory` / `metric_anomaly` events (drive AI triggers). |

## Related

- Env-var reference: [`../../../.env.example`](../../../.env.example)
- Agent settings store: [`../Services/Agent/AgentSettingsStore.cs`](../Services/Agent/AgentSettingsStore.cs)
- Guardrail store: [`../Services/Agent/Guardrails/`](../Services/Agent/Guardrails/)
