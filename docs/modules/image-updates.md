# Module: image-updates

Image-update checking + opt-in auto-update — one module for both. A background checker polls container
registries for newer image digests; the opt-in auto-updater pulls + recreates containers that have an update.

| | |
|---|---|
| **Id** | `image-updates` |
| **Enabled by default** | yes |
| **Toggle** | `Features:image-updates:Enabled` (env `Features__image-updates__Enabled=false`) — restart required |
| **Depends on** | — (soft dependency on Core via `NoopImageUpdateStore`) |
| **Nav** | — (pending updates surface on the Dashboard) |
| **MCP tools** | — of its own; `check_updates` / `update_container` stay in the Core, mixed `ContainerTools` |
| **Services** | `IImageUpdateStore`, `IRegistryClient`, hosted `ImageUpdateChecker` + `IAutoUpdateService` |

When **disabled**: the checker + auto-updater don't run, the Dashboard shows no pending updates, and the
Image-Updates panel in *Settings* is hidden.

**Soft dependency:** the Dashboard and the Core `ContainerTools` (`check_updates`/`update_container`) read
`IImageUpdateStore`, so Core keeps a `NoopImageUpdateStore` default for when the module is off (the store then
holds nothing).

Settings: the Image-Updates panel (`ImageUpdate` config section) — enable checking, interval, notify-on-update.

**Note:** the optional changeme C12 (auto-update rollback) is deferred; this module PR is a byte-identical
extraction.

Code: [`src/Whiskers/Modules/ImageUpdate/`](../../src/Whiskers/Modules/ImageUpdate/) · services in
[`Services/ImageUpdate`](../../src/Whiskers/Services/ImageUpdate/),
[`Services/AutoUpdate`](../../src/Whiskers/Services/AutoUpdate/) · update tools in Core
[`ContainerTools.cs`](../../src/Whiskers/Mcp/Tools/ContainerTools.cs).
