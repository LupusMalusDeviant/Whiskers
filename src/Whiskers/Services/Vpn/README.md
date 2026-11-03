# Services/Vpn

Mesh-VPN abstraction. Decouples Whiskers from any single VPN: the daemon bring-up that used to be
hard-coded in `entrypoint.sh` now lives behind `IVpnProvider`, so the VPN can be Tailscale, NetBird,
or run entirely outside the container (host/sidecar). The "none" default also keeps the app image free
of a baked-in VPN daemon, which is a prerequisite for a future distroless/chiseled image.

## Files

| File | Purpose |
|---|---|
| `IVpnProvider.cs` | A VPN backend: `EnsureUpAsync` / `GetStatusAsync` / `DownAsync` / `IsAvailableAsync` + `VpnStatus`. `EnsureUpAsync` is `[MutatingOperation("vpn.bootstrap")]` and returns a `VpnBringUpResult` (`Connected`, `HostManaged`, `NotInstalled`, `InvalidSetting`, `NeedsLogin`, `Failed`) whose message never holds the key (R1-43). |
| `IVpnService.cs` / `VpnService.cs` | Resolves the active provider from config (falls back to `none` by id — the mutation scope guard hands out proxies, so never by type) and exposes status. |
| `VpnBootstrapHostedService.cs` | On startup brings the active provider up **through the Mutation Gateway** (`vpn.bootstrap`, R1-43): unattended, on the authority of the admin who took the "VPN bring-up" over in Settings → Automation owners (`IAutomationOwnerStore.VpnBootstrap`), re-read at every start. No owner, a blocked/suspended entry, a gateway refusal or a failed CLI → nothing (more) runs, one audit event, an error in the log and the status in `IVpnBootstrapState`; an owner who lost the authority is suspended. Nothing for provider `none`. |
| `VpnBootstrapState.cs` | `IVpnBootstrapState` (the last bring-up of this process, shown in the Automation owners panel), `VpnBootstrapReport`, `VpnBootstrapFailedException` (thrown from `StartAsync` when `Vpn:FailStartupOnError` is set). |
| `VpnSettings.cs` | Config (`Vpn` section): `Provider` (none\|tailscale\|netbird), hostname, `FailStartupOnError`, per-provider keys/URLs. |
| `VpnProcessRunner.cs` | `IVpnProcessRunner` + `VpnProcessRunner`: runs the VPN CLIs and starts daemons detached. Every argument is its own argv element (`ProcessStartInfo.ArgumentList`), never a joined string; there is no environment parameter. |
| `VpnArguments.cs` | Validates the values that become arguments (mesh hostname: RFC 1123 name; login server / management URL: absolute http(s) without user info or whitespace) and scrubs CLI output of the key (also 8-character pieces of it). |
| `VpnSecretFile.cs` | The enrollment key as a file: fresh directory (0700), file created 0600 in the same call (mode verified before the key is written), no trailing newline, deleted with its directory when the CLI returned. |
| `Providers/TailscaleVpnProvider.cs` | Tailscale — ensures `tailscaled` is up, then `tailscale up --hostname=… [--accept-routes] [--login-server=…] [--auth-key=file:<0600 file>]` (Headscale-capable via LoginServer). A daemon counts as running only when `status --json` answers with a `BackendState` (exit code 1 also means "no daemon"). If no daemon answers but `tailscale0` already exists, Tailscale is **host-managed** and no second `tailscaled` is started — it would make the host daemon restart and cut the tailnet (`VPN_FORCE_LEGACY_TAILSCALE=1` overrides, same as `entrypoint.sh`). |
| `Providers/NetbirdVpnProvider.cs` | NetBird — `netbird up [--management-url=…] [--hostname=…] [--setup-key-file=<0600 file>]`. |
| `Providers/NoopVpnProvider.cs` | VPN managed on host/sidecar — the decoupled default. |

## Behavior / migration

- Default `Vpn:Provider` is **none**, so existing deployments are unchanged. `entrypoint.sh` only runs
  its legacy Tailscale bring-up when the `VPN_PROVIDER` env var is **unset**; setting `VPN_PROVIDER`
  (mirror of `Vpn__Provider`) makes the shell defer to the in-app provider.
- **In-app provider (R1-43):** the bring-up needs an owner. After an upgrade an admin takes "VPN bring-up" over in
  Settings → Automation owners; it runs at the next start. Without one the VPN is not brought up (status "not run,
  no owner", a Denied audit event). `Vpn:FailStartupOnError=true` (`VPN_FAIL_STARTUP_ON_ERROR`) makes such a start
  stop instead — set it only after the takeover, or the start always stops.
- **Keys never on a command line:** `tailscale up` reads a key from `--auth-key=file:<path>` (the CLI does **not** read
  `TS_AUTHKEY`; that variable belongs to Tailscale's container image start script, so the earlier env hand-over did not
  log in), NetBird from `--setup-key-file=<path>`. The file is 0600 in a 0700 directory and deleted right after the
  call. The legacy `entrypoint.sh` does the same with the shell's `printf` builtin and `umask 077`, and unsets
  `TAILSCALE_AUTHKEY` before it starts the app.
- The legacy entrypoint bring-up is the host operator's container start: it is not decided or audited by the gateway.
  A failed `tailscale up` there is printed as an `ERROR` line instead of being swallowed; Whiskers still starts.
- The mesh hostname and the control URL are recorded in the event's details, not as decision arguments: they name
  Whiskers itself on the mesh (by default its container name), which the control-plane patterns (`whiskers*`,
  `serverwatch*`) would read as a protected container.
- Keyless Tailscale SSH for the web terminal is a separate per-server flag — see `ServerConfig.TailscaleSsh`
  and [`../Terminal/`](../Terminal/). Decoupling the VPN does not affect it: with `none`, connectivity is
  provided by the host/sidecar and the flag still works.

## Related

- Gateway, owners and the automation runner: [`../Mutations/`](../Mutations/)
- Mesh + mTLS onboarding: [`../Onboarding/`](../Onboarding/)
- Host/Docker connectivity that rides the mesh: [`../Docker/`](../Docker/), [`../Server/`](../Server/)
- Container VPN wiring: [`../../../docker-compose.yml`](../../../docker-compose.yml), [`../../../entrypoint.sh`](../../../entrypoint.sh)
