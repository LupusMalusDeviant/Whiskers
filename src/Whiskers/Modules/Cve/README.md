# Modules/Cve

CVE monitoring (RoadToSAP Phase 1, §3 item 5): a background scanner (Trivy for container images + apt for the
host OS), the findings store, the CVE-age store, the `/cves` page and the CVE MCP tools.

- `CveModule.cs` — `Id = "cve"`, enabled by default. `ConfigureServices` (moved **verbatim** from `Program.cs`)
  binds `CveMonitorSettings` and registers `ICveFindingsStore`, `ICveAgeStore`, `IOsCveScanner`,
  `ITrivyScanner` and the hosted `CveMonitorService` (singleton + `ICveMonitorService` + `IHostedService`).
  Nav: the `cves` entry ("CVE-Monitor", group *Übersicht*). MCP tools: `CveTools` (dedicated, so it moves with
  the module).

**Toggle:** `Features:cve:Enabled` (`Features__cve__Enabled=false`), restart-only. When off, the scanner
doesn't run, the `cves` nav + MCP tools disappear, `/cves` shows a "module disabled" notice, and the CVE panel
in *Settings* is hidden.

**Soft dependency (no-ops).** `ICveFindingsStore` + `ICveMonitorService` are consumed by **Core** pages —
`Dashboard` and `ContainerDetail` inject the findings store (for CVE counts/badges), `Settings` injects both.
So Core registers [`NoopCveFindingsStore` + `NoopCveMonitorService` + `NoopCveAgeStore`](../../Services/Cve/NoopCveServices.cs)
before the module loop (the age-store no-op is only to keep the inline-gated `/cves` page's injection safe);
the real services win by last-registration when enabled. With the module off, those Core pages simply show no
CVE data. The `/cves` page gates its content inline with `<ModuleGuard ModuleId="cve">`, and the Settings CVE
panel is gated with `@if IsEnabled("cve")`.

**Deferred:** the changeme **C8** refactor (removing the `IServiceProvider` service-locator in
`CveMonitorService` in favour of constructor injection) is **not** part of this PR — the extraction is
byte-identical, and C8 is a separate, focused clean-up (the 8 resolved deps are all singletons with no DI
cycle, so it's safe but wants a proper rename rather than an alias workaround).

Service code stays in [`../../Services/Cve/`](../../Services/Cve/), tools in
[`../../Mcp/Tools/CveTools.cs`](../../Mcp/Tools/CveTools.cs).

See [RoadToSAP](../../../../docs/roadmap/RoadToSAP.md) and [`docs/modules/cve.md`](../../../../docs/modules/cve.md).
