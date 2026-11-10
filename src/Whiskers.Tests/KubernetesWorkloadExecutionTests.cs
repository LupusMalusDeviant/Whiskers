using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Workloads;
using Whiskers.Services.Workloads.Kubernetes;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0012 (D8, Q7): a Kubernetes workload is resolved with its controller and their UIDs, and the provider acts
/// on exactly that controller — the patch names it and carries its UID as the precondition, a bare pod is deleted with
/// its UID as <c>DeleteOptions.Preconditions</c> — without reading the pod again. The cluster is a recording HTTP
/// handler behind the real client; a live check on a k3s cluster is a manual item of the contract.
/// </summary>
public sealed class KubernetesWorkloadExecutionTests
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    private readonly FakeCluster _cluster = new();
    private readonly KubernetesWorkloadProvider _provider;
    private readonly MutationTargetResolver _resolver;

    public KubernetesWorkloadExecutionTests()
    {
        var server = new Whiskers.Models.ServerConfig { Id = "k3s", Name = "k3s", IsDefault = true };
        var client = new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "http://k8s.test" }, _cluster);
        _provider = new KubernetesWorkloadProvider(server, new Clients(client));
        var services = new ServiceCollection()
            .AddSingleton<IServerConfigService>(new FakeServerConfig(server))
            .AddSingleton<IWorkloadProviderFactory>(new Factory(_provider))
            .BuildServiceProvider();
        _resolver = new MutationTargetResolver(services);
    }

    private static MutationRequest Request(string capability, string workloadId) =>
        new(capability, Admin, MutationSurface.Ui, new MutationTarget("workload", workloadId, null, "k3s"),
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                $$"""{"workloadId":"{{workloadId}}","serverId":"k3s"}""")!);

    private async Task<ResolvedWorkload> ResolveAsync(string capability, string workloadId)
    {
        var resolution = await _resolver.ResolveAsync(Request(capability, workloadId), new CapabilityCatalog().Find(capability)!,
            CancellationToken.None);
        return Assert.IsType<ResolvedWorkload>(resolution.Object);
    }

    [Fact]
    public async Task A_pod_of_a_deployment_resolves_to_the_deployment_and_its_uid()
    {
        var workload = await ResolveAsync("workload.restart", "prod/pod-x1");

        Assert.Equal(("prod", "Deployment", "api", "dep-uid-1", "pod-uid-1"),
            (workload.Namespace, workload.ControllerKind, workload.ControllerName, workload.ControllerUid, workload.PodUid));
        Assert.Equal("prod/pod-x1", workload.Id);
        Assert.Equal("k3s", workload.ServerId);
    }

    [Fact]
    public async Task A_restart_patches_the_decided_deployment_with_its_uid_and_reads_no_pod()
    {
        var workload = await ResolveAsync("workload.restart", "prod/pod-x1");
        _cluster.Requests.Clear();

        await _provider.RestartAsync(workload);

        var patch = Assert.Single(_cluster.Requests);
        Assert.Equal(("PATCH", "/apis/apps/v1/namespaces/prod/deployments/api"), (patch.Method, patch.Path));
        using var body = JsonDocument.Parse(patch.Body);
        Assert.Equal("dep-uid-1", body.RootElement.GetProperty("metadata").GetProperty("uid").GetString());
        Assert.True(body.RootElement.GetProperty("spec").GetProperty("template").GetProperty("metadata")
            .GetProperty("annotations").TryGetProperty("kubectl.kubernetes.io/restartedAt", out _));
    }

    [Fact]
    public async Task A_stop_scales_the_decided_deployment_to_zero_with_its_uid()
    {
        var workload = await ResolveAsync("workload.stop", "prod/pod-x1");
        _cluster.Requests.Clear();

        await _provider.StopAsync(workload);

        var patch = Assert.Single(_cluster.Requests);
        Assert.Equal(("PATCH", "/apis/apps/v1/namespaces/prod/deployments/api"), (patch.Method, patch.Path));
        using var body = JsonDocument.Parse(patch.Body);
        Assert.Equal("dep-uid-1", body.RootElement.GetProperty("metadata").GetProperty("uid").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("spec").GetProperty("replicas").GetInt32());
    }

    [Fact]
    public async Task A_start_reads_only_the_decided_scale_and_refuses_a_replaced_controller()
    {
        var workload = await ResolveAsync("workload.start", "prod/pod-x1");
        _cluster.Requests.Clear();
        _cluster.DeploymentUid = "dep-uid-2";   // the Deployment was deleted and created again under the same name

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.StartAsync(workload));

        Assert.Contains("replaced after the decision", refused.Message);
        var read = Assert.Single(_cluster.Requests);
        Assert.Equal(("GET", "/apis/apps/v1/namespaces/prod/deployments/api/scale"), (read.Method, read.Path));
    }

    [Fact]
    public async Task A_bare_pod_is_deleted_with_its_uid_as_precondition()
    {
        var workload = await ResolveAsync("workload.stop", "prod/solo");
        Assert.Equal(("None", "solo-uid-1"), (workload.ControllerKind, workload.PodUid));
        _cluster.Requests.Clear();

        await _provider.StopAsync(workload);

        var delete = Assert.Single(_cluster.Requests);
        Assert.Equal(("DELETE", "/api/v1/namespaces/prod/pods/solo"), (delete.Method, delete.Path));
        using var body = JsonDocument.Parse(delete.Body);
        Assert.Equal("solo-uid-1", body.RootElement.GetProperty("preconditions").GetProperty("uid").GetString());
    }

    [Fact]
    public async Task A_precondition_the_cluster_refuses_is_reported_as_replaced()
    {
        var workload = await ResolveAsync("workload.restart", "prod/pod-x1");
        _cluster.Conflict = true;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _provider.RestartAsync(workload));

        Assert.Contains("replaced after the decision", refused.Message);
    }

    [Fact]
    public async Task A_controller_replaced_between_decision_and_run_is_refused_as_changed()
    {
        var capability = new CapabilityCatalog().Find("workload.restart")!;
        var request = Request("workload.restart", "prod/pod-x1");
        var decided = (await _resolver.ResolveAsync(request, capability, CancellationToken.None)).Object!;
        _cluster.DeploymentUid = "dep-uid-2";

        var confirmed = await _resolver.ConfirmAsync(request, capability, decided, CancellationToken.None);

        Assert.Null(confirmed.Target);
        Assert.Equal(MutationReasonCodes.TargetChanged, confirmed.Code);
    }

    [Fact]
    public async Task A_workload_decided_on_another_server_is_refused_before_any_call()
    {
        var other = new KubernetesWorkloadProvider(new Whiskers.Models.ServerConfig { Id = "other", Name = "other" },
            new Clients(new k8s.Kubernetes(new KubernetesClientConfiguration { Host = "http://k8s.test" }, _cluster)));
        var workload = await ResolveAsync("workload.restart", "prod/pod-x1");
        _cluster.Requests.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => other.RestartAsync(workload));

        Assert.Empty(_cluster.Requests);
    }

    // ---------------------------------------------------------------- doubles

    private sealed class Clients(IKubernetes client) : IKubernetesClientCache
    {
        public IKubernetes GetClient(string serverId) => client;
        public void Invalidate(string serverId) { }
    }

    private sealed class Factory(IWorkloadProvider provider) : IWorkloadProviderFactory
    {
        public IWorkloadProvider GetForServer(string serverId) => provider;
    }

    /// <summary>The API server: a pod of the Deployment "api" (through its ReplicaSet), a bare pod, the Deployment's
    /// scale and the writes, each recorded.</summary>
    private sealed class FakeCluster : DelegatingHandler
    {
        public sealed record Call(string Method, string Path, string Body);

        public List<Call> Requests { get; } = [];
        public string DeploymentUid { get; set; } = "dep-uid-1";
        public bool Conflict { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add(new Call(request.Method.Method, path, body));
            object? answer = (request.Method.Method, path) switch
            {
                ("GET", "/api/v1/namespaces/prod/pods/pod-x1") => new V1Pod
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = "pod-x1", NamespaceProperty = "prod", Uid = "pod-uid-1",
                        OwnerReferences = [new V1OwnerReference
                        {
                            Kind = "ReplicaSet", Name = "api-7d9f8", Uid = "rs-uid-1", Controller = true, ApiVersion = "apps/v1",
                        }],
                    },
                },
                ("GET", "/api/v1/namespaces/prod/pods/solo") => new V1Pod
                {
                    Metadata = new V1ObjectMeta { Name = "solo", NamespaceProperty = "prod", Uid = "solo-uid-1" },
                },
                ("GET", "/apis/apps/v1/namespaces/prod/replicasets/api-7d9f8") => new V1ReplicaSet
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = "api-7d9f8", NamespaceProperty = "prod", Uid = "rs-uid-1",
                        OwnerReferences = [new V1OwnerReference
                        {
                            Kind = "Deployment", Name = "api", Uid = DeploymentUid, Controller = true, ApiVersion = "apps/v1",
                        }],
                    },
                },
                ("GET", "/apis/apps/v1/namespaces/prod/deployments/api/scale") => new V1Scale
                {
                    Metadata = new V1ObjectMeta { Name = "api", NamespaceProperty = "prod", Uid = DeploymentUid },
                    Spec = new V1ScaleSpec { Replicas = 0 },
                },
                ("PATCH", "/apis/apps/v1/namespaces/prod/deployments/api") when !Conflict =>
                    new V1Deployment { Metadata = new V1ObjectMeta { Name = "api", NamespaceProperty = "prod" } },
                ("DELETE", "/api/v1/namespaces/prod/pods/solo") =>
                    new V1Pod { Metadata = new V1ObjectMeta { Name = "solo", NamespaceProperty = "prod" } },
                _ => null,
            };
            if (answer is null)
                return new HttpResponseMessage(Conflict ? HttpStatusCode.Conflict : HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                    Content = new StringContent("""{"kind":"Status","apiVersion":"v1","status":"Failure"}""",
                        Encoding.UTF8, "application/json"),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(KubernetesJson.Serialize(answer), Encoding.UTF8, "application/json"),
            };
        }
    }
}
