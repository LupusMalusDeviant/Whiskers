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
| `VpnSecretFile.cs` | The enrollment key as a file for one CLI call: a fresh directory (0700; an existing one or a link is refused, its mode verified), the file created 0600 in the same call (mode verified before the key is written), no trailing newline, not flushed to disk, deleted with its directory when the CLI returned (a failed delete is logged with the path). Goes to `WHISKERS_SECRET_DIR`, else `/dev/shm` (memory), else the temp directory; `RemoveStale` removes directories a crash or kill left behind, before every bring-up. |
| `VpnKeyFiles.cs` | Reads `Vpn:Tailscale:AuthKeyFile` / `Vpn:Netbird:SetupKeyFile` once when the options are built (a file wins over the value). Unreadable or empty → `KeyFileProblem`, and the bring-up fails without running a CLI; readable by group/others → `KeyFileWarning`, logged. |
| `Providers/TailscaleVpnProvider.cs` | Tailscale — ensures `tailscaled` is up, then `tailscale up --hostname=… [--accept-routes] [--login-server=…] [--auth-key=file:<0600 file>]` (Headscale-capable via LoginServer). A daemon counts as running only when `status --json` answers with a `BackendState` (exit code 1 also means "no daemon"). If no daemon answers but `tailscale0` already exists, Tailscale is **host-managed** and no second `tailscaled` is started — it would make the host daemon restart and cut the tailnet (`VPN_FORCE_LEGACY_TAILSCALE=1` overrides, same as `entrypoint.sh`). |
| `Providers/NetbirdVpnProvider.cs` | NetBird — `netbird up [--management-url=…] [--hostname=…] [--setup-key-file=<0600 file>]`. |
| `Providers/NoopVpnProvider.cs` | VPN managed on host/sidecar — the decoupled default. |

## Behavior / migration

- Default `Vpn:Provider` is **none**, so a deployment that sets no provider is unchanged (legacy entrypoint bring-up,
  or none). A deployment whose provider is `tailscale`/`netbird` (`Vpn__Provider`, also prefixed, or `VPN_PROVIDER`)
  now needs the owner takeover below; with `Vpn__Provider` set but no `VPN_PROVIDER`, the legacy bring-up that used to
  run as well is skipped and its `TAILSCALE_AUTHKEY` is unused (warned by name) — give the key as
  `Vpn__Tailscale__AuthKey` / `…AuthKeyFile`. **Exactly one side brings the VPN up**
  (R1-43, `entrypoint.sh`):

  | `VPN_PROVIDER` ↓ / Whiskers' `Vpn__Provider` → | unset | `none` | `tailscale` / `netbird` |
  |---|---|---|---|
  | unset | legacy bring-up | legacy bring-up (compose default) | in-app |
  | `none` | nobody (VPN on the host) | nobody | in-app (warned: differs) |
  | `tailscale` / `netbird` | in-app (`VPN_PROVIDER` is handed over as `Vpn__Provider`) | nobody (warned: differs) | in-app |

  Whiskers' provider is the bare `Vpn__Provider`, then `DOTNET_Vpn__Provider`, then `ASPNETCORE_Vpn__Provider` — the
  order in which the configuration lets them win (`SecretEnvironmentTests` measures it on the real builder). The
  script cannot see a provider given on Whiskers' command line or in the data directory's JSON layers; set it in the
  environment. docker-compose sets `Vpn__Provider=${VPN_PROVIDER:-none}`, so compose stays on the diagonal and on
  "unset / none". `VpnSecretHandlingTests.Exactly_one_side_brings_the_vpn_up` runs this table against the script.
- **In-app provider (R1-43):** the bring-up needs an owner. After an upgrade an admin takes "VPN bring-up" over in
  Settings → Automation owners; it runs at the next start. Without one the VPN is not brought up (status "not run,
  no owner", a Denied audit event). `Vpn:FailStartupOnError=true` (`VPN_FAIL_STARTUP_ON_ERROR`) makes such a start
  stop instead — set it only after the takeover, or the start always stops. **Locked out** (the owner lost the Admin
  role or the sign-in, the owner file became unreadable, and every start stops): set it to `false`, restart, take the
  bring-up over again (or fix the VPN settings), restart, then set it back. The exception text says the same.
- **Keys never on a command line:** `tailscale up` reads a key from `--auth-key=file:<path>` (upstream
  `cmd/tailscale/cli/up.go`, read 2026-11-03: only `--auth-key` exists, `file:` is read by `resolveValueFromFile`; the
  CLI reads no `TS_AUTHKEY` — that variable belongs to Tailscale's container image start script `containerboot`, so
  the earlier env hand-over did not log in), NetBird from `--setup-key-file=<path>` (a NetBird CLI with that flag; an
  older one fails with an unknown-flag error, which is safe). The file is 0600 in a 0700 directory and deleted right
  after the call.
- **Keys never in a child's environment:** docker-compose hands them over as `Vpn__Tailscale__AuthKey` /
  `Vpn__Netbird__SetupKey` in every mode. `entrypoint.sh` moves the active provider's key into a 0600 file
  (`whiskers-vpnkey.*` under `WHISKERS_SECRET_DIR`, else `/dev/shm`, else `TMPDIR`; it stays there while the container
  runs and is removed at the next start), passes `Vpn__Tailscale__AuthKeyFile` / `Vpn__Netbird__SetupKeyFile` instead and
  drops every key variable (`Vpn__…`, `TAILSCALE_AUTHKEY`, `NETBIRD_SETUP_KEY`, `TS_AUTHKEY`, `NB_SETUP_KEY`, each also
  behind `DOTNET_` / `ASPNETCORE_`) before it starts its first process — the values live on in unexported shell
  variables, which are unset first so an inherited one cannot keep an export flag — so neither the app nor the legacy
  `tailscaled` (which runs as long as the container) inherits one. Keys are resolved in the same order as the provider
  (bare, `DOTNET_`, `ASPNETCORE_`); a key no provider will read is dropped with a console warning (expected on a compose
  start with `VPN_PROVIDER` unset and `TAILSCALE_AUTHKEY` in `.env`). The script moves only the **exact spelling** of these
  names: a variant in other letter case (`VPN__TAILSCALE__AUTHKEY`) reaches Whiskers' start environment, from where
  Whiskers drops it as below. Whiskers itself drops them (any case, also behind the configuration prefixes
  `ASPNETCORE_` / `DOTNET_`, which `SecretEnvironmentTests` derives from the real builder) from its environment after
  the configuration read them and scrubs them from every process it starts (`Utils/SecretEnvironment`). Two Whiskers
  instances outside a container that share one temp directory should each get their own `WHISKERS_SECRET_DIR`: the
  stale-key cleanup at start would otherwise remove the other's in-flight key directory. Best: deliver the key as a file from the start
  (`TAILSCALE_AUTHKEY_FILE` / `NETBIRD_SETUP_KEY_FILE`, e.g. a Docker secret, mode 0600/0400). **Residual:** a value set
  as an environment variable stays visible in `docker inspect` and in Docker's container record; and when Whiskers is
  started **without** `entrypoint.sh` with the key in its environment, `/proc/<whiskers-pid>/environ` keeps showing the
  start-time block whatever the process does later — use the file variant there.
- The legacy entrypoint bring-up (`VPN_PROVIDER` unset, Whiskers' provider not `tailscale`/`netbird`) is the host operator's container start: it is not decided or
  audited by the gateway. It reads `TAILSCALE_AUTHKEY` only — which docker-compose does not set (it passes the `.env`
  value as `Vpn__Tailscale__AuthKey`), so it applies to `docker run -e TAILSCALE_AUTHKEY=…`. A failed `tailscale up` is
  printed as an `ERROR` line instead of being swallowed (Whiskers still starts); the key file is removed also on an
  early exit, and a stop signal during `tailscale up` is passed on (exit 143/130/129) instead of starting Whiskers.
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
- Container VPN wiring: [`../../../docker-compose.yml`](../../../../docker-compose.yml), [`../../../entrypoint.sh`](../../../../entrypoint.sh)
- Kubernetes: the Helm chart ([`../../../../deploy/helm/whiskers/`](../../../../deploy/helm/whiskers/)) sets
  `VPN_PROVIDER=none` and `Vpn__Provider=none` on every render (R1-46) — the "nobody" cell of the table above; the
  non-root, read-only pod cannot run the legacy bring-up, and the in-app providers are not offered there. Mesh access
  is the chart's pinned Tailscale sidecar.
