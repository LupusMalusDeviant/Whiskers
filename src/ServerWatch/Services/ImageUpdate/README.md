# Services/ImageUpdate

Detects when a newer image is available for a running container by comparing the running image's digest against the current digest in its registry. Results feed the dashboard's update badges and the one-click update flow.

## Files

| File | Purpose |
|---|---|
| `ImageUpdateChecker.cs` | Background checker: for each container, resolves its registry reference and compares digests to detect available updates. |
| `IRegistryClient.cs` / `RegistryClient.cs` | Queries a container registry for the current remote image digest; includes the image-reference parser. |
| `IImageUpdateStore.cs` / `ImageUpdateStore.cs` | In-memory store of detected container image updates. |

## Related

- Applying updates (and scheduled auto-updates): [`../AutoUpdate/`](../AutoUpdate/)
- Container operations: [`../Docker/`](../Docker/)
- MCP tool: `get_update_status`; UI: [`../../Components/Pages/Dashboard.razor`](../../Components/Pages/Dashboard.razor)
