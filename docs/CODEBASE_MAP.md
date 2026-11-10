# Codebase map

A one-page orientation: what lives where, how big it is, how a call travels through the code, and where to start
for a typical change. The folder READMEs hold the detail; this page links them together.

Sizes are `.cs` + `.razor` lines as of November 2026 (rounded), so read them as proportions, not as exact numbers.

## Projects

| Project | Files | Lines | What |
|---|---:|---:|---|
| [`src/Whiskers`](../src/Whiskers/) | 715 | 105 k | The app: Blazor Server UI, MCP server, services, background loops |
| [`src/Whiskers.Data`](../src/Whiskers.Data/) | 19 | 1 k | EF Core entities and the two DbContexts (`MetricsDbContext`, `WhiskersIdentityDbContext`); its own assembly so the migration projects can reference it ([ADR-0004](adr/0004-postgres-provider-support.md)) |
| [`src/Whiskers.Migrations.Sqlite`](../src/Whiskers.Migrations.Sqlite/) / [`.Postgres`](../src/Whiskers.Migrations.Postgres/) | 40 + 40 | 24 k + 25 k | Generated EF migrations, one set per provider, additive only. Never edited by hand |
| [`src/Whiskers.Tests`](../src/Whiskers.Tests/) | 353 | 100 k | xUnit + bUnit; the main CI run of 8 November 2026 ran 7,000 in-process test cases and 134 Docker integration tests, including architecture tests that pin the security rules below |

## The app by area

| Area | Files | Lines | Start reading |
|---|---:|---:|---|
| [`Services/`](../src/Whiskers/Services/) | 463 | 72 k | [Services tour](../src/Whiskers/Services/README.md) |
| [`Components/`](../src/Whiskers/Components/) (Pages 59, Shared 20, Layout 5) | 88 | 20 k | [Pages](../src/Whiskers/Components/Pages/README.md) |
| [`Mcp/`](../src/Whiskers/Mcp/) (23 tool classes, Prompts, Resources) | 36 | 5 k | [Mcp](../src/Whiskers/Mcp/README.md), [tool catalog](mcp-tool-catalog.md) |
| [`Startup/`](../src/Whiskers/Startup/) + `Program.cs` | 9 | 2 k | [Startup](../src/Whiskers/Startup/README.md) |
| [`Models/`](../src/Whiskers/Models/) | 37 | 2 k | [Models](../src/Whiskers/Models/README.md) |
| [`Configuration/`](../src/Whiskers/Configuration/) | 35 | 2 k | [Configuration](../src/Whiskers/Configuration/README.md) |
| [`Modules/`](../src/Whiskers/Modules/) | 23 | 1 k | [Modules](../src/Whiskers/Modules/README.md), [docs/modules](modules/) |
| [`Utils/`](../src/Whiskers/Utils/), [`Hubs/`](../src/Whiskers/Hubs/), [`HealthChecks/`](../src/Whiskers/HealthChecks/) | 23 | 2 k | their READMEs |

The biggest service folders: `Mutations` (11 k lines, the gateway), `Metrics` (4.5 k), `Auth` (4 k), `Agent` (4 k),
`Docker` (4 k), `Notifications` (3.8 k), `Backup` (3.7 k), `Certificates` (3 k).

## How a call flows

Every change to infrastructure takes the same path, whoever asks for it. Read in this order:

1. **Entry point.**
   - An MCP client calls `tools/call` on `/mcp`. [`McpBearerAuthMiddleware`](../src/Whiskers/Mcp/McpBearerAuthMiddleware.cs) authenticates a valid API key from the `Authorization: Bearer` header. The `/mcp` authorization policy (`MapMcp` in [`WhiskersPipelineExtensions`](../src/Whiskers/Startup/WhiskersPipelineExtensions.cs)) admits that key or, when no bearer header is sent, a signed-in web session. Then [`McpGuardrailFilter`](../src/Whiskers/Mcp/McpGuardrailFilter.cs) takes each tool call.
   - The in-process agent goes through [`AgentToolInvoker`](../src/Whiskers/Services/Agent/AgentToolInvoker.cs).
   - A page goes through [`IUiMutationRunner`](../src/Whiskers/Services/Mutations/UiMutationRunner.cs).
   - Unattended work goes through [`IAutomationMutationRunner`](../src/Whiskers/Services/Mutations/AutomationMutationRunner.cs): scheduled tasks, AutoUpdate, AutoPatch, webhooks, Git deploy and the VPN bring-up at start.
2. **Decision.** [`MutationGateway`](../src/Whiskers/Services/Mutations/MutationGateway.cs) maps the action to a capability ([`CapabilityCatalog`](../src/Whiskers/Services/Mutations/CapabilityCatalog.cs)), resolves the target, and asks the [guardrail engine](../src/Whiskers/Services/Guardrails/) for Allow, Confirm or Deny. A Confirm becomes an approval ([`Services/Approvals`](../src/Whiskers/Services/Approvals/)). A direct MCP call of a read-only tool passes [`DirectMcpCallGuard`](../src/Whiskers/Services/Guardrails/DirectMcpCallGuard.cs) inside `McpGuardrailFilter` instead; the agent's reads are checked in `AgentToolInvoker`.
3. **Execution.** The delegate calls a service, which reaches the target through one of three paths:
   - [`DockerConnectionManager`](../src/Whiskers/Services/Docker/DockerConnectionManager.cs): the Docker API over the local socket, TCP (mTLS when a client certificate is configured, otherwise plain or TLS) or an SSH tunnel.
   - [`HostCommandExecutor`](../src/Whiskers/Services/Server/HostCommandExecutor.cs): the shell plane, nsenter over the same mTLS channel, or legacy SSH.
   - Kubernetes servers, which the connection manager refuses, through the workload seam ([`Services/Workloads`](../src/Whiskers/Services/Workloads/), `KubernetesWorkloadProvider`).

   A service method that changes something carries `[MutatingOperation]`, and the scope guard refuses it outside a gateway decision.
4. **Record.** The gateway writes the decision and its outcome. The audit log is a SHA-256 hash chain ([`AuditChain`](../src/Whiskers/Services/AuditLog/AuditChain.cs)), signed when `VAULT_KEY` is set. MCP calls also land in the Agent History.

Background work runs in 19 loops derived from [`FleetBackgroundService`](../src/Whiskers/Services/FleetBackgroundService.cs)
(some only when their module is enabled):
- metrics, disk health, container health, CVE, image updates, log monitor and log hygiene;
- certificates, endpoint checks, scheduler, AutoUpdate and AutoPatch;
- the notification outbox, audit recovery, interrupted-work reconciliation and suspension reminders;
- the scan supervisor, the self-metrics recorder and the outcome sweeper.

Next to them run plain hosted services that are not fleet loops: two on a timer, `AuditCheckpointService` (signs the
end of the audit chain once a minute and at shutdown) and `HubSessionSweep` (re-checks the open `ContainerHub`
connections every 15 seconds); `AuthAuditLog`,
which writes queued sign-in audit rows; and one-shot start or stop steps (VPN bring-up, interval and outbound-target
checks, `ShutdownDrain`).

Ten loops record their cycles in [self-metrics](../src/Whiskers/Services/Observability/SelfMetrics/), which feed the
self-status page: metrics, container health, CVE, image updates, log monitor, log hygiene, certificates, scheduler,
AutoUpdate and the outcome sweeper. The scan supervisor reads those counters; the self-metrics recorder saves them to
the database and restores them at start. The other loops (disk health, endpoint checks, AutoPatch, the notification
outbox, audit recovery, interrupted-work reconciliation, suspension reminders) record no cycles, and the base class
reports nothing itself.

## Where state lives

| What | Where |
|---|---|
| Metrics, audit, approvals, decisions, history, log alert rules | `MetricsDbContext` (SQLite by default, PostgreSQL optional) |
| Local accounts | `WhiskersIdentityDbContext` (own migration history table) |
| Servers, registries, guardrail policy, whitelist, roles, MCP keys, AI triggers, settings | JSON stores under the data directory ([`JsonFileStore`](../src/Whiskers/Services/Persistence/JsonFileStore.cs)) |
| Secrets (API tokens, kubeconfigs, keys) | [`VaultService`](../src/Whiskers/Services/Vault/), encrypted with `VAULT_KEY` |
| Everything above, on disk | the data directory (`WHISKERS_DATA_DIR`, default `/app/data`), plus the PostgreSQL database when that provider is configured; backed up by [self-backup](../src/Whiskers/Services/Backup/) |

## Where to start for a typical change

| Change | Start at |
|---|---|
| A new MCP tool | [`Mcp/Tools/`](../src/Whiskers/Mcp/Tools/README.md): declare its level with `[McpToolLevel]` and add it to `McpPermissionLevels.DefaultToolLevels`; if it changes something, add its capability to `CapabilityCatalog` first. `McpToolCatalogSnapshotTests` then fails and writes `docs/mcp-tool-catalog.md.actual`; review it and move it into place |
| A new page action that changes something | `IUiMutationRunner.RunAsync` with a capability id, behind the page's own `RequireAsync` role check; `UiAuditCompletenessTests` and `MutationGatewayCoverageTests` must list the new file or handler |
| A new optional feature | Copy [`Modules/HelloWorld`](../src/Whiskers/Modules/HelloWorld/) and its pages `Components/Pages/HelloWorld*.razor`, add it to `ModuleCatalog` |
| A guardrail rule | [`Services/Guardrails`](../src/Whiskers/Services/Guardrails/README.md) (`BuiltInGuardrailRules`, `GuardrailRuleCatalog`) |
| A new table or column | `Whiskers.Data` entity, then one migration per provider (additive only) |
| A user-facing string | `Resources/SharedResource.resx` (English) and `.de.resx` (German) |

## Protected areas

Changed only with an explicit, recorded decision (see [ARCHITECTURE.md](ARCHITECTURE.md#security-critical-areas-and-change-control)):

- the authentication middleware and its order ([`Startup/WhiskersPipelineExtensions.cs`](../src/Whiskers/Startup/WhiskersPipelineExtensions.cs), [`WhiskersAuthenticationExtensions.cs`](../src/Whiskers/Startup/WhiskersAuthenticationExtensions.cs));
- the OAuth email whitelist;
- the mTLS/WireGuard layer: certificates, the allowed client CN and the mesh binding ([`Services/Certificates`](../src/Whiskers/Services/Certificates/), the mTLS credentials in `DockerConnectionManager`, [`Services/Vpn`](../src/Whiskers/Services/Vpn/), and the host side in `DockerProxyCompose` of [`Services/Onboarding/OnboardingCommands.cs`](../src/Whiskers/Services/Onboarding/OnboardingCommands.cs): ghostunnel's `--allow-cn` and its port bound to the host's mesh IP);
- the docker-socket-proxy verb whitelist (the `socket-proxy` `environment` block in the same `DockerProxyCompose`).

Command and query execution on managed hosts (`HostCommandExecutor` with nsenter, mTLS and legacy SSH, the
`execute_command` / `execute_query` tools and `DatabaseService`) is one of the security-critical areas listed in the
same section: changes there get a dedicated review and tests that pin the behaviour.

## Hotspots

The largest files, i.e. the first candidates when something gets hard to change:

| File | Lines | Note |
|---|---:|---|
| `Services/Webhooks/WebhookService.cs` | 1,651 | CRUD, queue, run execution and suspension in one class |
| `Components/Pages/Settings.razor` | 1,585 | Hosts the whitelist UI; changes to whitelist behaviour are protected |
| `Components/Pages/Dashboard.razor` | 1,150 | |
| `Components/Pages/Servers.razor` | 1,077 | |
| `Services/Mutations/MutationGateway.cs` | 1,050 | Security core |
| `Components/Pages/ContainerDatabasePanel.razor` | 1,041 | The database tab of the container page, moved out of `ContainerDetail.razor` |
| `Components/Pages/ContainerDetail.razor` | 1,033 | One page with eight tabs |
| `Services/AuditLog/AuditChain.cs` | 1,024 | Security core; long because of its proofs and comments |
| `Services/Auth/IdentityBindingStore.cs` | 997 | Account bindings shared with the host CLI through a file lock |
| `Startup/WhiskersPipelineExtensions.cs` | 943 | Middleware order; protected area |
