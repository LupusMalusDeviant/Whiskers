# Configuration

Strongly-typed settings classes bound from configuration (env vars / `.env` / `appsettings` / the writable `agent-settings.json`). Each maps to a section and is injected via `IOptions<T>` / `IOptionsMonitor<T>`. Binding is wired in [`Program.cs`](../Program.cs); the env-var names are documented in [`.env.example`](../../../.env.example).

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
| `CoolifySettings.cs` | Coolify connection defaults. |
| `TerminalSettings.cs` | Web-terminal settings. |
| `AiChatSettings.cs` | Read-only advisor chat (provider, model, key). |
| `AgentSettings.cs` | Acting-agent provider access (UI-editable, reload-on-change). |
| `GuardrailPolicy.cs` | The agent's configurable, restrictive-by-default security policy (persisted in admin-only `guardrails.json`). |

## Related

- Env-var reference: [`../../../.env.example`](../../../.env.example)
- Agent settings store: [`../Services/Agent/AgentSettingsStore.cs`](../Services/Agent/AgentSettingsStore.cs)
- Guardrail store: [`../Services/Agent/Guardrails/`](../Services/Agent/Guardrails/)
