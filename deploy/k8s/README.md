# deploy/k8s

Kubernetes assets for connecting a cluster to Whiskers (kubernetesImplement Track B).

## Files

| File | Purpose |
|---|---|
| `whiskers-agent-rbac.yaml` | Minimal ServiceAccount + ClusterRole(-Binding) for the Kubernetes workload provider: read pods/logs, scale + rollout-restart controllers, delete bare pods. No secrets, no exec, no cluster-admin. |

## Connecting a cluster

Whiskers takes a **pasted kubeconfig** in the server dialog (*Server → hinzufügen → Kubernetes-Cluster*).
The kubeconfig is stored **encrypted in the Whiskers vault** (requires `VAULT_KEY`), never on disk.

**Option A — quick (existing kubeconfig):** paste your admin/user kubeconfig. Fine for a first
look; prefer Option B for standing access.

**Option B — least privilege (recommended):**

```bash
kubectl apply -f whiskers-agent-rbac.yaml

# Mint a token for the ServiceAccount (k8s >= 1.24; pick a sensible duration):
TOKEN=$(kubectl -n kube-system create token whiskers-agent --duration=8760h)
SERVER=$(kubectl config view --minify -o jsonpath='{.clusters[0].cluster.server}')
CA=$(kubectl config view --minify --raw -o jsonpath='{.clusters[0].cluster.certificate-authority-data}')

cat <<EOF > whiskers-agent.kubeconfig
apiVersion: v1
kind: Config
clusters:
  - name: cluster
    cluster: { server: ${SERVER}, certificate-authority-data: ${CA} }
users:
  - name: whiskers-agent
    user: { token: ${TOKEN} }
contexts:
  - name: whiskers
    context: { cluster: cluster, user: whiskers-agent }
current-context: whiskers
EOF
```

Paste `whiskers-agent.kubeconfig` into the dialog. Optionally restrict Whiskers further with the
namespace allowlist field (empty = all namespaces the account can see).

## Semantics (honest mapping)

| Whiskers action | Kubernetes effect |
|---|---|
| Stop | Scale the owning Deployment/StatefulSet to 0 (bare pod: delete). DaemonSets refuse. |
| Start | Scale the owner back to 1 (only when currently 0 — multi-replica counts are never stomped). |
| Restart | Rollout-restart (pod-template annotation patch). Bare pods refuse (no controller to bring them back). |
| Stats | Not yet (needs metrics-server — later Track B step). |

Interactive exec and MCP tool coverage for K8s follow in Track B.3.
