# Services/Workloads/Kubernetes

The Kubernetes side of the workload seam (kubernetesImplement Track B.2): pods appear next to Docker containers
through `IWorkloadProvider`. MCP tools for pods are planned (Track B.3); today pods are operated from the UI.

| File | What |
|---|---|
| `KubernetesWorkloadProvider.cs` | Lists pods as workloads (id `{namespace}/{podName}`), start/stop/restart where Kubernetes allows it honestly, logs. |
| `IKubernetesClientCache.cs` / `KubernetesClientCache.cs` | One cached `IKubernetes` client per server, built in memory from the kubeconfig in the vault; never written to disk. |
| `KubeconfigPolicy.cs` | Which kubeconfigs reach the client at all (R1-42, E-09): refuses `exec` credential plugins (they would start a program on the Whiskers host) and auth providers that refresh against a URL from the file. Pinned by `KubeconfigExecutionTests`. |
