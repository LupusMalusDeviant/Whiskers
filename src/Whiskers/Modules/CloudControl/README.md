# Modules/CloudControl

Out-of-band cloud control (RoadToSAP Phase 1, §3 item 6): power actions (reboot / shutdown / hard-reset /
power-on) and snapshots on cloud servers via provider APIs — Hetzner and Hostinger — plus the `/cloud` page and
the cloud/Hetzner MCP tools.

- `CloudControlModule.cs` — `Id = "cloud-control"`, enabled by default. `ConfigureServices` (moved **verbatim**
  from `Program.cs`) registers the two provider HTTP clients (`IHetznerService` → `HetznerApiService`,
  `IHostingerService` → `HostingerApiService`, each with a rotating primary handler) and
  `ICloudControlService` → `CloudControlService` (the provider-agnostic dispatcher). Nav: the `cloud` entry
  (group *Infrastruktur*). MCP tools: `CloudTools` (`cloud_*`) + `HetznerTools` (`hetzner_*`), both dedicated.

**Toggle:** `Features:cloud-control:Enabled` (`Features__cloud-control__Enabled=false`), restart-only. When off,
the `cloud` nav + MCP tools disappear and `/cloud` shows a "module disabled" notice.

**Clean extraction (no no-ops).** Nothing in Core consumes these services — only the module's own page,
the two dedicated MCP tool classes, and `CloudControlService` itself. So no no-op defaults are needed; the
`/cloud` page uses the thin route-wrapper + `ModuleGuard` pattern
([`Cloud.razor`](../../Components/Pages/Cloud.razor) → [`CloudView.razor`](../../Components/Pages/CloudView.razor))
so a disabled module never instantiates the `ICloudControlService` injection. Per-server cloud credentials
(provider + API key) are configured under *Server*, not here.

**Deferred (§3.6 assigns C10).** The `ICloudProvider` seam — making Hetzner/Hostinger pluggable providers
behind a common contract (the `IVpnProvider` reference pattern), so `CloudControlService` dispatches over a
provider set instead of a hard-wired switch — is **not** part of this PR. It refactors destructive
power/snapshot dispatch and wants focused, carefully-verified work; the extraction here is byte-identical.
Provider code stays in [`../../Services/Cloud/`](../../Services/Cloud/),
[`../../Services/Hetzner/`](../../Services/Hetzner/), [`../../Services/Hostinger/`](../../Services/Hostinger/).

See [RoadToSAP](../../../../docs/roadmap/RoadToSAP.md) and
[`docs/modules/cloud-control.md`](../../../../docs/modules/cloud-control.md).
