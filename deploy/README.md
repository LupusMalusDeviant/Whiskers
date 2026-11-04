# deploy/

Deployment assets — the pieces used to install and run Whiskers, separate from the application source.

| File / dir | What it is |
|---|---|
| [`install.sh`](install.sh) | One-command installer (outOfTheBox W2, R1-46). Installs **one released version** of `ghcr.io/lupusmalusdeviant/whiskers` (never `latest`), generates the vault key, verifies the image signature when cosign is installed, writes a small compose file into `./whiskers/`, brings it up, waits for `/healthz`, and prints the URL, the setup token and what to back up. Re-running it upgrades in place. See [Installer](#installer) below. |
| [`docker-compose.postgres.yml`](docker-compose.postgres.yml) | Overlay that adds a PostgreSQL service and points Whiskers at it. Use with the base file: `docker compose -f docker-compose.yml -f deploy/docker-compose.postgres.yml up -d`. |
| [`helm/whiskers/`](helm/whiskers/) | Helm chart for running Whiskers **on** Kubernetes (Track A): single replica + Recreate, non-root/read-only, PVC data dir, optional Postgres/ingress/Tailscale sidecar. Published as an OCI artifact (`oci://ghcr.io/lupusmalusdeviant/charts/whiskers`) by the release pipeline; linted + render-checked by [`chart-ci.yml`](../.github/workflows/chart-ci.yml). |
| [`k8s/`](k8s/) | Assets for **managing** a Kubernetes cluster *from* Whiskers (Track B): least-privilege `whiskers-agent` RBAC manifest + kubeconfig onboarding guide. |
| [`telemetry/`](telemetry/) | mTLS / socket-proxy templates for the hardened remote-monitoring posture. |
| [`borg-fleet/`](borg-fleet/) | Nightly append-only BorgBackup of every managed server to its own Hetzner Storage Box sub-account; `borg-enroll <server-id>` enrols a server in one command. |

## Installer

```bash
# Take the installer from the release you want: it installs exactly that release.
curl -fsSLO https://github.com/LupusMalusDeviant/Whiskers/releases/download/vX.Y.Z/install.sh
curl -fsSLO https://github.com/LupusMalusDeviant/Whiskers/releases/download/vX.Y.Z/checksums.txt
sha256sum --check --ignore-missing checksums.txt   # catches a broken download, not tampering (same source)
bash install.sh
```

`checksums.txt` comes from the same release page as `install.sh`, so it detects a corrupted download, not a
tampered release. What protects the image is the signature check below.

**Version.** The installer's default is the release it belongs to (`WHISKERS_RELEASE` in the script; the release
pipeline accepts only `vX.Y.Z` tags — no pre-releases — and refuses one whose version differs, so the `install.sh` of
release X.Y.Z installs X.Y.Z). `--version X.Y.Z` installs another release. There is no `latest` and no pre-release:
`--version latest`, `--version 1.0.0-rc.1`, and an `--image` without a tag or digest or with the tag `latest`, are
refused before anything is pulled or written. `--image` must be a plain image reference (lowercase repository,
optional `:tag`, optional `@sha256:` digest); `--bind` an IPv4 address or a well-formed `[IPv6]` address (eight groups of 1–4 hex digits, or fewer with one `::`, optionally ending in IPv4); `--port` 1–65535; `--data` a path
without `:`, `#`, `$` (Compose would substitute a variable and mount another directory), quotes or line breaks — anything else is refused, since these values go into the compose file.

**Signature.** Before it pulls, the installer runs `cosign verify` when cosign is installed (`--verify auto`, the
default). For release X.Y.Z it requires a signature from exactly the release workflow run of tag `vX.Y.Z`
(`--certificate-identity-regexp '^https://github\.com/LupusMalusDeviant/Whiskers/\.github/workflows/release\.yml@refs/tags/vX\.Y\.Z$'`),
so an older signed image re-tagged as X.Y.Z does not pass. The digest the signature covers is written into the compose
file (`image: ghcr.io/lupusmalusdeviant/whiskers:X.Y.Z@sha256:…`): Docker pulls exactly the verified image even if the
tag were moved afterwards. A bad signature, or a verification that does not name exactly one digest, stops the
installer with nothing changed. Without cosign it says so, prints the command to run by hand, and the compose file
names the tag only. `--verify require` refuses to install without a verified signature (also for an `--image` outside
`ghcr.io/lupusmalusdeviant/whiskers`, which cannot carry the release signature); `--verify off` skips the check (said
on the console).

**Vault key.** A new install generates a random key (`openssl rand -hex 32`, else `/dev/urandom`) into
`./whiskers/secrets/vault.key` — the directory 0700, the file 0600, written by the shell's `printf` builtin (never on a
command line, never printed) — and the compose file hands it to Whiskers as a secret file (`VAULT_KEY_FILE`, mounted at
`/run/secrets/whiskers_vault_key`), not as an environment value. A re-run keeps an existing key; an empty key file
stops the installer (restore it from your backup). An install made by an older installer has no key: the installer
says so and does not change it — to add one, create `secrets/vault.key` the same way, add the `secrets:` entry and
`VAULT_KEY_FILE` as in a new install's compose file, and `docker compose up -d`. The key file belongs to the user
who ran the installer and is readable only by them: the default profile's container runs as root and reads it, but
under Docker's `userns-remap` or after switching to the hardened profile (`user: 10001`) Whiskers cannot read it and
refuses to start (it never starts without the key it was given) — then hand the file to that user
(`chown 10001 secrets/vault.key`, or the remapped id), keeping mode 0600.

**Back up two things, apart from each other and off the host:** the vault key (password manager or offline media —
without it the vault's secrets and every encrypted backup are unreadable; with it, anyone holding a backup can read
it) and the data (Settings → Backup & Restore, encrypted self-backup / scheduled SelfBackup task; copy the archives
off the host). Guide: [`docs/operations/backup-and-recovery.md`](../docs/operations/backup-and-recovery.md).

**Upgrade.** Run the `install.sh` of the newer release (or `install.sh --version <newer X.Y.Z>`). It changes only the
`image:` line of the compose file it wrote, keeping the file's mode and owner, then `pull` + `up -d`. A copy of the
previous file is kept as `docker-compose.yml.bak-<time>` with the same permissions — **it may contain secrets** (e.g. a
`VAULT_KEY` you set inline): delete it once the upgrade works. The port and bind address come from the existing
compose file (for the health check and the URL it prints); `--port`, `--bind` and `--data` only apply to a new install
and are ignored on a re-run with a warning — edit the compose file to change them. On its first start the new release
takes a pre-upgrade self-backup and migrates the database; the installer waits up to about 5 minutes for `/healthz`.

A **downgrade is refused** (a newer release may have migrated the database): going back is the previous release on the
pre-upgrade backup, see the guide above. The installed release is the newest of: the compose file's `:X.Y.Z` tag, the
version the running Whiskers recorded at its last start (`/app/data/.last-started-version`, written since R1-34), and
the image's `org.opencontainers.image.version` label. An install made with `:latest` by an older installer (or one
naming only a digest) is moved to the pinned release only when that tells a release that is not newer than the target.
When nothing tells — the container is not running, or it is a release from before R1-34 that records no version — the
installer refuses: start the existing Whiskers and re-run, or, if you know it runs an older release (the version is
shown in the sidebar), re-run with `--allow-unknown-current`. The override covers only an unknown *installed* release: the target's release must always be known — its `:X.Y.Z` tag, or for a digest-only `--image` the release its verified signature names (the certificate subject `…release.yml@refs/tags/vX.Y.Z`; without cosign such a target is refused on an existing install) — and a known downgrade is never allowed. The check also runs when the compose file already names the target: re-running on data a newer release has migrated is refused before `pull`/`up`. A compose
file the installer did not write, or one with more than one `image:` line, is left alone (the installer stops and
says so).

## Release pipeline

The image and the release assets are produced by [`.github/workflows/release.yml`](../.github/workflows/release.yml) on every `vX.Y.Z` tag (R1-45):

1. **prepare** — the tag must be `vX.Y.Z` and match `<Version>` in `src/Whiskers/Whiskers.csproj`, `version`/`appVersion` in `deploy/helm/whiskers/Chart.yaml` and `WHISKERS_RELEASE` in `deploy/install.sh` (R1-46). Every later job checks out exactly the tag commit. The run fails early if that version is already published.
2. **test** — the full CI suite ([`ci.yml`](../.github/workflows/ci.yml), called via `workflow_call`) on the tag commit.
3. **build** — `linux/amd64` and `linux/arm64` are built once each and staged in GHCR **by digest only** (no tag).
4. **scan** — Trivy scans both staged digests; any CRITICAL fails the run. Findings without a fix are not skipped: an exception must be recorded in [`.trivyignore`](../.trivyignore) with a reason and an expiry date.
5. **publish-image** — the two scanned digests are merged into one multi-arch index tagged `X.Y.Z`, verified to contain exactly the scanned digests, and cosign-signed (keyless). Nothing is rebuilt.
6. **publish-chart** — the Helm chart `X.Y.Z` to `oci://ghcr.io/lupusmalusdeviant/charts/whiskers`.
7. **github-release** — one SBOM per architecture (`whiskers-X.Y.Z-linux-amd64.sbom.spdx.json`, `…-linux-arm64…`, of exactly the published image manifests), checksums, and image-pinned `docker-compose.yml` / `docker-compose.hardened.yml` / `install.sh`.
8. **move-tags** — last, once the GitHub release exists: the moving tags are pointed at the published digest — `X.Y` only when this is the highest **published** release of its line, `latest` only when it is the highest published release overall (by GitHub releases, not by git tags). This is decided when the job runs, so every "Re-run failed jobs" decides again: a re-run of an older release never moves them back past a version published since, and a version finished after a later hotfix takes `latest` back.

**Published versions are immutable.** [`.github/scripts/assert-not-published.sh`](../.github/scripts/assert-not-published.sh) checks that the `X.Y.Z` image, chart version or GitHub release does not exist yet and fails the run if it does — or if it cannot tell (tool missing, auth error, outage). A fresh run checks all three strictly in `prepare`; each publish job checks again right before its push. The one exception is `publish-image`: an `X.Y.Z` that already holds **exactly** this run's scanned amd64/arm64 digests is accepted (it is not re-tagged; signing is repeated, which is harmless, and `move-tags` decides the moving tags again), so a run that broke after tagging can be finished. Any other content under `X.Y.Z` fails the run. To fix a bad release, bump the version and tag again. `X.Y` and `latest` are moving tags by design.

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

### Pinned build inputs (R1-52)

Everything a build reads is pinned, and each pin has an update path in [`.github/dependabot.yml`](../.github/dependabot.yml) (`BuildReproducibilityTests` fails when a project, the SDK, the base images or the actions lose coverage):

| Input | Pinned in | Moved by |
|---|---|---|
| NuGet packages (direct and transitive, with content hash) | `src/*/packages.lock.json`; CI and the Dockerfile restore with `--locked-mode` | Dependabot `nuget` (every project directory) |
| .NET SDK | `global.json` (exact version, `rollForward: disable`) and the Dockerfile's `sdk:<version>@sha256:…` | Dependabot `dotnet-sdk` and `docker` — see below |
| Base images (SDK, ASP.NET runtime, `docker:<v>-cli`) | `FROM image:tag@sha256:…` in the [`Dockerfile`](../Dockerfile) | Dependabot `docker`: a new digest behind the same tag (OS and runtime fixes) or a new tag |
| GitHub Actions | full commit SHA + `# vX.Y.Z` comment | Dependabot `github-actions` |

**Moving a digest by hand:** `docker buildx imagetools inspect <image:tag>` prints the multi-arch index digest (the `Digest:` line); write it as `image:tag@sha256:<digest>`. Use the index digest, not a per-architecture one, or the arm64 build gets the amd64 image.

**Moving the SDK** touches three things in one PR: `global.json`'s version, the Dockerfile's `mcr.microsoft.com/dotnet/sdk:<version>@sha256:…`, and the lock files — the SDK writes its own framework package (`Microsoft.AspNetCore.App.Internal.Assets`) into `src/Whiskers/packages.lock.json` and `src/Whiskers.Tests/packages.lock.json`. With the new SDK installed, run `dotnet restore Whiskers.slnx --force-evaluate` and commit the result. A Dependabot PR that moves only one of them fails `BuildReproducibilityTests` or CI's locked restore — complete it on that branch.

**Runtime tools in the image.** The image ships what Whiskers starts as a process and nothing of the Docker engine: the static docker CLI (local terminal `docker exec`), `nsenter` (host shell of the full profile), `ssh`/`sshpass`, `tailscale`/`tailscaled` with `iptables`, the PostgreSQL 18 client tools and `curl` (health probe). `ImageRuntimeToolsTests` keeps that list in step with the source and checks a built image in CI (`image-runtime` job). Run them locally against an image with `WHISKERS_IMAGE=<image> dotnet test src/Whiskers.Tests --filter Category=ImageRuntime`. The Tailscale and PostgreSQL packages come from their apt repositories at their current version.

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
