using k8s.Models;
using Whiskers.Services.Workloads.Kubernetes;

namespace Whiskers.Tests;

/// <summary>Track B.2 — pure unit tests for the Kubernetes workload provider's mapping logic
/// (no cluster needed): pod → ContainerInfo, owner-group resolution (incl. the ReplicaSet
/// hash-trim to the Deployment name) and the {namespace}/{podName} id scheme.</summary>
public class KubernetesWorkloadProviderTests
{
    private static V1Pod Pod(
        string name = "web-7d9c5b6f4-abcde", string ns = "apps", string phase = "Running",
        string? ownerKind = "ReplicaSet", string? ownerName = "web-7d9c5b6f4",
        bool ready = true, string image = "nginx:1.27")
    {
        var owner = ownerKind is null ? ((string Kind, string Name)?)null : (ownerKind, ownerName ?? "");
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                CreationTimestamp = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
                Labels = new Dictionary<string, string> { ["app"] = "web" },
                OwnerReferences = owner is { } o
                    ? new List<V1OwnerReference> { new() { Kind = o.Kind, Name = o.Name, Controller = true } }
                    : null,
            },
            Spec = new V1PodSpec
            {
                Containers = new List<V1Container> { new() { Name = "main", Image = image } },
            },
            Status = new V1PodStatus
            {
                Phase = phase,
                ContainerStatuses = new List<V1ContainerStatus>
                {
                    new() { Name = "main", Ready = ready },
                },
            },
        };
        return pod;
    }

    // --- Id scheme ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("apps/web-abc", true, "apps", "web-abc")]
    [InlineData("default/x", true, "default", "x")]
    [InlineData("no-slash", false, "", "")]
    [InlineData("/leading", false, "", "")]
    [InlineData("trailing/", false, "", "")]
    [InlineData("a/b/c", false, "", "")]
    [InlineData("", false, "", "")]
    public void Id_parsing_round_trips_and_rejects_malformed(string id, bool ok, string ns, string name)
    {
        Assert.Equal(ok, KubernetesWorkloadProvider.TryParseId(id, out var parsedNs, out var parsedName));
        if (ok)
        {
            Assert.Equal(ns, parsedNs);
            Assert.Equal(name, parsedName);
        }
    }

    // --- Owner resolution ---------------------------------------------------------------------------

    [Fact]
    public void ReplicaSet_owner_folds_to_the_deployment_name()
    {
        var (kind, name) = KubernetesWorkloadProvider.ResolveOwner(Pod());
        Assert.Equal("Deployment", kind);
        Assert.Equal("web", name);
    }

    [Fact]
    public void StatefulSet_and_DaemonSet_owners_are_used_directly()
    {
        Assert.Equal(("StatefulSet", "db"), KubernetesWorkloadProvider.ResolveOwner(Pod(ownerKind: "StatefulSet", ownerName: "db")));
        Assert.Equal(("DaemonSet", "node-agent"), KubernetesWorkloadProvider.ResolveOwner(Pod(ownerKind: "DaemonSet", ownerName: "node-agent")));
    }

    [Fact]
    public void Bare_pod_has_owner_kind_none()
    {
        Assert.Equal(("None", ""), KubernetesWorkloadProvider.ResolveOwner(Pod(ownerKind: null)));
    }

    // --- Pod mapping ---------------------------------------------------------------------------------

    [Fact]
    public void Running_ready_pod_maps_to_running_healthy_with_owner_group()
    {
        var w = KubernetesWorkloadProvider.ToWorkload(Pod(), "srv-k8s", "Cluster 1");
        Assert.Equal("apps/web-7d9c5b6f4-abcde", w.Id);
        Assert.Equal("running", w.State);
        Assert.Equal("healthy", w.HealthStatus);
        Assert.Equal("Running (1/1 ready)", w.Status);
        Assert.Equal("nginx:1.27", w.Image);
        Assert.Equal("srv-k8s", w.ServerId);
        Assert.Equal("Cluster 1", w.ServerName);
        // The compose-project label carries the owner group so the dashboard grouping just works.
        Assert.Equal("web", w.ComposeProject);
    }

    [Fact]
    public void Running_but_not_ready_pod_is_unhealthy()
    {
        var w = KubernetesWorkloadProvider.ToWorkload(Pod(ready: false), "s", "S");
        Assert.Equal("running", w.State);
        Assert.Equal("unhealthy", w.HealthStatus);
    }

    [Theory]
    [InlineData("Succeeded", "exited")]
    [InlineData("Failed", "exited")]
    [InlineData("Pending", "created")]
    [InlineData("Unknown", "created")]
    public void Phases_map_to_container_states(string phase, string expected)
    {
        Assert.Equal(expected, KubernetesWorkloadProvider.ToWorkload(Pod(phase: phase), "s", "S").State);
    }

    [Fact]
    public void Bare_pod_groups_as_standalone()
    {
        var w = KubernetesWorkloadProvider.ToWorkload(Pod(ownerKind: null), "s", "S");
        Assert.Equal("Standalone", w.ComposeProject);
    }

    [Fact]
    public void Pod_labels_are_carried_over()
    {
        var w = KubernetesWorkloadProvider.ToWorkload(Pod(), "s", "S");
        Assert.Equal("web", w.Labels["app"]);
    }
}
