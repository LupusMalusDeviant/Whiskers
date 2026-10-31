# Configuration

Strongly-typed settings classes bound from configuration (env vars / `.env` / `appsettings` / the writable `agent-settings.json` and `app-settings.json`). Each maps to a section and is injected via `IOptions<T>` / `IOptionsMonitor<T>`. Binding is wired in [`Program.cs`](../Program.cs); the env-var names are documented in [`.env.example`](../../../.env.example). UI edits are written to `app-settings.json` (the last config layer, reload-on-change) by [`../Services/Persistence/AppSettingsStore.cs`](../Services/Persistence/AppSettingsStore.cs) so they apply live.

**`DataPathOptions`** is the one exception to the `IOptions<T>` pattern below: it centralizes every path under the data directory (`WHISKERS_DATA_DIR`, default `/app/data`) and is built at **bootstrap** — before the DI container exists — then registered as a plain singleton and injected directly. Consumers take it as an optional last constructor parameter (the container supplies the registered instance; an explicit path still wins, which keeps the test seams working).

## Files

| File | Section / purpose |
|---|---|
| `SecretSource.cs` | Reads a secret setting (R1-28): `NAME_FILE` (a Docker/Kubernetes secret file) wins over `NAME`; a configured file that is missing, unreadable or empty throws `SecretFileException` and stops the start. Used for `VAULT_KEY` (vault, backup encryption, audit chain) and `WHISKERS_DB_CONNECTION`. |
| `DataPathOptions.cs` | Central resolver for all data-directory paths (DB, JSON stores, DataProtection keys, `ssh-keys/`, `mtls/`, `backups/`). Root = `WHISKERS_DATA_DIR` (default `/app/data`). Built at bootstrap, injected as a plain singleton — **not** via `IOptions<T>`. |
| `ServiceCollectionExtensions.cs` | DI registration helpers (RoadToSAP Phase 0), **not** a settings class: `AddSingletonWithInterface[AndHostedService]` collapse the concrete + interface-forwarder [+ hosted] idiom into one call; `AddInitializable<T>` registers a startup [`IInitializable`](../Services/IInitializable.cs) (unwrapping a mutation scope guard). |
| `GoogleAuthSettings.cs` | Google OAuth 2.0 credentials. |
| `OidcSettings.cs` | Generic OpenID Connect provider settings. |
| `DockerSettings.cs` | Docker connection defaults. |
| `AuditSettings.cs` | Section `Audit` (R1-23): `RetentionDays` for the audit log and MCP call history (default 365, never below 30). |
| `MetricsSettings.cs` | Metric collection cadence / retention; `ScrapeToken` gates the Prometheus `/metrics` endpoint (disabled when unset). |
| `HealthMonitorSettings.cs` | Health-monitor thresholds. |
| `ImageUpdateSettings.cs` | Image-update check cadence. |
| `AutoPatchSettings.cs` | Nightly security auto-patching (section `AutoPatch`): on/off, dry run, window hour, severity, limit, exclusions. |
| `CveMonitorSettings.cs` | CVE scan cadence. |
| `MattermostSettings.cs` / `MatrixSettings.cs` | Notification channel settings. |
| `TerminalSettings.cs` | Web-terminal settings. |
| `AiChatSettings.cs` | Read-only advisor chat (provider, model, key). |
| `AgentSettings.cs` | Acting-agent provider access + system prompt (UI-editable, reload-on-change). |
| `WhiskersPublicUrl.cs` | The configured address people open Whiskers at (`Whiskers:PublicUrl`, env `WHISKERS_PUBLIC_URL` in compose; R1-15). The only source of absolute links in outbound notifications (approval links) — never a request's host; an invalid value (not absolute http(s), credentials, query or fragment) is ignored. |
| `GuardrailPolicy.cs` | A single guardrail policy (restrictive-by-default), incl. the per-policy tool mode (deny/allow) and `RequireSecondApprover` (R1-15, PC-D4: nobody decides their own request, not even in the chat; optional in `guardrails.json`, off by default and left out of the stored policy while off). |
| `GuardrailConfig.cs` | The multi-preset wrapper: several named `GuardrailPreset`s + the active one (persisted in admin-only `guardrails.json`). `ActivePolicy()` never falls back to another preset: an active preset that does not exist yields `GuardrailPolicy.FailClosed()` (R1-12). |
| `MetricAlertSettings.cs` | Thresholds for `high_cpu` / `high_memory` / `metric_anomaly` events (drive AI triggers). |

## Related

- Env-var reference: [`../../../.env.example`](../../../.env.example)
- Agent settings store: [`../Services/Agent/AgentSettingsStore.cs`](../Services/Agent/AgentSettingsStore.cs)
- Guardrail store: [`../Services/Guardrails/`](../Services/Guardrails/)
