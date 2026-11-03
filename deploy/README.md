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

The image and the release assets are produced by [`.github/workflows/release.yml`](../.github/workflows/release.yml) on every `vX.Y.Z` tag (R1-45):

1. **prepare** — the tag must be `vX.Y.Z` and match `<Version>` in `src/Whiskers/Whiskers.csproj` and `version`/`appVersion` in `deploy/helm/whiskers/Chart.yaml`. Every later job checks out exactly the tag commit. The run fails early if that version is already published.
2. **test** — the full CI suite ([`ci.yml`](../.github/workflows/ci.yml), called via `workflow_call`) on the tag commit.
3. **build** — `linux/amd64` and `linux/arm64` are built once each and staged in GHCR **by digest only** (no tag).
4. **scan** — Trivy scans both staged digests; any CRITICAL fails the run. Findings without a fix are not skipped: an exception must be recorded in [`.trivyignore`](../.trivyignore) with a reason and an expiry date.
5. **publish-image** — the two scanned digests are merged into one multi-arch index tagged `X.Y.Z`, verified to contain exactly the scanned digests, and cosign-signed (keyless). Nothing is rebuilt.
6. **publish-chart** — the Helm chart `X.Y.Z` to `oci://ghcr.io/lupusmalusdeviant/charts/whiskers`.
7. **github-release** — one SBOM per architecture (`whiskers-X.Y.Z-linux-amd64.sbom.spdx.json`, `…-linux-arm64…`, of exactly the published image manifests), checksums, and image-pinned `docker-compose.yml` / `docker-compose.hardened.yml` / `install.sh`.
8. **move-tags** — last, once the GitHub release exists: the moving tags are pointed at the published digest — `X.Y` only when this is the highest **published** release of its line, `latest` only when it is the highest published release overall (by GitHub releases, not by git tags). This is decided when the job runs, so every "Re-run failed jobs" decides again: a re-run of an older release never moves them back past a version published since, and a version finished after a later hotfix takes `latest` back.

**Published versions are immutable.** [`.github/scripts/assert-not-published.sh`](../.github/scripts/assert-not-published.sh) checks that the `X.Y.Z` image, chart version or GitHub release does not exist yet and fails the run if it does — or if it cannot tell (tool missing, auth error, outage). A fresh run checks all three strictly in `prepare`; each publish job checks again right before its push. The one exception is `publish-image`: an `X.Y.Z` that already holds **exactly** this run's scanned amd64/arm64 digests is accepted (it is not re-tagged; signing and the moving tags are repeated, which is harmless), so a run that broke after tagging can be finished. Any other content under `X.Y.Z` fails the run. To fix a bad release, bump the version and tag again. `X.Y` and `latest` are moving tags by design.

**When a release run fails**, it depends on where:

| Failed in | What to do |
|---|---|
| `prepare`, `test`, `build`, `scan` | Nothing was published. Fix the cause; then either start a manual run from the tag (below) or, if the fix is outside the repository (a registry outage), use **Re-run failed jobs**. If the fix needs a code change, bump the version and tag again. |
| `publish-image` (e.g. a Sigstore/Rekor outage while signing) | Use **Re-run failed jobs** — not "Re-run all jobs" and not a new manual run: both start at `prepare`, which refuses the now-existing `X.Y.Z`. The re-run keeps the build outputs, recognises the identical digests and finishes signing and tagging. |
| `publish-chart` | **Re-run failed jobs**. The chart is pushed by its last step, so a failed job has not published it. |
| `github-release` | **Re-run failed jobs**. If the release itself was already created (only the asset upload failed), delete that incomplete GitHub release first (the tag stays), then re-run. The moving tags follow in `move-tags`. |
| `move-tags` | **Re-run failed jobs**. Everything else is published; the tags are decided again from the releases published at that moment. |

**Manual runs** (Actions → release → Run workflow) must be started **from the tag** ("Use workflow from" = `vX.Y.Z`) with the same tag as input; a run from a branch is refused. This keeps the label, the code and the cosign signing identity (`release.yml@refs/tags/vX.Y.Z`) the same. A manual run starts from scratch, so it only helps when nothing of that version was published yet.

**One release at a time.** The workflow uses one concurrency group. GitHub keeps at most one *pending* run per group: if two more tags are pushed while a release is running, the older pending run is **cancelled**, not queued. A release run shown as "cancelled" without anyone cancelling it is this case — start it again by hand from its tag.

All actions in the workflows are pinned to full commit SHAs (Dependabot updates them); each job holds only the permissions it needs.

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
