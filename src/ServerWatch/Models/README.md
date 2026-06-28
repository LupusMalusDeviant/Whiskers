# Models

Plain data models, DTOs, records and enums passed between services, the UI, and the MCP layer. No behaviour beyond simple helpers.

## Files

| File | Purpose |
|---|---|
| `ContainerInfo.cs` / `ContainerStats.cs` | Container metadata and live stats. |
| `ServerConfig.cs` | A configured Docker host (connection type, transport, metrics source). |
| `ServerSystemInfo.cs` | Host system info (OS, CPU, RAM, disk). |
| `HealthRecord.cs` | A point-in-time container health record. |
| `ImageUpdateInfo.cs` / `UpdatePolicy.cs` | Detected image updates and auto-update policy. |
| `NetworkInfo.cs` | Docker network metadata. |
| `DatabaseInfo.cs` | Detected in-container database info. |
| `DeploymentRequest.cs` | A container/Compose deployment request. |
| `AppTemplate.cs` | An app-store deployment template. |
| `LogAlertRule.cs` | A log-pattern alert rule. |
| `ScheduledTask.cs` | A scheduled task definition. |
| `NotificationEvent.cs` | A notification payload. |
| `ContainerNotificationPrefs.cs` | Per-container notification preferences. |
| `VolumeBackup.cs` | A volume backup record. |
| `AuditLogEntry.cs` | An audit-trail entry. |
| `McpPermission.cs` | MCP permission levels + the canonical tool→level map (`DefaultToolLevels`). |
| `UserRole.cs` / `WhitelistData.cs` | Roles and the email whitelist. |
| `VaultEntry.cs` | An encrypted vault entry. |
| `AiTrigger.cs` | An AI trigger (events, name filter, prompt, guardrail preset, cooldown) + the `AiTriggerEvents` catalog of event types. |
| `InAppNotification.cs` | A notification shown in the in-app bell/feed (title, detail, severity). |

## Subfolders

| Folder | Contents |
|---|---|
| [`Agent/`](Agent/) | Agent runtime + provider-neutral DTOs (`AgentRuntime`, `AgentDtos`) |
| [`Cloud/`](Cloud/) | Cloud server info (provider-agnostic) |
| [`Cve/`](Cve/) | CVE findings, scan results, severity/source enums, summaries |
| [`Hetzner/`](Hetzner/) | Hetzner API response models |
| [`Hostinger/`](Hostinger/) | Hostinger API response models |

## Related

- Consumers: [`../Services/`](../Services/), [`../Mcp/`](../Mcp/), [`../Components/`](../Components/)
