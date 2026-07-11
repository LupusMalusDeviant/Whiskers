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

```bash
# From the OCI registry (published by the release pipeline):
helm install whiskers oci://ghcr.io/lupusmalusdeviant/charts/whiskers \
  --set vault.key="$(openssl rand -hex 32)"

# From source:
helm install whiskers deploy/helm/whiskers --set vault.key=...
```

Open the UI (`kubectl port-forward svc/whiskers 8080:8080` → http://localhost:8080) — the
first-run setup wizard creates the admin account. For unattended installs set
`auth.adminEmail` + `auth.adminPasswordSecret` (Secret key `adminPassword`).

## Values (highlights)

| Value | Default | Notes |
|---|---|---|
| `persistence.enabled` / `size` | `true` / `2Gi` | `/app/data` PVC: SQLite DB, JSON stores, keys, certs. `existingClaim` supported. |
| `database.provider` | `sqlite` | `postgres` needs `database.existingSecret` (key `connectionString`) or inline `connectionString`. Strategy stays Recreate either way (JSON stores remain on the PVC until changeme C7). |
| `vault.existingSecret` / `key` | unset | Secret key `vaultKey` → `VAULT_KEY`. Strongly recommended — without it the vault, kubeconfig-based K8s servers and encrypted backups are off. |
| `ingress.enabled` | `false` | See WebSockets below. |
| `tailscaleSidecar.enabled` | `false` | Userspace tailscaled (no NET_ADMIN / /dev/net/tun) for outbound mesh access; `authKeySecret` key `authKey`. |
| `localDocker.enabled` | `false` | Mounts the node's Docker socket. Root-equivalent on that node + node pinning — almost never what you want. |
| `trustedProxyCidrs` | `[]` | Whiskers trusts RFC1918 + loopback + Tailscale CGNAT for `X-Forwarded-*`. Add your pod CIDR only if it is outside RFC1918. |
| `auth.adminEmail` / `adminPasswordSecret` | unset | Unattended admin seed (`WHISKERS_ADMIN_EMAIL` + password file mount). |

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
`/healthz` (liveness+startup) and `/readyz` (readiness).

## Backup

The PVC is the state. Use Whiskers' built-in self-backup (Settings → Backup & Restore /
scheduled SelfBackup task) for an application-consistent, optionally VAULT_KEY-encrypted
archive — or snapshot the PVC with your CSI tooling while the pod is stopped.

## Postgres variant

```bash
helm install whiskers deploy/helm/whiskers \
  --set database.provider=postgres \
  --set database.existingSecret=whiskers-db   # key: connectionString
```

The identity + metrics schemas migrate on boot (dual migration assemblies, ADR-0004).
