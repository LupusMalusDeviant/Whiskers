# deploy/

Deployment assets — the pieces used to install and run Whiskers, separate from the application source.

| File / dir | What it is |
|---|---|
| [`install.sh`](install.sh) | One-command installer (outOfTheBox W2). Pulls the published image (`ghcr.io/lupusmalusdeviant/whiskers`), writes a small compose file into `./whiskers/`, brings it up, waits for `/healthz`, and prints the URL. Re-running it updates in place (`pull` + `up`). Takes `--port`, `--bind`, `--data`, `--dir`, `--image`, `--install-docker`, `--yes`; env equivalents `WHISKERS_PORT` etc. Never installs Docker or exposes anything publicly without an explicit opt-in. |
| [`docker-compose.postgres.yml`](docker-compose.postgres.yml) | Overlay that adds a PostgreSQL service and points Whiskers at it. Use with the base file: `docker compose -f docker-compose.yml -f deploy/docker-compose.postgres.yml up -d`. |
| [`helm/whiskers/`](helm/whiskers/) | Helm chart for running Whiskers **on** Kubernetes (Track A): single replica + Recreate, non-root/read-only, PVC data dir, optional Postgres/ingress/Tailscale sidecar. Published as an OCI artifact (`oci://ghcr.io/lupusmalusdeviant/charts/whiskers`) by the release pipeline; linted + render-checked by [`chart-ci.yml`](../.github/workflows/chart-ci.yml). |
| [`k8s/`](k8s/) | Assets for **managing** a Kubernetes cluster *from* Whiskers (Track B): least-privilege `whiskers-agent` RBAC manifest + kubeconfig onboarding guide. |
| [`telemetry/`](telemetry/) | mTLS / socket-proxy templates for the hardened remote-monitoring posture. |

## Release pipeline

The image and the release assets are produced by [`.github/workflows/release.yml`](../.github/workflows/release.yml) on every `v*` tag: multi-arch build (`linux/amd64`, `linux/arm64`), a **Trivy scan gate that runs before anything is published** (a CRITICAL fails the whole run), push to GHCR (`latest`, `X.Y.Z`, `X.Y`), then a GitHub Release with an SBOM, checksums, and image-pinned `docker-compose.yml` / `docker-compose.hardened.yml` / `install.sh` attached.

See the [README quick start](../README.md#quick-start) for the user-facing install paths and [`docs/roadmap/outOfTheBox.md`](../docs/roadmap/outOfTheBox.md) (W2) for the design.
