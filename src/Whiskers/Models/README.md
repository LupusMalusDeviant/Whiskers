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
| `NotificationEvent.cs` | A notification payload. `ApprovalId` (R1-15) makes an approval notification open the approval's card. |
| `ContainerNotificationPrefs.cs` | Per-container notification preferences. |
| `VolumeBackup.cs` | A volume backup record. |
| `AuditLogEntry.cs` | An audit-trail entry. |
| `McpPermission.cs` | MCP permission levels + the tool > level map (`DefaultToolLevels`): read tools as declared by `[McpToolLevel]`; mutating tools take their level from the capability catalog ([`../Services/Mutations/CapabilityCatalog.cs`](../Services/Mutations/CapabilityCatalog.cs), R1-05), which is the one table to change. |
| `UserRole.cs` / `WhitelistData.cs` | Roles and the email whitelist. |
| `VaultEntry.cs` | An encrypted vault entry. |
| `AiTrigger.cs` | An AI trigger (events, name filter, prompt, guardrail preset, cooldown, and since R1-13 its write grant — `WriteCapabilities` + `WriteTargets` — with `OwnerId` and `SuspendedReason`, which only the store sets; no grant = read-only) + the `AiTriggerEvents` catalog of event types. |
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
