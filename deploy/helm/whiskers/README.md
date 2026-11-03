# Whiskers Helm Chart

Deploys Whiskers on Kubernetes (kubernetesImplement Track A). **Single replica by design** for
1.0: Blazor Server circuits, stateful background loops and the SQLite/JSON-store data directory
make multi-replica wrong — the chart pins `replicas: 1` with `strategy: Recreate` (no two writers
on the PVC during rollouts). HA is a post-1.0 topic.

On Kubernetes there is no meaningful "local" Docker host: the pod starts with
`WHISKERS_DISABLE_LOCAL_DOCKER=true` and an empty fleet — Whiskers is the control plane for
**remote** Docker hosts (SSH-bootstrap onboarding / TCP+mTLS / mesh) and Kubernetes clusters
(kubeconfig, see [`../../k8s/README.md`](../../k8s/README.md)).

## Install

Always install a released chart **version**: the chart's `appVersion` is the image tag it runs (`image.tag` empty =
that release; `image.tag=latest` is refused). Keep the vault key in a Secret you create yourself, so it is never
in your values file, your shell history or the Helm release record:

```bash
# The vault key: generate it once, keep a copy off the cluster (password manager). Without it the vault and
# every encrypted backup are unreadable.
kubectl create secret generic whiskers-vault --from-literal=vaultKey="$(openssl rand -hex 32)"

# Verify the image signature of the release you install (keyless cosign, see the README "Supply chain"):
cosign verify ghcr.io/lupusmalusdeviant/whiskers:X.Y.Z \
  --certificate-identity-regexp '^https://github.com/LupusMalusDeviant/Whiskers/.github/workflows/release.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com

# From the OCI registry (published by the release pipeline), pinned to that release:
helm install whiskers oci://ghcr.io/lupusmalusdeviant/charts/whiskers --version X.Y.Z \
  --set vault.existingSecret=whiskers-vault

# From source (a checkout of the release tag):
helm install whiskers deploy/helm/whiskers --set vault.existingSecret=whiskers-vault
```

Open the UI (`kubectl port-forward svc/whiskers 8080:8080` → http://localhost:8080) — the
first-run setup wizard creates the admin account; it asks for the one-time setup token
(`kubectl exec deploy/whiskers -c whiskers -- cat /app/data/setup-token.txt`). For unattended installs set
`auth.adminEmail` + `auth.adminPasswordSecret` (Secret key `adminPassword`).

The chart **refuses to render** (fails closed) instead of deploying a pod that cannot work: `database.provider`
other than `sqlite`/`postgres`, `postgres` without `database.existingSecret` or `database.connectionString`, an
unpinned sidecar image (no tag/digest, or `latest`), `image.tag=latest`, and an `extraEnv` entry that would change
the VPN mode.

## Upgrade

`helm upgrade whiskers oci://ghcr.io/lupusmalusdeviant/charts/whiskers --version <new X.Y.Z> -f your-values.yaml`
(verify the new image's signature first, as above). Keep your settings in your own values file, or use
`--reset-then-reuse-values` (Helm ≥ 3.14): plain `--reuse-values` keeps the old chart's defaults and misses values
a new chart adds. The pod is replaced (`Recreate`), and on its first start Whiskers takes
a pre-upgrade self-backup and migrates the database before it answers — the startup probe allows 5 minutes for that
(`probes.startup.*`, raise it for a large database). A downgrade is **not** a `helm rollback` onto a migrated
database: it is the previous release on that pre-upgrade backup, see
[`docs/operations/backup-and-recovery.md`](../../../docs/operations/backup-and-recovery.md).

**From a chart before R1-46:** the Tailscale sidecar's state moves from an `emptyDir` to its own PVC, so the first
start after the upgrade registers the node once more (with `tailscaleSidecar.authKeySecret`; use a reusable or
fresh key); from then on it keeps its identity. If you overrode `tailscaleSidecar.image` with an unpinned value
(`…:latest` or no tag), pin it, or the upgrade is refused.

## Values (highlights)

| Value | Default | Notes |
|---|---|---|
| `persistence.enabled` / `size` | `true` / `2Gi` | `/app/data` PVC: SQLite DB, JSON stores, keys, certs. `existingClaim` supported. |
| `database.provider` | `sqlite` | `postgres` needs `database.existingSecret` (key `connectionString`, preferred) or inline `connectionString` — without either the chart refuses to render. Strategy stays Recreate either way (JSON stores remain on the PVC until changeme C7). |
| `vault.existingSecret` / `key` | unset | Secret key `vaultKey` → `VAULT_KEY`. Strongly recommended — without it the vault, kubeconfig-based K8s servers and encrypted backups are off. |
| `ingress.enabled` | `false` | See WebSockets below. |
| `tailscaleSidecar.enabled` | `false` | Userspace tailscaled (no NET_ADMIN / /dev/net/tun) for outbound mesh access; `authKeySecret` key `authKey`. |
| `tailscaleSidecar.image` | `tailscale/tailscale:v1.104.4@sha256:…` | Pinned (tag + index digest). No tag/digest or `latest` is refused. |
| `tailscaleSidecar.state.persistent` / `size` / `existingClaim` | `true` / `128Mi` / unset | The node identity (tailscaled state) on its own PVC, so a restarted pod is the same tailnet node and the auth key is used once (`TS_AUTH_ONCE`). The chart sets `TS_KUBE_SECRET=""`: inside Kubernetes the Tailscale image would otherwise keep its state in a Secret named `tailscale`, which needs RBAC the chart does not grant. `persistent=false` = `emptyDir`: a new node at every restart. |
| VPN mode (fixed) | `VPN_PROVIDER=none`, `Vpn__Provider=none` | Set on every render (R1-46). The pod is non-root with a read-only root file system, where the image's legacy `tailscaled` bring-up cannot work; the in-app providers (`tailscale`/`netbird`) are not supported in the chart. Mesh access = the sidecar. `extraEnv` cannot override these names (also `DOTNET_`/`ASPNETCORE_`-prefixed, in any letter case and spelled with `:`, e.g. `Vpn:Provider`), `TAILSCALE_AUTHKEY` or `VPN_FORCE_LEGACY_TAILSCALE`. |
| `localDocker.enabled` | `false` | Mounts the node's Docker socket. Root-equivalent on that node + node pinning — almost never what you want. |
| `trustedProxyCidrs` | `[]` | Whiskers trusts RFC1918 + loopback + Tailscale CGNAT for `X-Forwarded-*`. Add your pod CIDR only if it is outside RFC1918. |
| `auth.adminEmail` / `adminPasswordSecret` | unset | Unattended admin seed (`WHISKERS_ADMIN_EMAIL` + password file mount). |

## Uninstall

`helm uninstall` deletes the PVCs the chart created — the data PVC **and** the Tailscale sidecar's state PVC (neither
carries `helm.sh/resource-policy: keep`). Back up first (see Backup). Without the state PVC the sidecar's next install
registers a new tailnet node; remove the old one in the Tailscale admin console. To keep either across an uninstall,
create the PVC yourself and pass it as `persistence.existingClaim` / `tailscaleSidecar.state.existingClaim`.

## Verification checklist (needs a cluster)

The chart's unit tests and `chart-ci.yml` render the templates and check that the pinned sidecar digest is the
digest of its tag; they do not start a pod. Before a release that changes the chart or the sidecar pin, run once on
a disposable cluster (kind):

1. `helm install` with `vault.existingSecret` → the pod becomes Ready; `kubectl logs … -c whiskers` shows
   `VPN provider 'none' … skipping legacy bring-up` and no `tailscaled` line.
2. With `tailscaleSidecar.enabled=true` and a real `authKeySecret`: the `tailscale` container (uid 10001 from the pod,
   `TS_KUBE_SECRET=""`) starts, logs in, and the node appears in the tailnet; its state is on the
   `<release>-whiskers-tailscale` PVC.
3. `kubectl delete pod …` → the new pod is the **same** tailnet node (no second node, no new login with the key).
4. `helm upgrade` to the next chart version → Ready within the startup budget; the setup token and data survive.

## WebSockets / SignalR through Ingress

Blazor Server needs a long-lived WebSocket. **ingress-nginx** kills idle proxied connections
after 60 s — raise the timeouts or circuits drop constantly:

```yaml
ingress:
  enabled: true
  className: nginx
  annotations:
    nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"
    nginx.ingress.kubernetes.io/proxy-send-timeout: "3600"
  hosts:
    - host: whiskers.example.com
      paths: [{ path: /, pathType: Prefix }]
```

**Traefik** needs no extra configuration.

## Security posture

Runs under the restricted PodSecurity standard (with `localDocker.enabled=false`):
`runAsNonRoot` (uid 10001), `readOnlyRootFilesystem` (+`emptyDir` on `/tmp`), all capabilities
dropped, `seccompProfile: RuntimeDefault`, `fsGroup: 10001` for PVC ownership. Probes:
`/healthz` (liveness+startup) and `/readyz` (readiness), timings under `probes.*`. The startup probe allows
5 minutes (`probes.startup.periodSeconds` × `failureThreshold`): an upgrade takes a pre-upgrade backup and runs
the schema migrations before Whiskers answers. Raise `probes.startup.failureThreshold` for a large database.

Stopping: `terminationGracePeriodSeconds` is 60. On SIGTERM Whiskers starts no new scheduled task, AutoUpdate or
AutoPatch container and gives running ones up to 30 s to finish (host shutdown budget 45 s). What a kill cuts off
anyway is marked interrupted at the next start and reported to the admins, never run again.

## Backup

The PVC is the state. Use Whiskers' built-in self-backup (Settings → Backup & Restore /
scheduled SelfBackup task) for an application-consistent, optionally VAULT_KEY-encrypted
archive — or snapshot the PVC with your CSI tooling while the pod is stopped. Keep the vault key (the
`vault.existingSecret` Secret) apart from those backups and off the cluster: a backup without its key cannot be
restored, and a key next to its backups protects nothing. Guide:
[`docs/operations/backup-and-recovery.md`](../../../docs/operations/backup-and-recovery.md).

## Postgres variant

```bash
helm install whiskers deploy/helm/whiskers \
  --set database.provider=postgres \
  --set database.existingSecret=whiskers-db   # key: connectionString
```

The identity + metrics schemas migrate on boot (dual migration assemblies, ADR-0004).
