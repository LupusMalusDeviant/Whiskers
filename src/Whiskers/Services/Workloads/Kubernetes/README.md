# Services/Workloads/Kubernetes

The Kubernetes side of the workload seam (kubernetesImplement Track B.2): pods appear next to Docker containers
through `IWorkloadProvider`. MCP tools for pods are planned (Track B.3); today pods are operated from the UI.

| File | What |
|---|---|
| `KubernetesWorkloadProvider.cs` | Lists pods as workloads (id `{namespace}/{podName}`), start/stop/restart where Kubernetes allows it honestly, logs. Contract 0012 (D8): `GetWorkloadIdentityAsync` reads the pod and its controller with their UIDs (a ReplicaSet through its owner reference to the Deployment); start/stop/restart act on the `ResolvedWorkload` the gateway decided — a merge patch of that controller carrying `metadata.uid` (the API server refuses it with 409 when the controller was replaced), a bare pod deleted with its UID as precondition — and read no pod. Needs `get` on pods and replicasets, `get` on `deployments/scale` and `statefulsets/scale`, `patch` on deployments, statefulsets and daemonsets, `delete` on pods. Pinned by `KubernetesWorkloadExecutionTests` (recording API server); a live check on a k3s cluster is a manual item. |
| `IKubernetesClientCache.cs` / `KubernetesClientCache.cs` | One cached `IKubernetes` client per server, built in memory from the kubeconfig in the vault; never written to disk. |
| `KubeconfigPolicy.cs` | Which kubeconfigs reach the client at all (R1-42, E-09). A kubeconfig must be self-contained: one that names an `exec` plugin (it would start a program on the Whiskers host), any `auth-provider`, or a local file (`client-certificate`, `client-key`, `certificate-authority` paths), for any user or cluster, is refused with no setting to allow it. Inline credentials are supported. Checked when the kubeconfig is saved and every time a client is built; pinned by `KubeconfigExecutionTests`. |
