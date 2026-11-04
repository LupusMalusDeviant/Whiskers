# Codebase map

A one-page orientation: what lives where, how big it is, how a call travels through the code, and where to start
for a typical change. The folder READMEs hold the detail; this page links them together.

Sizes are `.cs` + `.razor` lines as of November 2026 (rounded), so read them as proportions, not as exact numbers.

## Projects

| Project | Files | Lines | What |
|---|---:|---:|---|
| [`src/Whiskers`](../src/Whiskers/) | 670 | 92 k | The app: Blazor Server UI, MCP server, services, background loops |
| [`src/Whiskers.Data`](../src/Whiskers.Data/) | 18 | 1 k | EF Core entities and the two DbContexts (`MetricsDbContext`, `WhiskersIdentityDbContext`); its own assembly so the migration projects can reference it ([ADR-0004](adr/0004-postgres-provider-support.md)) |
| [`src/Whiskers.Migrations.Sqlite`](../src/Whiskers.Migrations.Sqlite/) / [`.Postgres`](../src/Whiskers.Migrations.Postgres/) | 38 + 38 | 22 k + 23 k | Generated EF migrations, one set per provider, additive only. Never edited by hand |
| [`src/Whiskers.Tests`](../src/Whiskers.Tests/) | 285 | 72 k | xUnit + bUnit; about 4,800 tests, including architecture tests that pin the security rules below |

## The app by area

| Area | Files | Lines | Start reading |
|---|---:|---:|---|
| [`Services/`](../src/Whiskers/Services/) | 429 | 61 k | [Services tour](../src/Whiskers/Services/README.md) |
| [`Components/`](../src/Whiskers/Components/) (Pages 56, Shared 17, Layout 5) | 81 | 19 k | [Pages](../src/Whiskers/Components/Pages/README.md) |
| [`Mcp/`](../src/Whiskers/Mcp/) (Tools 23, Prompts, Resources) | 35 | 5 k | [Mcp](../src/Whiskers/Mcp/README.md), [tool catalog](mcp-tool-catalog.md) |
| [`Startup/`](../src/Whiskers/Startup/) + `Program.cs` | 9 | 2 k | [Startup](../src/Whiskers/Startup/README.md) |
| [`Models/`](../src/Whiskers/Models/) | 37 | 2 k | [Models](../src/Whiskers/Models/README.md) |
| [`Configuration/`](../src/Whiskers/Configuration/) | 35 | 2 k | [Configuration](../src/Whiskers/Configuration/README.md) |
| [`Modules/`](../src/Whiskers/Modules/) | 23 | 1 k | [Modules](../src/Whiskers/Modules/README.md), [docs/modules](modules/) |
| [`Utils/`](../src/Whiskers/Utils/), [`Hubs/`](../src/Whiskers/Hubs/), [`HealthChecks/`](../src/Whiskers/HealthChecks/) | 20 | 2 k | their READMEs |

The biggest service folders: `Mutations` (8 k lines, the gateway), `Agent` (4 k), `Notifications` (4 k),
`Backup` (4 k), `Docker` (3.5 k), `Metrics` (3 k), `Auth` (2.3 k), `Observability` (2.2 k).

## How a call flows

Every change to infrastructure takes the same path, whoever asks for it. Read in this order:

1. **Entry point.**
   - An MCP client calls `tools/call`. [`McpBearerAuthMiddleware`](../src/Whiskers/Mcp/McpBearerAuthMiddleware.cs) authenticates the key, then [`McpGuardrailFilter`](../src/Whiskers/Mcp/McpGuardrailFilter.cs) takes the call.
   - The in-process agent goes through [`AgentToolInvoker`](../src/Whiskers/Services/Agent/AgentToolInvoker.cs).
   - A page goes through [`IUiMutationRunner`](../src/Whiskers/Services/Mutations/UiMutationRunner.cs).
   - Scheduled tasks, AutoUpdate and AutoPatch go through [`IAutomationMutationRunner`](../src/Whiskers/Services/Mutations/AutomationMutationRunner.cs).
2. **Decision.** [`MutationGateway`](../src/Whiskers/Services/Mutations/MutationGateway.cs) maps the action to a capability ([`CapabilityCatalog`](../src/Whiskers/Services/Mutations/CapabilityCatalog.cs)), resolves the target, and asks the [guardrail engine](../src/Whiskers/Services/Guardrails/) for Allow, Confirm or Deny. A Confirm becomes an approval ([`Services/Approvals`](../src/Whiskers/Services/Approvals/)). A read-only MCP tool passes [`DirectMcpCallGuard`](../src/Whiskers/Services/Guardrails/DirectMcpCallGuard.cs) instead.
3. **Execution.** The delegate calls a service, which reaches the host through one of two paths:
   - [`DockerConnectionManager`](../src/Whiskers/Services/Docker/DockerConnectionManager.cs): Docker API, local socket or TCP + mTLS.
   - [`HostCommandExecutor`](../src/Whiskers/Services/Server/HostCommandExecutor.cs): the shell plane, nsenter over the same mTLS channel, or legacy SSH.

   A service method that changes something carries `[MutatingOperation]`, and the scope guard refuses it outside a gateway decision.
4. **Record.** The gateway writes the decision and its outcome. The audit log is a SHA-256 hash chain ([`AuditChain`](../src/Whiskers/Services/AuditLog/AuditChain.cs)), signed when `VAULT_KEY` is set. MCP calls also land in the Agent History.

Background work runs in 17 loops derived from [`FleetBackgroundService`](../src/Whiskers/Services/FleetBackgroundService.cs):
- metrics, container health, CVE, image updates, log monitor and log hygiene;
- certificates, scheduler, AutoUpdate and AutoPatch;
- the notification outbox, audit recovery, interrupted-work reconciliation and suspension reminders;
- the scan supervisor, self-metrics and the outcome sweeper.

Each loop reports to [self-metrics](../src/Whiskers/Services/Observability/SelfMetrics/), which feed the self-status page.

## Where state lives

| What | Where |
|---|---|
| Metrics, audit, approvals, decisions, history, rules | `MetricsDbContext` (SQLite by default, PostgreSQL optional) |
| Local accounts | `WhiskersIdentityDbContext` (own migration history table) |
| Servers, registries, settings files | JSON stores under the data directory ([`JsonFileStore`](../src/Whiskers/Services/Persistence/JsonFileStore.cs)) |
| Secrets (API tokens, kubeconfigs, keys) | [`VaultService`](../src/Whiskers/Services/Vault/), encrypted with `VAULT_KEY` |
| Everything above, on disk | one data directory (`WHISKERS_DATA_DIR`, default `/app/data`); backed up by [self-backup](../src/Whiskers/Services/Backup/) |

## Where to start for a typical change

| Change | Start at |
|---|---|
| A new MCP tool | [`Mcp/Tools/`](../src/Whiskers/Mcp/Tools/README.md), its level in `McpToolLevelCatalog`; if it changes something, a capability in `CapabilityCatalog`. The catalog doc regenerates (`McpToolCatalogSnapshotTests`) |
| A new page action that changes something | `IUiMutationRunner.RunAsync` with a capability id, `RequireAsync` in front; `UiAuditCompletenessTests` and `MutationGatewayCoverageTests` must list the new file or handler |
| A new optional feature | Copy [`Modules/HelloWorld`](../src/Whiskers/Modules/HelloWorld/), add it to `ModuleCatalog` |
| A guardrail rule | [`Services/Guardrails`](../src/Whiskers/Services/Guardrails/README.md) (`BuiltInGuardrailRules`, `GuardrailRuleCatalog`) |
| A new table or column | `Whiskers.Data` entity, then one migration per provider (additive only) |
| A user-facing string | `Resources/SharedResource.resx` (English) and `.de.resx` (German) |

## Protected areas

Changed only with an explicit, recorded decision (see [ARCHITECTURE.md](ARCHITECTURE.md#security-critical-areas-and-change-control)):

- the authentication middleware and its order ([`Startup/WhiskersPipelineExtensions.cs`](../src/Whiskers/Startup/WhiskersPipelineExtensions.cs), [`WhiskersAuthenticationExtensions.cs`](../src/Whiskers/Startup/WhiskersAuthenticationExtensions.cs));
- the OAuth email whitelist;
- the mTLS/WireGuard layer ([`Services/Certificates`](../src/Whiskers/Services/Certificates/), `HostCommandExecutor`);
- the docker-socket-proxy verb whitelist.

## Hotspots

The largest files, i.e. the first candidates when something gets hard to change:

| File | Lines | Note |
|---|---:|---|
| `Components/Pages/ContainerDetail.razor` | 2,010 | One page with eight tabs; the database tab is being moved into its own component |
| `Components/Pages/Settings.razor` | 1,541 | Holds the auth/whitelist settings; protected area |
| `Components/Pages/Dashboard.razor` | 1,123 | |
| `Services/Webhooks/WebhookService.cs` | 1,113 | CRUD, queue, run execution and suspension in one class |
| `Services/AuditLog/AuditChain.cs` | 1,024 | Security core; long because of its proofs and comments |
| `Components/Pages/Servers.razor` | 958 | |
| `Services/Mutations/MutationGateway.cs` | 932 | Security core |
| `Services/Backup/BackupService.cs` | 888 | |
| `Services/Metrics/MetricsCollectorService.cs` | 870 | |
| `Startup/WhiskersPipelineExtensions.cs` | 864 | Middleware order; protected area |
