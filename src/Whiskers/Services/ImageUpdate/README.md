# Services/ImageUpdate

Detects when a newer image is available for a running container by comparing the running image's digest against the current digest in its registry. Results feed the dashboard's update badges and the one-click update flow.

## Files

| File | Purpose |
|---|---|
| `ImageUpdateChecker.cs` | Background checker: for each container, resolves its registry reference and compares digests to detect available updates (skips digest-pinned refs; notifies once per new update). |
| `IRegistryClient.cs` / `RegistryClient.cs` | Queries a container registry for the current remote image digest; includes the image-reference parser and a registry-agnostic bearer-token flow (parses the `WWW-Authenticate` 401 challenge) so GHCR/Quay/LSCR work, not just Docker Hub. |
| `IImageUpdateStore.cs` / `ImageUpdateStore.cs` | In-memory store of detected container image updates. |
| `NoopImageUpdateStore.cs` | Core default `IImageUpdateStore` for when the **ImageUpdate module** is off — holds no updates, so the Dashboard + the Core `ContainerTools` update tools resolve it cleanly. Real store wins by last-registration when on (RoadToSAP Phase 1). |

## Wiring

This module (`Modules/ImageUpdate`, toggle `Features:image-updates:Enabled`) also registers the opt-in
[`../AutoUpdate/`](../AutoUpdate/) hosted service — the two are one feature (§3.7). It contributes no nav and no
MCP tools; `check_updates`/`update_container` stay in the Core, mixed `ContainerTools`, and the Dashboard reads
`IImageUpdateStore`, so Core keeps the `NoopImageUpdateStore` default for when the module is off.

## Related

- Applying updates (and scheduled auto-updates): [`../AutoUpdate/`](../AutoUpdate/)
- Container operations: [`../Docker/`](../Docker/)
- MCP tool: `get_update_status`; UI: [`../../Components/Pages/Dashboard.razor`](../../Components/Pages/Dashboard.razor)
