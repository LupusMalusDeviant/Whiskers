# deploy/

Deployment assets — the pieces used to install and run Whiskers, separate from the application source.

| File / dir | What it is |
|---|---|
| [`install.sh`](install.sh) | One-command installer (outOfTheBox W2). Pulls the published image (`ghcr.io/lupusmalusdeviant/whiskers`), writes a small compose file into `./whiskers/`, brings it up, waits for `/healthz`, and prints the URL. Re-running it updates in place (`pull` + `up`). Takes `--port`, `--bind`, `--data`, `--dir`, `--image`, `--install-docker`, `--yes`; env equivalents `WHISKERS_PORT` etc. Never installs Docker or exposes anything publicly without an explicit opt-in. |
| [`docker-compose.postgres.yml`](docker-compose.postgres.yml) | Overlay that adds a PostgreSQL service and points Whiskers at it. Use with the base file: `docker compose -f docker-compose.yml -f deploy/docker-compose.postgres.yml up -d`. |
| [`helm/whiskers/`](helm/whiskers/) | Helm chart for running Whiskers **on** Kubernetes (Track A): single replica + Recreate, non-root/read-only, PVC data dir, optional Postgres/ingress/Tailscale sidecar. Published as an OCI artifact (`oci://ghcr.io/lupusmalusdeviant/charts/whiskers`) by the release pipeline; linted + render-checked by [`chart-ci.yml`](../.github/workflows/chart-ci.yml). |
| [`k8s/`](k8s/) | Assets for **managing** a Kubernetes cluster *from* Whiskers (Track B): least-privilege `whiskers-agent` RBAC manifest + kubeconfig onboarding guide. |
| [`telemetry/`](telemetry/) | mTLS / socket-proxy templates for the hardened remote-monitoring posture. |
| [`borg-fleet/`](borg-fleet/) | Nightly append-only BorgBackup of every managed server to its own Hetzner Storage Box sub-account; `borg-enroll <server-id>` enrols a server in one command. |

## Release pipeline

The image and the release assets are produced by [`.github/workflows/release.yml`](../.github/workflows/release.yml) on every `v*` tag: multi-arch build (`linux/amd64`, `linux/arm64`), a **Trivy scan gate that runs before anything is published** (a CRITICAL fails the whole run), push to GHCR (`latest`, `X.Y.Z`, `X.Y`), then a GitHub Release with an SBOM, checksums, and image-pinned `docker-compose.yml` / `docker-compose.hardened.yml` / `install.sh` attached.

See the [README quick start](../README.md#quick-start) for the user-facing install paths.

## `tailnet-guard.sh` — for hosts that reach their fleet over Tailscale

Optional host-level guard for the deployment host. A `docker compose up --build` produces a burst of veth
link changes; on 2026-08-20 that stripped `tailscale0`'s addresses on a deploy host, and tailscaled did not
restore them — `tailscale ping` still answered (userspace disco) while every TCP connection to the tailnet
timed out via the default route. Whiskers reported "success" for the deploy while five of six servers had
silently become unreachable.

The script verifies the data path end-to-end (an address on `tailscale0` **and** a TCP connect to a
configured peer's Docker port) and restarts `tailscaled` once if it is broken. Install it as root and wire
it in two places:

```bash
install -m 0755 deploy/tailnet-guard.sh /usr/local/sbin/tailnet-guard
/usr/local/sbin/tailnet-guard --check-only     # report only, never acts
```

- at the end of your deploy script, after `docker compose up` — and once more ~45s later: the breakage has
  been observed *after* the compose call returned, so a single immediate check can pass and still leave the
  fleet cut off
- as a systemd timer (every 60s) for the churn that happens outside deploys, and as the backstop for the
  delayed case above

Repairs are serialized with `flock`: the timer and a deploy's postflight can fire together, and two
concurrent `systemctl restart tailscaled` runs race each other into a worse state than the one being
repaired.

Complementary hardening for hosts running `systemd-networkd`: mark the tun unmanaged
(`[Match] Name=tailscale0` + `[Link] Unmanaged=yes`) and set `ManageForeignRoutes=no` /
`ManageForeignRoutingPolicyRules=no`.
