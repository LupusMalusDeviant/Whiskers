# Services/Vpn

Mesh-VPN abstraction. Decouples ServerWatch from any single VPN: the daemon bring-up that used to be
hard-coded in `entrypoint.sh` now lives behind `IVpnProvider`, so the VPN can be Tailscale, NetBird,
or run entirely outside the container (host/sidecar). The "none" default also keeps the app image free
of a baked-in VPN daemon, which is a prerequisite for a future distroless/chiseled image.

## Files

| File | Purpose |
|---|---|
| `IVpnProvider.cs` | A VPN backend: `EnsureUpAsync` / `GetStatusAsync` / `DownAsync` / `IsAvailableAsync` + `VpnStatus` record. |
| `IVpnService.cs` / `VpnService.cs` | Resolves the active provider from config (falls back to Noop) and exposes status. |
| `VpnBootstrapHostedService.cs` | On startup brings the active provider up (replaces the entrypoint Tailscale bring-up); no-op for `none`. |
| `VpnSettings.cs` | Config (`Vpn` section): `Provider` (none\|tailscale\|netbird), hostname, per-provider keys/URLs. |
| `VpnProcessRunner.cs` | Internal helper to run the VPN CLIs and start daemons detached. |
| `Providers/TailscaleVpnProvider.cs` | Tailscale — ensures `tailscaled` is up, then `tailscale up` with the auth key (Headscale-capable via LoginServer). |
| `Providers/NetbirdVpnProvider.cs` | NetBird — `netbird up` with a setup key, optional self-hosted management URL. |
| `Providers/NoopVpnProvider.cs` | VPN managed on host/sidecar — the decoupled default. |

## Behavior / migration

- Default `Vpn:Provider` is **none**, so existing deployments are unchanged. `entrypoint.sh` only runs
  its legacy Tailscale bring-up when the `VPN_PROVIDER` env var is **unset**; setting `VPN_PROVIDER`
  (mirror of `Vpn__Provider`) makes the shell defer to the in-app provider.
- Keyless Tailscale SSH for the web terminal is a separate per-server flag — see `ServerConfig.TailscaleSsh`
  and [`../Terminal/`](../Terminal/). Decoupling the VPN does not affect it: with `none`, connectivity is
  provided by the host/sidecar and the flag still works.
- Enrollment secrets are passed to the CLIs via environment variables (`TS_AUTHKEY` for Tailscale,
  `NB_SETUP_KEY` for NetBird), never on the command line, so they don't appear in the host process list.

## Related

- Mesh + mTLS onboarding: [`../Onboarding/`](../Onboarding/)
- Host/Docker connectivity that rides the mesh: [`../Docker/`](../Docker/), [`../Server/`](../Server/)
- Container VPN wiring: [`../../../docker-compose.yml`](../../../docker-compose.yml), [`../../../entrypoint.sh`](../../../entrypoint.sh)
