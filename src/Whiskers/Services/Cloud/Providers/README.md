# Services/Cloud/Providers

The cloud-provider seam (changeme **C10** / RoadToSAP §3.6), modelled on `IVpnProvider`.
`CloudControlService` dispatches power/snapshot/metric actions to the matching provider instead of a hard
`if Hetzner … else Hostinger` switch — a new provider is a new registration, not an edit to the dispatch.

| File | Role |
|---|---|
| `ICloudProvider.cs` | The seam: `Provider` (matched against `ServerConfig.CloudProvider` — the **enum stays the persisted key**, so `servers.json` is unchanged), `DisplayName`, and the agnostic ops (resolve/list, power on/off/reboot/hard-reset, snapshot, metrics). Each provider formats its own result messages (moved **byte-identical** from the old inline dispatch) and resolves its target by **public IP, then name**. |
| `IHetznerExtensions.cs` | Optional capability for Hetzner-only ops (rescue, backups, server-type change, snapshot management). Only the Hetzner provider implements it; the Hetzner MCP tools resolve it through the seam. |
| `HetznerCloudProvider.cs` | `ICloudProvider` + `IHetznerExtensions` over `IHetznerService`. |
| `HostingerCloudProvider.cs` | `ICloudProvider` over `IHostingerService` (no Hetzner extensions). |

**Multi-registration:** the Hetzner provider is registered as `ICloudProvider` **and** `IHetznerExtensions`
(same instance); `CloudControlService` takes `IEnumerable<ICloudProvider>` and selects by
`ServerConfig.CloudProvider`.

**Target resolution is the safety-critical part** — power-off / hard-reset / snapshot all resolve their VM
through `Map` (public-IP, then a self-flagging name fallback: a mesh/Tailscale SshHost ≠ the cloud public IP).
`Map` is public + static and covered by `CloudTargetResolutionTests` (IP match → no note; name fallback →
warning surfaced; **no match → null, so nothing destructive fires on a wrong VM**).

**OPT-12:** every provider + client call carries a `CancellationToken`, threaded through to the HTTP layer.
The `ICloudControlService` facade stays token-free (its callers — the MCP tools and the `/cloud` page — have
no cancellation origin).
