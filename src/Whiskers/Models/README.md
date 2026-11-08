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
| `NotificationEvent.cs` | A notification payload. `ApprovalId` (R1-15) makes an approval notification open the approval's card. R1-40: `DeliveryId` (one identity for every retry and redelivery), `IsRedelivery` (set by the outbox worker; not throttled again) and `DeliveredRecipients` (the e-mail recipients already reached after a partial SMTP delivery; replaced, never changed in place, so `Copy()` stays shallow). |
| `ContainerNotificationPrefs.cs` | Per-container notification preferences. |
| `VolumeBackup.cs` | A volume backup record. |
| `AuditLogEntry.cs` | An audit-trail entry. |
| `McpPermission.cs` | MCP permission levels + the tool > level map (`DefaultToolLevels`): read tools as declared by `[McpToolLevel]`; mutating tools take their level from the capability catalog ([`../Services/Mutations/CapabilityCatalog.cs`](../Services/Mutations/CapabilityCatalog.cs), R1-05), which is the one table to change. |
| `UserRole.cs` / `WhitelistData.cs` | Roles and the email whitelist. |
| `VaultEntry.cs` | An encrypted vault entry. |
| `AiTrigger.cs` | An AI trigger (events, name filter, prompt, guardrail preset, cooldown, and since R1-13 its write grant — `WriteCapabilities` + `WriteTargets` — with `OwnerId` and `SuspendedReason`, which only the store sets; no grant = read-only) + the `AiTriggerEvents` catalog of event types. |
| `InAppNotification.cs` | A notification shown in the in-app bell/feed (title, detail, severity). |
| `ContainerLogConfiguration.cs` | One container's Docker log driver, its `max-size` / `max-file` and the log file path (Plan-0007). |
| `FleetContainerListing.cs` | A fleet-wide container list plus which servers answered and which failed (`FleetServerFailure`). |
| `GitDeployApp.cs` | A git-based deployment (repo cloned on the target, brought up with compose); its token lives in the vault. |
| `RegistryConfig.cs` | A container registry for authenticated pulls; its credential lives in the vault. |

## Subfolders

| Folder | Contents |
|---|---|
| [`Agent/`](Agent/) | Agent runtime + provider-neutral DTOs (`AgentRuntime`, `AgentDtos`) |
| [`Cloud/`](Cloud/) | Cloud server info (provider-agnostic) |
| [`Cve/`](Cve/) | CVE findings, scan results, severity/source enums, summaries |
| [`Dns/`](Dns/) | Provider-neutral DNS record (`DnsRecord`) and the result of `set_dns_record` (`DnsSetResult`) |
| [`Help/`](Help/) | The in-app handbook's shape: `HelpChapter`, `HelpSection`, `HelpFigure` |
| [`Hetzner/`](Hetzner/) | Hetzner API response models |
| [`Hostinger/`](Hostinger/) | Hostinger API response models |

## Related

- Consumers: [`../Services/`](../Services/), [`../Mcp/`](../Mcp/), [`../Components/`](../Components/)
