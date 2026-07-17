# Startup

The composition root, split out of `Program.cs` so it stays a thin orchestrator (RoadToSAP §6 DoD).
`Program.cs` is now ~50 lines: resolve data paths → optional `--migrate-to-postgres` CLI → **apply a pending
F3 self-restore** (`RestoreBootHandler`, the one step that must run *before* anything opens the DB or reads the
DataProtection key ring) → call these extension methods in order → `Build()` → configure the HTTP pipeline →
run startup → `Run()`. Every block was moved **verbatim** — same services, same middleware order; only the
location changed.

| File | Extension methods | What |
|---|---|---|
| `WhiskersHostingExtensions.cs` | `AddWhiskersConfiguration`, `AddWhiskersModules`, `AddWhiskersCoreServices`, `AddWhiskersUi` | UI-writable config layers + data-protection keys; the module pipeline (Core no-op defaults → each enabled module's `ConfigureServices` → MCP tools → nav registry); the remaining Core services (Docker, health, metrics, VPN, database, audit, startup initializers); UI (localization, MudBlazor, Blazor server components, SignalR). |
| `WhiskersAuthenticationExtensions.cs` | `AddWhiskersAuthentication` | **Security-sensitive — Off-Limits zone (CLAUDE.md, ADR-0002):** cookie session + optional Google/OIDC providers, the fail-open per-request whitelist re-check, or full LAN bypass. Isolated so the auth wiring is reviewable in one place. |
| `WhiskersPipelineExtensions.cs` | `ConfigureWhiskersHttpPipeline`, `RunWhiskersStartupAsync` | The HTTP request pipeline in its **fixed, security-critical order** (forwarded headers → path base → exception handler → localization → static files → antiforgery → **setup redirect (W1)** → **maintenance gate (F3)** → authentication → LAN bypass → MCP bearer → authorization → health/culture/auth/hub/MCP/webhook/metrics/self-backup-download endpoints → Razor components), then the startup warm-up (`IInitializable` loop in `Order` + metrics-DB init/migration + Identity migration & unattended admin seed). |
| `SetupRedirectPaths.cs` | — | Path/verb rules for the **W1 setup-redirect** gate, split out so they are unit-testable without a live pipeline: the anonymous-infra allowlist plus `IsHtmlNavigation` (only genuine top-level HTML GETs are funneled to `/setup`, which is load-bearing — `MapStaticAssets` serves *fingerprinted* assets at the root, so no prefix allowlist could catch them). |
| `MaintenancePaths.cs` | — | The same shape for the **F3 maintenance** gate: its own exempt allowlist (a separate list, so the two gates stay independent), reusing `SetupRedirectPaths.IsHtmlNavigation`. |

**Ordering guarantees preserved:** the module no-op defaults are still registered *before* the module loop
(last-registration wins); the auth middleware chain keeps its exact order; the `IInitializable` warm-ups still
run sorted by `Order`. The block-call order in `Program.cs` differs slightly from the old inline layout, but
that is DI-neutral — there are no cross-block duplicate service types, and the only multi-registration
`IEnumerable`s (`IInitializable`, `IVpnProvider`) are either order-independent or sorted at use.

Verified behaviour-neutral: build + full tests + the `BootMatrixTests` (all-on / only-core / example-on) +
a real Development boot in **both** auth modes (LAN bypass: pages render; real auth: protected pages redirect
to `/login`, `/login` + `/healthz` stay anonymous).
