using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Whiskers.Mcp;
using Whiskers.Models;
using Whiskers.Services.Backup;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// R1-48 (INV-20/21/38): the inventory of every write Whiskers performs, by the surface it starts on — UI pages, MCP
/// tools and the agent, background services and the scheduler, webhooks and terminals — and the execution boundary the
/// runtime scope guard checks for it. Every capability in the catalog has a boundary: a marked service operation, or,
/// for a raw host command, the executor's primitive called only from the places listed here. Together with
/// <see cref="MutationBypassArchitectureTests"/> (every call to a boundary sits in a decision that covers it) and
/// <see cref="MutationScopeGuardTests"/> (an undecided call throws), nothing that changes infrastructure runs past the
/// gateway.
/// </summary>
public class MutationGatewayCoverageTests
{
    private static readonly Assembly App = typeof(Program).Assembly;
    private static readonly CapabilityCatalog Catalog = new();

    /// <summary>Every interface method marked with a capability, by capability.</summary>
    private static readonly ILookup<string, MethodInfo> Marked = App.GetTypes()
        .Where(t => t.IsInterface)
        .SelectMany(t => t.GetMethods())
        .Select(m => (Method: m, Attr: m.GetCustomAttribute<MutatingOperationAttribute>()))
        .Where(x => x.Attr is not null)
        .ToLookup(x => x.Attr!.CapabilityId, x => x.Method);

    /// <summary>
    /// The capabilities whose execution is a raw command handed to the host executor's primitive
    /// (<see cref="IHostCommandExecutor.ExecuteMutationAsync"/>), with no service operation of their own.
    /// </summary>
    public static readonly string[] PrimitiveCapabilities =
    [
        "host.exec",                 // execute_command (MCP/agent), scheduled custom command
        "host.prune",                // scheduled cleanup
        "db.backup_delete",          // DB-backup retention (`rm` of old dumps)
        "container.env_set",         // set_container_env (MCP/agent)
        "container.env_file_save",   // container page: .env save + compose up
        "container.exec",            // container page: Artisan migrate / rollback
    ];

    /// <summary>Who calls the primitive, and under which capability (the scanner sees the caller, the guard the scope).</summary>
    public static readonly (string Owner, string Handler, string Capabilities)[] PrimitiveCallers =
    [
        ("Whiskers.Mcp.Tools.ServerTools", "ExecuteCommand", "host.exec"),
        ("Whiskers.Mcp.Tools.ContainerTools", "SetContainerEnv", "container.env_set"),
        ("Whiskers.Mcp.Tools.ContainerTools", "DeployCompose", "compose.deploy"),
        ("Whiskers.Services.Scheduler.TaskExecutor", "ExecuteCustomCommand", "host.exec"),
        ("Whiskers.Services.Scheduler.TaskExecutor", "ExecuteCleanup", "host.prune"),
        ("Whiskers.Services.Scheduler.TaskExecutor", "ApplyRetention", "db.backup_delete"),
        ("Whiskers.Components.Pages.ContainerDetail", "SaveEnvAndRestart", "container.env_file_save"),
        ("Whiskers.Components.Pages.ContainerDetail", "ExecInContainerViaHost", "container.exec, db.query"),
        ("Whiskers.Services.Mutations.ComposeImpact", "DeployAsync", "app.deploy, compose.deploy (UI deployments)"),
    ];

    /// <summary>
    /// Pages and MCP tools that use the executor's plain <see cref="IHostCommandExecutor.ExecuteAsync"/>: reads only.
    /// A page or tool that changes something with a host command uses the primitive instead.
    /// </summary>
    public static readonly (string Owner, string Handler, string Why)[] PageAndToolReads =
    [
        ("Whiskers.Components.Pages.ContainerDetail", "LoadEnvVars", "finds the compose directory and reads its .env"),
        ("Whiskers.Components.Pages.ContainerDetail", "ExecInContainerViaHost", "artisan migrate:status (capability null)"),
        ("Whiskers.Mcp.Tools.MonitoringTools", "GetServerLogs", "journalctl / log tail"),
    ];

    /// <summary>The writes by surface: a row per path a person, a key, the agent or Whiskers itself starts.</summary>
    public static readonly (string Surface, string Capability, string Boundary)[] Inventory =
    [
        // UI pages (U-rows) — IUiMutationRunner
        ("ui", "container.start", "IDockerService.StartContainerAsync"),
        ("ui", "workload.start", "IWorkloadProvider.StartAsync"),
        ("ui", "workload.stop", "IWorkloadProvider.StopAsync"),
        ("ui", "workload.restart", "IWorkloadProvider.RestartAsync"),
        ("ui", "container.env_file_save", "primitive"),
        ("ui", "container.exec", "primitive"),
        ("ui", "server.save", "IServerConfigService.AddServerAsync"),
        ("ui", "server.save", "IServerConfigService.UpdateServerAsync"),
        ("ui", "server.save", "IServerConfigService.SaveSshKeyAsync"),
        ("ui", "server.remove", "IServerConfigService.TakeServerAsync"),
        ("ui", "server.onboard", "IOnboardingService.OnboardServerAsync"),
        ("ui", "selfbackup.create", "IBackupService.CreateBackupAsync"),
        ("ui", "selfbackup.delete", "IBackupService.DeleteBackupAsync"),
        ("ui", "selfbackup.restore", "IBackupService.BeginRestoreAsync"),
        ("ui", "aitrigger.save", "IAiTriggerStore.SaveAsync"),
        ("ui", "webhook.save", "IWebhookService.CreateWebhookAsync"),
        ("ui", "webhook.save", "IWebhookService.SetOwnerAsync"),
        ("ui", "webhook.rotate_secret", "IWebhookService.RegenerateSecretAsync"),
        ("ui", "webhook.set_enabled", "IWebhookService.SetEnabledAsync"),
        ("ui", "webhook.delete", "IWebhookService.DeleteWebhookAsync"),
        ("ui", "cloud.power_on", "ICloudControlService.PowerOnAsync"),
        // MCP tools and the agent (M-rows) — McpGuardrailFilter / AgentToolInvoker
        ("mcp", "host.exec", "primitive"),
        ("mcp", "container.env_set", "primitive"),
        ("mcp", "app.deploy", "IDeploymentService.DeployFromFormAsync"),
        ("mcp", "autopatch.preview", "IAutoPatchService.StartPreview"),
        ("mcp", "image.assess_update", "IUpdateRiskService.AssessAsync"),
        ("mcp", "checks.pause", "ILoopSuspensionService.Suspend"),
        ("mcp", "checks.resume", "ILoopSuspensionService.Resume"),
        ("mcp", "hetzner.rescue_enable", "IHetznerExtensions.EnableRescueAsync"),
        ("mcp", "hetzner.snapshot_delete", "IHetznerExtensions.DeleteImageAsync"),
        // Provider internals executing a decided cloud operation
        ("mcp", "cloud.reboot", "ICloudProvider.RebootAsync"),
        ("mcp", "cloud.reboot", "IHetznerService.RebootAsync"),
        // Background services and the scheduler (S-rows) — IAutomationMutationRunner
        ("background", "host.exec", "primitive"),
        ("background", "host.prune", "primitive"),
        ("background", "selfbackup.create", "IBackupService.CreateBackupAsync"),
        ("background", "selfbackup.prune", "IBackupService.PruneAsync"),
        ("background", "db.backup_delete", "primitive"),
        ("background", "volume.backup_delete", "IVolumeBackupService.DeleteBackupAsync"),
        ("background", "autopatch.run", "IAutoPatchService.RunCycleAsync"),
        ("background", "container.update", "IDockerService.RecreateContainerAsync"),
        ("background", "vpn.bootstrap", "IVpnProvider.EnsureUpAsync"),
        // Webhooks (S-04/S-05) — automation runner on the owner's authority
        ("webhook", "container.restart", "IDockerService.RestartContainerAsync"),
        ("webhook", "gitdeploy.deploy", "IGitDeployService.DeployAsync"),
        // Terminals (T-rows)
        ("terminal", "terminal.container_open", "ITerminalSessionManager"),
        ("terminal", "terminal.host_open", "ITerminalSessionManager"),
    ];

    [Fact]
    public void Every_capability_has_an_execution_boundary_the_guard_checks()
    {
        foreach (var capability in Catalog.All.Select(c => c.Id))
            Assert.True(Marked.Contains(capability) || PrimitiveCapabilities.Contains(capability),
                $"'{capability}' has neither a [MutatingOperation] method nor a primitive execution: nothing would " +
                "stop it running outside a decision.");

        // …and nothing is marked with a capability the gateway cannot decide.
        foreach (var marked in Marked)
            Assert.True(Catalog.Find(marked.Key) is not null, $"'{marked.Key}' is marked but not in the catalog.");
        // A primitive capability really has no operation of its own (else the operation is the boundary).
        Assert.All(PrimitiveCapabilities, c => Assert.False(Marked.Contains(c), $"'{c}' has a marked operation."));
    }

    [Fact]
    public void The_inventory_names_real_boundaries_for_every_surface()
    {
        Assert.Equal(["background", "mcp", "terminal", "ui", "webhook"], Inventory.Select(r => r.Surface).Distinct().Order());
        foreach (var (surface, capability, boundary) in Inventory)
        {
            Assert.True(Catalog.Find(capability) is not null, $"{surface}: unknown capability '{capability}'.");
            if (boundary == "primitive")
            {
                Assert.Contains(capability, PrimitiveCapabilities);
                continue;
            }
            var parts = boundary.Split('.');
            var methods = Marked[capability].Where(m => m.DeclaringType!.Name == parts[0]
                                                        && (parts.Length == 1 || m.Name == parts[1])).ToList();
            Assert.True(methods.Count > 0, $"{surface}: {boundary} is not marked '{capability}'.");
        }

        // The rows the work package names explicitly.
        foreach (var named in new[] { "host.exec", "server.save", "server.remove", "server.onboard", "selfbackup.create",
                     "selfbackup.delete", "selfbackup.restore", "selfbackup.prune", "db.backup_delete", "volume.backup_delete" })
            Assert.Contains(Inventory, r => r.Capability == named);
    }

    [Fact]
    public void Exactly_the_listed_places_run_a_raw_host_command_that_changes_something()
    {
        var actual = MutationCallScanner.PrimitiveCallers(App)
            .Select(c => $"{c.Owner.FullName}.{c.Handler}")
            .Order()
            .ToList();

        Assert.Equal(PrimitiveCallers.Select(c => $"{c.Owner}.{c.Handler}").Order(), actual);
    }

    [Fact]
    public void Pages_and_tools_use_the_plain_executor_for_reads_only()
    {
        var read = typeof(IHostCommandExecutor).GetMethod(nameof(IHostCommandExecutor.ExecuteAsync))!;
        var actual = MutationCallScanner.Callers(App, m => m == read)
            .Where(c => c.Owner.Namespace is { } ns
                        && (ns.StartsWith("Whiskers.Components", StringComparison.Ordinal)
                            || ns.StartsWith("Whiskers.Mcp", StringComparison.Ordinal)))
            .Select(c => $"{c.Owner.FullName}.{c.Handler}")
            .Order()
            .ToList();

        Assert.Equal(PageAndToolReads.Select(c => $"{c.Owner}.{c.Handler}").Order(), actual);
    }

    [Fact]
    public void Only_the_host_executor_reaches_the_docker_host_shell()
    {
        // The executor's mTLS backend: anyone else calling it would run a host command past both the primitive and
        // the plain read path the two tests above pin.
        var callers = MutationCallScanner
            .Callers(App, m => m.DeclaringType == typeof(Whiskers.Services.Docker.IDockerService)
                               && m.Name == nameof(Whiskers.Services.Docker.IDockerService.RunHostShellAsync))
            .Select(c => c.Owner.FullName!)
            .Distinct()
            .Order();

        Assert.Equal(["Whiskers.Services.Server.HostCommandExecutor"], callers);
    }

    [Fact]
    public void Every_changing_mcp_tool_is_decided_by_the_gateway()
    {
        var tools = App.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => (Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Tool is not null)
            .ToList();
        Assert.NotEmpty(tools);

        foreach (var (method, tool) in tools)
        {
            var name = tool!.Name ?? Whiskers.Services.Agent.AgentToolRegistry.ToSnakeCase(method.Name);
            var level = method.GetCustomAttribute<McpToolLevelAttribute>()?.Level ?? McpPermissionLevels.Admin;
            // A tool that is not a plain read either maps to a capability, or is one of the documented non-capability
            // admin tools (agent and key administration, which change no infrastructure).
            if (McpPermissionLevels.Normalize(level) == McpPermissionLevels.Read) continue;
            if (Catalog.FindByMcpTool(name) is not null) continue;
            Assert.Contains(name, NonInfrastructureTools);
        }
    }

    /// <summary>Write/Admin tools that change no infrastructure (they read, or start the agent, which itself goes
    /// through the gateway per call).</summary>
    private static readonly string[] NonInfrastructureTools =
    [
        "instruct_agent",
    ];

    [Fact]
    public void The_start_up_backup_paths_have_exactly_their_one_caller_each()
    {
        static IReadOnlyList<string> CallersOf(string method) => MutationCallScanner
            .Callers(App, m => m.DeclaringType == typeof(IBackupService) && m.Name == method)
            .Select(c => c.Owner.FullName!)
            .Distinct()
            .Order()
            .ToList();

        // S-09: the pre-upgrade backup and its pruning, before the gateway can decide or record.
        Assert.Equal(["Whiskers.Services.Persistence.UpgradeGuard"], CallersOf(nameof(IBackupService.CreatePreUpgradeBackupAsync)));
        Assert.Equal(["Whiskers.Services.Persistence.UpgradeGuard"], CallersOf(nameof(IBackupService.DeletePreUpgradeBackupAsync)));
        // S-10: the command-line restore, from the process start only.
        Assert.Equal(["Whiskers.Services.Backup.RestoreCli"], CallersOf(nameof(IBackupService.BeginHostRestoreAsync)));
        Assert.Equal(["Program"], MutationCallScanner
            .Callers(App, m => m.DeclaringType == typeof(RestoreCli) && m.Name == nameof(RestoreCli.RunAsync))
            .Select(c => c.Owner.FullName!).Distinct());
    }

    [Fact]
    public async Task A_workload_provider_from_the_factory_is_guarded()
    {
        var servers = new FakeServerConfig(new Whiskers.Models.ServerConfig { Id = "local", Name = "local" });
        var factory = new Whiskers.Services.Workloads.WorkloadProviderFactory(servers, new FakeDocker(),
            new Whiskers.Services.Workloads.Kubernetes.KubernetesClientCache(servers, null!),
            new MutationScopeMonitor(NullLogger<MutationScopeMonitor>.Instance));

        var provider = factory.GetForServer("local");

        Assert.IsAssignableFrom<DispatchProxy>(provider);
        await Assert.ThrowsAsync<MutationScopeViolationException>(() => provider.StartAsync("c1"));
    }

    // ---------------------------------------------------------------- compositions

    [Fact]
    public void A_composed_operation_never_needs_more_than_the_one_that_composes_it()
    {
        foreach (var (outer, inner) in MutationCompositions.Entries)
        {
            var o = Catalog.Find(outer);
            Assert.True(o is not null, $"Unknown composing capability '{outer}'.");
            foreach (var i in inner)
            {
                var c = Catalog.Find(i);
                Assert.True(c is not null, $"'{outer}' composes unknown '{i}'.");
                Assert.True(Rank(c!.RequiredLevel) <= Rank(o!.RequiredLevel),
                    $"'{outer}' ({o.RequiredLevel}) composes '{i}' ({c.RequiredLevel}): a nested step would do more than was decided.");
            }
        }

        static int Rank(string level) => McpPermissionLevels.Normalize(level) switch
        {
            McpPermissionLevels.Admin => 2,
            McpPermissionLevels.Write => 1,
            _ => 0,
        };
    }

    [Fact]
    public void Compositions_are_explicit_and_transitive_only_through_the_table()
    {
        Assert.True(MutationCompositions.Allows("compose.deploy", "container.create"));   // via app.deploy
        Assert.True(MutationCompositions.Allows("workload.start", "container.start"));
        Assert.False(MutationCompositions.Allows("workload.start", "container.stop"));
        Assert.False(MutationCompositions.Allows("container.start", "image.pull"));
        Assert.False(MutationCompositions.Allows("host.exec", "container.create"));
        Assert.Empty(MutationCompositions.Of("config.whitelist"));
    }
}
