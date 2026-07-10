# Modules/ImageUpdate

Image-update checking + opt-in auto-update (RoadToSAP Phase 1, §3 item 7) — **one** module for both halves of
the feature. The background `ImageUpdateChecker` polls registries for newer image digests into the update
store, and the opt-in `AutoUpdateService` applies them.

- `ImageUpdateModule.cs` — `Id = "image-updates"`, enabled by default. `ConfigureServices` (moved **verbatim**
  from `Program.cs`) binds `ImageUpdateSettings`, registers the `RegistryClient` typed HTTP client + its
  `IRegistryClient` forwarder, `IImageUpdateStore`, the hosted `ImageUpdateChecker`, and the hosted opt-in
  `AutoUpdateService` (`IAutoUpdateService`). **No nav entry and no MCP tools of its own.**

**Toggle:** `Features:image-updates:Enabled` (`Features__image-updates__Enabled=false`), restart-only. When
off, the checker + auto-updater don't run, the Dashboard shows no pending updates, and the Image-Updates panel
in *Settings* is hidden.

**Soft dependency (no-op).** `IImageUpdateStore` is consumed by **Core**: the Dashboard page (update counts)
and the `check_updates`/`update_container` MCP tools, which live in the mixed, Core-resident `ContainerTools`
(so they can't move). Core therefore registers a [`NoopImageUpdateStore`](../../Services/ImageUpdate/NoopImageUpdateStore.cs)
default before the module loop; the real `ImageUpdateStore` wins by last-registration when enabled. With the
module off the store simply holds nothing. `IRegistryClient` and `IAutoUpdateService` have no external
consumers (internal to the checker / a standalone hosted service), so they need no defaults.

**Deferred:** the optional changeme **C12** (auto-update rollback on a failed update) is a feature addition, not
part of this extraction; this PR is a byte-identical move.

Service code stays in [`../../Services/ImageUpdate/`](../../Services/ImageUpdate/) and
[`../../Services/AutoUpdate/`](../../Services/AutoUpdate/); the update MCP tools remain in Core
[`ContainerTools.cs`](../../Mcp/Tools/ContainerTools.cs).

See [RoadToSAP](../../../../docs/roadmap/RoadToSAP.md) and
[`docs/modules/image-updates.md`](../../../../docs/modules/image-updates.md).
