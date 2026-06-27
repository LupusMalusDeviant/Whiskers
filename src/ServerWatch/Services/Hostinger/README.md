# Services/Hostinger

Client for the [Hostinger VPS API](https://developers.hostinger.com/) (`https://developers.hostinger.com/api/vps/v1`). Used by the [`Cloud/`](../Cloud/) control plane for out-of-band power/snapshot/metrics operations.

Like Hetzner, credentials are **per ServerWatch server**, so the bearer token is supplied per call. List/get responses are parsed tolerantly, since Hostinger's payload shapes vary.

## Files

| File | Purpose |
|---|---|
| `IHostingerService.cs` / `HostingerApiService.cs` | Hostinger VPS API client: list/get servers, power actions, snapshots and metrics — token supplied per call. |

## Related

- Provider-agnostic dispatch: [`../Cloud/`](../Cloud/)
- Models: [`../../Models/Hostinger/`](../../Models/Hostinger/)
