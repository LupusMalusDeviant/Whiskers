# Configuration

Strongly-typed settings classes bound from configuration (env vars / `.env` / `appsettings` / the writable `agent-settings.json` and `app-settings.json`). Each maps to a section and is injected via `IOptions<T>` / `IOptionsMonitor<T>`. Binding is wired in [`Program.cs`](../Program.cs); the env-var names are documented in [`.env.example`](../../../.env.example). UI edits are written to `app-settings.json` (the last config layer, reload-on-change) by [`../Services/Persistence/AppSettingsStore.cs`](../Services/Persistence/AppSettingsStore.cs) so they apply live.

**`DataPathOptions`** is the one exception to the `IOptions<T>` pattern below: it centralizes every path under the data directory (`WHISKERS_DATA_DIR`, default `/app/data`) and is built at **bootstrap** — before the DI container exists — then registered as a plain singleton and injected directly. Consumers take it as an optional last constructor parameter (the container supplies the registered instance; an explicit path still wins, which keeps the test seams working).

## Files

| File | Section / purpose |
|---|---|
| `DataPathOptions.cs` | Central resolver for all data-directory paths (DB, JSON stores, DataProtection keys, `ssh-keys/`, `mtls/`, `backups/`). Root = `WHISKERS_DATA_DIR` (default `/app/data`). Built at bootstrap, injected as a plain singleton — **not** via `IOptions<T>`. |
| `GoogleAuthSettings.cs` | Google OAuth 2.0 credentials. |
| `OidcSettings.cs` | Generic OpenID Connect provider settings. |
| `DockerSettings.cs` | Docker connection defaults. |
| `MetricsSettings.cs` | Metric collection cadence / retention; `ScrapeToken` gates the Prometheus `/metrics` endpoint (disabled when unset). |
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
