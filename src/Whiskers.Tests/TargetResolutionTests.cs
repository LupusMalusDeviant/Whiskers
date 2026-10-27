using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Cloud;
using Whiskers.Models.Hetzner;
using Whiskers.Services.Approvals;
using Whiskers.Services.AutoUpdate;
using Whiskers.Services.Cloud;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.Scheduler;
using Whiskers.Services.Server;
using Whiskers.Services.Webhooks;
using Whiskers.Services.Workloads;

namespace Whiskers.Tests;

/// <summary>
/// R1-06 (INV-24, INV-57): every target is resolved against the inventory before policy — servers, containers,
/// cloud servers, networks with their attached containers, scheduled tasks, DNS zones and a workload's live
/// controller — unknown or ambiguous ones are refused, and execution runs on the resolved identity. "local" and a
/// remote default server stand side by side throughout: decision and execution always pick the same one.
/// </summary>
public sealed class TargetResolutionTests : IDisposable
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    /// <summary>The machine Whiskers runs on, and a remote server that is the configured default.</summary>
    private static readonly ServerConfig Local = new() { Id = "local", Name = "Local" };
    private static readonly ServerConfig Edge = new() { Id = "edge01", Name = "Edge", IsDefault = true };

    private readonly FakeServerConfig _servers = new(Local, Edge);
    private readonly FakeDocker _docker = new(
        new ContainerInfo { Id = "aaa111local", Name = "web", ServerId = "local" },
        new ContainerInfo { Id = "bbb222edge", Name = "web", ServerId = "edge01" },
        new ContainerInfo { Id = "ccc333local", Name = "whiskers", ServerId = "local" },
        new ContainerInfo { Id = "bbb999edge", Name = "api", ServerId = "edge01" })
    {
        DefaultServerId = "edge01",
    };

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"targets-{Guid.NewGuid():N}.db");
    private ServiceProvider? _db;

    // ---------------------------------------------------------------- servers and containers

    [Fact]
    public async Task A_call_without_a_server_acts_on_the_default_server_and_says_so()
    {
        var (outcome, seen) = await RunAsync("container.restart", new { containerId = "web" });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(new MutationTarget("container", "bbb222edge", "web", "edge01"), seen! with { Resolved = false });
    }

    [Theory]
    [InlineData("local", "aaa111local", "local")]
    [InlineData("LOCAL", "aaa111local", "local")]     // an id differing only in case
    [InlineData("Edge", "bbb222edge", "edge01")]      // a server name
    [InlineData("edge01", "bbb222edge", "edge01")]
    public async Task Local_and_the_remote_default_are_never_confused(string serverId, string container, string server)
    {
        var (outcome, seen) = await RunAsync("container.restart", new { containerId = "web", serverId });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(container, seen!.Id);
        Assert.Equal(server, seen.ServerId);
    }

    [Fact]
    public async Task Execution_runs_on_the_resolved_identity_not_on_what_the_caller_typed()
    {
        var capability = new CapabilityCatalog().Find("container.restart")!;
        var args = Args(new { containerId = "aaa1", serverId = "LOCAL" });
        var (_, seen) = await RunAsync("container.restart", new { containerId = "aaa1", serverId = "LOCAL" });

        var canonical = MutationTargets.CanonicalArguments(capability, args, seen!);

        Assert.Equal("aaa111local", canonical["containerId"].GetString());
        Assert.Equal("local", canonical["serverId"].GetString());
    }

    [Fact]
    public void A_key_the_caller_did_not_pass_is_not_added()
    {
        var capability = new CapabilityCatalog().Find("container.restart")!;
        var resolved = new MutationTarget("container", "bbb222edge", "web", "edge01") { Resolved = true };

        var canonical = MutationTargets.CanonicalArguments(capability, Args(new { containerId = "web" }), resolved);

        Assert.False(canonical.ContainsKey("serverId"));   // a tool without that parameter must not receive it
        Assert.Equal("bbb222edge", canonical["containerId"].GetString());
    }

    [Fact]
    public void A_tool_that_takes_a_server_always_receives_the_resolved_one()
    {
        // set_container_env reads a missing server as "local"; the gateway decided on the default server.
        var capability = new CapabilityCatalog().Find("container.env_set")!;
        var resolved = new MutationTarget("container", "bbb222edge", "web", "edge01") { Resolved = true };

        var canonical = MutationTargets.CanonicalArguments(capability, Args(new { containerId = "web" }), resolved,
            takesServerId: Whiskers.Mcp.McpToolParameters.TakesServerId("set_container_env"));

        Assert.Equal("edge01", canonical["serverId"].GetString());
        Assert.True(Whiskers.Mcp.McpToolParameters.TakesServerId("restart_container"));
        Assert.False(Whiskers.Mcp.McpToolParameters.TakesServerId("cloud_reboot"));
    }

    [Theory]
    [InlineData("no-such-server", MutationReasonCodes.TargetUnknown)]
    [InlineData("twin", MutationReasonCodes.TargetAmbiguous)]
    public async Task A_refused_target_carries_a_reason_code_for_the_users_language(string serverId, string code)
    {
        _servers.GetServers().Add(new ServerConfig { Id = "twin-a", Name = "twin" });
        _servers.GetServers().Add(new ServerConfig { Id = "twin-b", Name = "Twin" });

        var (outcome, _) = await RunAsync("checks.pause", new { serverId, reason = "test" });

        Assert.Equal(code, outcome.ReasonCode);
        Assert.Equal([serverId], outcome.ReasonArgs);
    }

    [Theory]
    [InlineData("no-such-server", "Unknown server")]
    [InlineData("twin", "Ambiguous server")]
    public async Task An_unknown_or_ambiguous_server_is_refused_before_policy(string serverId, string reason)
    {
        _servers.GetServers().Add(new ServerConfig { Id = "twin-a", Name = "twin" });
        _servers.GetServers().Add(new ServerConfig { Id = "twin-b", Name = "Twin" });

        var (outcome, seen) = await RunAsync("checks.pause", new { serverId, reason = "test" });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains(reason, outcome.Reason);
        Assert.Null(seen);
    }

    [Fact]
    public async Task A_host_operation_without_a_server_is_recorded_on_the_default_server_not_on_local()
    {
        var (outcome, seen) = await RunAsync("host.prune", new { });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal("edge01", seen!.Id);
        Assert.Equal("edge01", seen.ServerId);
    }

    [Theory]
    [InlineData("")]        // no id yet: not the default server
    [InlineData("edge")]    // a new id that only matches an existing server's name
    public async Task Saving_a_new_server_is_never_decided_on_another_one(string serverId)
    {
        var (outcome, seen) = await RunAsync("server.save", new { serverId, name = "New" });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.False(seen!.Resolved);
        Assert.NotEqual("edge01", seen.Id);
    }

    [Theory]
    [InlineData("server.save", "edge01")]
    [InlineData("server.save", "EDGE01")]
    [InlineData("server.onboard", "edge01")]
    public async Task Saving_or_onboarding_an_existing_server_is_decided_on_that_server(string capability, string serverId)
    {
        var (outcome, seen) = await RunAsync(capability, new { serverId, name = "Edge" });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.True(seen!.Resolved);
        Assert.Equal("edge01", seen.Id);
    }

    [Fact]
    public async Task A_new_server_does_not_have_to_exist_yet()
    {
        var (outcome, _) = await RunAsync("server.onboard", new { serverId = "new-box", name = "New box" });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
    }

    // ---------------------------------------------------------------- cloud servers

    [Theory]
    [InlineData("hz-1", "hz-1")]
    [InlineData("hetzner box", "hz-1")]     // by name, ignoring case
    public async Task A_cloud_server_resolves_by_id_or_name_to_its_whiskers_id(string server, string id)
    {
        var (outcome, seen) = await RunAsync("cloud.reboot", new { server });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(id, seen!.Id);
        Assert.Equal("Hetzner Box", seen.Name);
    }

    [Theory]
    [InlineData("nope", "Unknown cloud server")]
    [InlineData("dup", "Ambiguous cloud server")]
    public async Task An_unknown_or_ambiguous_cloud_server_is_refused(string server, string reason)
    {
        var (outcome, _) = await RunAsync("cloud.power_on", new { server });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains(reason, outcome.Reason);
    }

    // ---------------------------------------------------------------- networks

    [Fact]
    public async Task Removing_a_network_a_protected_container_is_attached_to_is_denied()
    {
        _docker.Networks.Add(Network("net-app-1234", "appnet", "local", ("ccc333local", "whiskers"), ("aaa111local", "web")));

        var (outcome, _) = await RunAsync("network.remove", new { networkId = "appnet", serverId = "local" },
            protect: "whiskers*");

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("protected", outcome.Reason);
    }

    [Fact]
    public async Task Connecting_a_container_is_decided_on_that_container_not_on_the_ones_already_attached()
    {
        _docker.Networks.Add(Network("net-app-1234", "appnet", "local", ("ccc333local", "whiskers")));

        var (web, seen) = await RunAsync("network.connect",
            new { networkId = "net-app", containerId = "web", serverId = "local" }, protect: "whiskers*");
        var (whiskers, _) = await RunAsync("network.connect",
            new { networkId = "appnet", containerId = "whiskers", serverId = "local" }, protect: "whiskers*");

        Assert.Equal(MutationStatus.Executed, web.Status);
        Assert.Equal("net-app-1234", seen!.Id);                  // a unique id prefix, made canonical
        Assert.Equal("aaa111local", seen.RelatedContainerId);    // web on local, not web on the default server
        Assert.Equal(MutationStatus.Denied, whiskers.Status);
    }

    [Theory]
    [InlineData("nope", "Network not found")]
    [InlineData("net-", "Ambiguous network")]
    public async Task An_unknown_or_ambiguous_network_is_refused(string networkId, string reason)
    {
        _docker.Networks.Add(Network("net-a", "a", "edge01"));
        _docker.Networks.Add(Network("net-b", "b", "edge01"));

        var (outcome, _) = await RunAsync("network.remove", new { networkId });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains(reason, outcome.Reason);
    }

    [Fact]
    public async Task A_network_is_looked_up_on_the_server_the_call_names()
    {
        _docker.Networks.Add(Network("net-local", "shared", "local"));
        _docker.Networks.Add(Network("net-edge", "shared", "edge01"));

        var (_, onLocal) = await RunAsync("network.remove", new { networkId = "shared", serverId = "local" });
        var (_, onDefault) = await RunAsync("network.remove", new { networkId = "shared" });

        Assert.Equal("net-local", onLocal!.Id);
        Assert.Equal("net-edge", onDefault!.Id);
    }

    // ---------------------------------------------------------------- scheduled tasks

    [Theory]
    [InlineData("t-1", "t-1")]
    [InlineData("nightly", "t-1")]    // a unique name
    public async Task A_task_resolves_by_id_or_unique_name(string taskId, string id)
    {
        var (outcome, seen) = await RunAsync("scheduler.delete", new { taskId });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(id, seen!.Id);
        Assert.Equal("nightly", seen.Name);
    }

    [Theory]
    [InlineData("gone", "not found")]
    [InlineData("twice", "Ambiguous task")]
    public async Task An_unknown_or_ambiguous_task_is_refused(string taskId, string reason)
    {
        var (outcome, _) = await RunAsync("scheduler.delete", new { taskId });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains(reason, outcome.Reason);
    }

    // ---------------------------------------------------------------- DNS zones

    [Fact]
    public async Task A_zone_is_normalised_and_must_be_an_allowed_one()
    {
        var (allowed, seen) = await RunAsync("dns.record_set", new { zone = "Example.ORG.", name = "www" });
        var (other, _) = await RunAsync("dns.record_set", new { zone = "other.net", name = "www" });
        var (invalid, _) = await RunAsync("dns.record_set", new { zone = "not a zone", name = "www" });

        Assert.Equal(MutationStatus.Executed, allowed.Status);
        Assert.Equal("example.org", seen!.Id);
        Assert.Equal(MutationStatus.Denied, other.Status);
        Assert.Contains("not one of the allowed zones", other.Reason);
        Assert.Equal(MutationStatus.Denied, invalid.Status);
    }

    // ---------------------------------------------------------------- Kubernetes workloads

    [Fact]
    public async Task A_workload_is_decided_on_its_live_controller_not_on_the_name_the_page_rendered()
    {
        // The page still shows the pod under its old controller; meanwhile it belongs to "api" (protected).
        var (outcome, seen) = await RunAsync("workload.restart",
            new { workloadId = "prod/pod-x1", name = "old-frontend", serverId = "local" }, protect: "api*");

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("protected", outcome.Reason);
        Assert.Null(seen);
    }

    [Fact]
    public async Task A_workload_that_no_longer_exists_is_refused()
    {
        var (outcome, _) = await RunAsync("workload.stop", new { workloadId = "prod/gone", serverId = "local" });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("Workload not found", outcome.Reason);
    }

    // ---------------------------------------------------------------- webhooks and AutoUpdate

    [Fact]
    public async Task A_webhook_without_a_server_is_decided_and_run_on_local_even_when_another_server_is_default()
    {
        var hooks = WebhookService();
        var hook = await hooks.CreateWebhookAsync(new WebhookEntity { Name = "redeploy", TargetId = "web", Action = "restart" });

        // The Webhooks page's gate decides on this container …
        var (decided, _) = await WebhookTargets.ContainerAsync(_docker, hook);
        // … and the trigger runs on the same one.
        var (ok, output) = await hooks.TriggerSignedTestAsync(hook.WebhookId);

        Assert.True(ok, output);
        Assert.Equal("aaa111local", decided!.Id);
        Assert.Equal(("restart", "aaa111local", (string?)"local"), Assert.Single(_docker.Actions));
    }

    [Fact]
    public async Task A_webhook_naming_an_ambiguous_id_prefix_acts_on_nothing()
    {
        var hooks = WebhookService();
        var hook = await hooks.CreateWebhookAsync(new WebhookEntity
        {
            Name = "redeploy", TargetId = "b", Action = "restart", ServerId = "edge01",
        });

        var (ok, output) = await hooks.TriggerSignedTestAsync(hook.WebhookId);

        Assert.False(ok);
        Assert.Contains("Ambiguous", output);
        Assert.Empty(_docker.Actions);
    }

    [Fact]
    public async Task AutoUpdate_decides_and_recreates_a_local_container_on_local_not_on_the_default_server()
    {
        var sp = Db();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.UpdatePolicies.Add(new UpdatePolicyEntity
            {
                ContainerId = "aaa111local", ContainerName = "web", ServerId = "local", AutoUpdate = true,
                NotifyBeforeUpdate = false,
            });
            await db.SaveChangesAsync();
        }
        var updates = new UpdatesAvailable();
        updates.Set(new ImageUpdateInfo { ContainerId = "aaa111local", ContainerName = "web", ServerId = "local", UpdateAvailable = true });
        var owners = new MemoryAutomationOwners();
        await owners.SetAsync(IAutomationOwnerStore.AutoUpdate, "user:admin@example.com");
        var (runner, resolved) = AdminRunner();
        _docker.AllowMutations = true;

        var service = new AutoUpdateService(sp.GetRequiredService<IServiceScopeFactory>(), _docker, updates,
            new FakeNotifications(), null!, runner, owners, NullLogger<AutoUpdateService>.Instance);
        await service.CheckAndUpdateAsync(CancellationToken.None);

        var decided = Assert.Single(resolved);
        Assert.Equal("aaa111local", decided.Id);
        Assert.Equal("local", decided.ServerId);
        Assert.Equal(("recreate", "aaa111local", (string?)"local"), Assert.Single(_docker.Actions));
    }

    [Fact]
    public async Task A_scheduled_task_without_a_server_is_decided_and_run_on_local_even_when_another_server_is_default()
    {
        var (runner, resolved) = AdminRunner();
        _docker.AllowMutations = true;
        var executor = new TaskExecutor(_docker, null!, null!, null!, null!, runner,
            NullLogger<TaskExecutor>.Instance, notify: new FakeNotifications());

        var (ok, output) = await executor.ExecuteAsync(new ScheduledTaskEntity
        {
            TaskId = "t-9", Name = "nightly", TaskType = ScheduledTaskType.ContainerRestart, TargetId = "web",
            CreatedBy = "user:admin@example.com", CreatedVia = "user:admin@example.com",
        });

        Assert.True(ok, output);
        var decided = Assert.Single(resolved);
        Assert.Equal(("aaa111local", "local"), (decided.Id, decided.ServerId));
        Assert.Equal(("restart", "web", (string?)"local"), Assert.Single(_docker.Actions));
        Assert.Equal("local", ScheduledTaskServer.Of(new ScheduledTaskEntity()));
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(MutationOutcome<int> Outcome, MutationTarget? Seen)> RunAsync(string capabilityId, object arguments,
        string? protect = null)
    {
        var catalog = new CapabilityCatalog();
        var capability = catalog.Find(capabilityId)!;
        var args = Args(arguments);
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        policy.ProtectedResources = protect is null ? [] : [protect];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var gateway = new MutationGateway(catalog, AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            targets: Resolver(), approvals: new ApprovalGate(new ApprovalStore(), store));
        MutationTarget? seen = null;
        var outcome = await gateway.ExecuteAsync(
            new MutationRequest(capabilityId, Admin, MutationSurface.Mcp, MutationTargets.FromArguments(capability, args), args),
            (ctx, _) => { seen = ctx.Request.Target; return Task.FromResult(0); });
        return (outcome, seen);
    }

    private MutationTargetResolver Resolver() => new(new TestServices(
        _docker, _servers, new Cloud(), new Scheduler(), new Workloads(),
        new StaticOptionsMonitor<DnsSettings>(new DnsSettings { AllowedZones = ["example.org"] })));

    /// <summary>An automation runner on a gateway with the real resolver, owned by an Admin; records every
    /// target the resolver decided on.</summary>
    private (AutomationMutationRunner Runner, List<MutationTarget> Resolved) AdminRunner()
    {
        var f = AutomationMutationRunnerTests.Build(requireOwner: true);
        f.Whitelist.Allowed.Add("admin@example.com");
        f.Roles.Map["admin@example.com"] = AppRole.Admin;
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var recording = new RecordingResolver(Resolver());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            targets: recording, approvals: new ApprovalGate(new ApprovalStore(), store));
        var runner = new AutomationMutationRunner(gateway, new CapabilityCatalog(),
            new AutomationOwnerResolver(f.Roles, f.Whitelist, f.Keys, f.Accounts),
            new StaticOptionsMonitor<AutomationOptions>(f.Options));
        return (runner, recording.Resolved);
    }

    private WebhookService WebhookService() =>
        new(Db().GetRequiredService<IServiceScopeFactory>(), Docker(), new NoHost(), NullLogger<WebhookService>.Instance);

    private FakeDocker Docker()
    {
        _docker.AllowMutations = true;
        return _docker;
    }

    private ServiceProvider Db()
    {
        if (_db is not null) return _db;
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _db = services.BuildServiceProvider();
        using var scope = _db.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
        return _db;
    }

    public void Dispose()
    {
        _db?.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* temp file */ }
    }

    private static Dictionary<string, JsonElement> Args(object arguments) =>
        JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private static NetworkInfo Network(string id, string name, string serverId, params (string Id, string Name)[] attached) =>
        new()
        {
            Id = id, Name = name, ServerId = serverId,
            Containers = attached.Select(c => new NetworkContainer { ContainerId = c.Id, Name = c.Name }).ToList(),
        };

    private sealed class RecordingResolver(IMutationTargetResolver inner) : IMutationTargetResolver
    {
        public List<MutationTarget> Resolved { get; } = [];

        public async Task<TargetResolution> ResolveAsync(MutationRequest request, Capability capability, CancellationToken ct)
        {
            var resolution = await inner.ResolveAsync(request, capability, ct);
            if (resolution.Target is { } target) Resolved.Add(target);
            return resolution;
        }
    }

    private sealed class Cloud : ICloudControlService
    {
        private readonly List<ServerConfig> _servers =
        [
            new() { Id = "hz-1", Name = "Hetzner Box", CloudProvider = CloudProvider.Hetzner },
            new() { Id = "dup-1", Name = "dup", CloudProvider = CloudProvider.Hetzner },
            new() { Id = "dup-2", Name = "DUP", CloudProvider = CloudProvider.Hetzner },
        ];

        public List<ServerConfig> CloudServers() => _servers;
        public ServerConfig? ResolveWhiskers(string idOrName) => throw new NotSupportedException();
        public Task<CloudServerInfo?> ResolveAsync(ServerConfig sw) => throw new NotSupportedException();
        public Task<List<CloudServerInfo>> ListAllAsync() => throw new NotSupportedException();
        public Task<string> PowerOnAsync(string idOrName) => throw new NotSupportedException();
        public Task<string> ShutdownAsync(string idOrName) => throw new NotSupportedException();
        public Task<string> RebootAsync(string idOrName) => throw new NotSupportedException();
        public Task<string> HardResetAsync(string idOrName) => throw new NotSupportedException();
        public Task<string> CreateSnapshotAsync(string idOrName, string? description) => throw new NotSupportedException();
        public Task<string> MetricsAsync(string idOrName, string type) => throw new NotSupportedException();
        public Task<(string token, HetznerServer server)?> HetznerContextAsync(string idOrName) => throw new NotSupportedException();
    }

    private sealed class Scheduler : ISchedulerService
    {
        private readonly List<ScheduledTaskEntity> _tasks =
        [
            new() { TaskId = "t-1", Name = "nightly", TaskType = ScheduledTaskType.ContainerRestart, CronExpression = "0 2 * * *" },
            new() { TaskId = "t-2", Name = "twice", TaskType = ScheduledTaskType.ContainerRestart, CronExpression = "0 2 * * *" },
            new() { TaskId = "t-3", Name = "twice", TaskType = ScheduledTaskType.ContainerRestart, CronExpression = "0 3 * * *" },
        ];

        public Task<List<ScheduledTaskEntity>> GetTasksAsync() => Task.FromResult(_tasks.ToList());
        public Task<ScheduledTaskEntity> CreateTaskAsync(ScheduledTaskEntity task) => throw new NotSupportedException();
        public Task UpdateTaskAsync(ScheduledTaskEntity task) => throw new NotSupportedException();
        public Task DeleteTaskAsync(string taskId) => throw new NotSupportedException();
        public Task<(bool Success, string Output)> RunNowAsync(string taskId) => throw new NotSupportedException();
        public Task<bool> SetOwnerAsync(string taskId, string ownerId, string? expectedOwnerId, string? createdVia = null) =>
            throw new NotSupportedException();
        public Task<List<TaskRunHistoryEntity>> GetHistoryAsync(string? taskId = null, int limit = 50) =>
            throw new NotSupportedException();
    }

    /// <summary>One pod on "local", currently owned by the Deployment "api".</summary>
    private sealed class Workloads : IWorkloadProviderFactory, IWorkloadProvider
    {
        public IWorkloadProvider GetForServer(string serverId) =>
            serverId == "local" ? this : throw new InvalidOperationException($"No workloads on {serverId}");

        public string ServerId => "local";
        public WorkloadCapabilities Capabilities => default!;
        public Task<IList<ContainerInfo>> ListWorkloadsAsync(bool all = true, CancellationToken ct = default) =>
            throw new NotSupportedException();

        /// <summary>Mapped by the real Kubernetes provider: the pod's controller reference is a ReplicaSet of the
        /// Deployment "api", which the provider folds to the Deployment.</summary>
        public Task<ContainerInfo?> GetWorkloadAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(id == "prod/pod-x1"
                ? Whiskers.Services.Workloads.Kubernetes.KubernetesWorkloadProvider.ToWorkload(new k8s.Models.V1Pod
                {
                    Metadata = new k8s.Models.V1ObjectMeta
                    {
                        Name = "pod-x1", NamespaceProperty = "prod",
                        OwnerReferences =
                        [
                            new k8s.Models.V1OwnerReference
                            {
                                Kind = "ReplicaSet", Name = "api-7d9f8", Controller = true, ApiVersion = "apps/v1", Uid = "u1",
                            },
                        ],
                    },
                }, "local", "Local")
                : null);

        public Task StartAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestartAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetLogsAsync(string id, int tailLines = 100, DateTime? since = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class NoHost : IHostCommandExecutor
    {
        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null) => throw new NotSupportedException();
    }

    /// <summary>The update check's answers, held in memory.</summary>
    private sealed class UpdatesAvailable : IImageUpdateStore
    {
        private readonly List<ImageUpdateInfo> _items = [];
        public DateTime? LastCheckAt { get; set; }
        public bool IsChecking { get; set; }
        public void Set(ImageUpdateInfo info) => _items.Add(info);
        public ImageUpdateInfo? Get(string containerId, string? serverId = null) =>
            _items.FirstOrDefault(i => i.ContainerId == containerId && i.ServerId == (serverId ?? "local"));
        public IReadOnlyList<ImageUpdateInfo> GetUpdatesForServer(string serverId) => _items.Where(i => i.ServerId == serverId).ToList();
        public IReadOnlyList<ImageUpdateInfo> GetAllPendingUpdates() => _items.Where(i => i.UpdateAvailable).ToList();
        public IReadOnlyList<ImageUpdateInfo> GetAll() => _items;
        public void Remove(string containerId, string? serverId = null) =>
            _items.RemoveAll(i => i.ContainerId == containerId && i.ServerId == (serverId ?? "local"));
        public void Clear() => _items.Clear();
        public Task SaveAsync() => Task.CompletedTask;
    }
}

/// <summary>A service provider over fixed instances: each type resolves to the first instance assignable to it.</summary>
internal sealed class TestServices(params object[] services) : IServiceProvider
{
    public object? GetService(Type serviceType) => services.FirstOrDefault(serviceType.IsInstanceOfType);
}
