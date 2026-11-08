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
        "endpointcheck.run",         // R1-57: the monitor (as the owner) and "Run now" (as the Admin)
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
        ("Whiskers.Services.Mutations.ComposeImpact", "SaveEnvAsync", "container.env_file_save"),
        // The write helper: migrate, rollback and SQL import run the primitive inside their capability decision.
        ("Whiskers.Components.Pages.ContainerDatabasePanel", "ExecInContainerViaHost", "container.exec, db.query"),
        ("Whiskers.Services.Mutations.ComposeImpact", "DeployAsync", "app.deploy, compose.deploy"),
        ("Whiskers.Services.Webhooks.WebhookService", "DeployCompose", "compose.deploy"),
        ("Whiskers.Services.EndpointChecks.EndpointCheckRunner", "ProbeAsync", "endpointcheck.run"),
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
        ("ui", "endpointcheck.save", "IEndpointCheckStore.SaveAsync"),
        ("ui", "endpointcheck.delete", "IEndpointCheckStore.DeleteAsync"),
        ("ui", "endpointcheck.run", "primitive"),
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
        ("background", "endpointcheck.run", "primitive"),
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
    public void The_primitive_runs_only_under_the_capabilities_its_callers_are_decided_as()
    {
        // R1-48 (review M1): at run time the primitive needs a scope of one of MutationCompositions.RawHostCommands, not
        // just any open scope. That set is exactly what the pinned callers run under — no more.
        var declared = PrimitiveCallers
            .SelectMany(c => c.Capabilities.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(declared, c => Assert.True(Catalog.Find(c) is not null, c));
        Assert.Equal(declared.Order(StringComparer.Ordinal), MutationCompositions.RawHostCommands.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_raw_host_command_is_refused_inside_a_scope_that_is_no_raw_host_capability()
    {
        var executor = MutationScopeProxy<IHostCommandExecutor>.Wrap(new RecordingExecutor(), new MutationScopeMonitor(
            NullLogger<MutationScopeMonitor>.Instance));

        // An open decision for a configuration write cannot run `rm -rf` through the primitive.
        using (MutationScope.Begin("webhook.save", "op-1"))
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => executor.ExecuteMutationAsync("local", "rm -rf /x"));
        using (MutationScope.Begin("host.exec", "op-2"))
            Assert.True((await executor.ExecuteMutationAsync("local", "true")).Success);
        // compose.deploy → app.deploy: the UI's per-container decisions inside a deployment still run it.
        using (MutationScope.Begin("compose.deploy", "op-3"))
        using (MutationScope.Begin("container.update", "op-3a"))
            Assert.True((await executor.ExecuteMutationAsync("local", "true")).Success);
        // The plain read path is not guarded (its callers are pinned instead).
        Assert.True((await executor.ExecuteAsync("local", "uptime")).Success);
    }

    private sealed class RecordingExecutor : IHostCommandExecutor
    {
        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null) =>
            Task.FromResult(new CommandResult { Output = "", Error = "", ExitCode = 0 });
    }
    [Fact]
    public void The_plain_executor_is_called_only_for_reads_or_inside_a_marked_operation()
    {
        // R1-48 (review M1): in every namespace. A caller is either a read (PlainExecutorReads) or sits in the body of a
        // marked operation of its own type, which the guard and the scanner already hold to its decision
        // (PlainExecutorInsideMarkedOperations, checked against the type's own [MutatingOperation] implementations).
        var read = typeof(IHostCommandExecutor).GetMethod(nameof(IHostCommandExecutor.ExecuteAsync))!;
        var actual = MutationCallScanner.Callers(App, m => m == read)
            .Select(c => $"{c.Owner.FullName}.{c.Handler}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        var expected = PlainExecutorReads.Select(c => $"{c.Owner}.{c.Handler}")
            .Concat(PlainExecutorInsideMarkedOperations.Select(c => $"{c.Owner}.{c.Handler}"))
            .Concat(PlainExecutorInPrivateHelpers.Select(c => $"{c.Owner}.{c.Handler}"))
            .Concat(ScannerHostSideEffects.Select(c => $"{c.Owner}.{c.Handler}"))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(expected.SequenceEqual(actual), "Plain-executor callers not listed:\n  " +
            string.Join("\n  ", actual.Except(expected)) + "\nListed but no longer calling:\n  " + string.Join("\n  ", expected.Except(actual)));

        foreach (var (owner, handler, capability) in PlainExecutorInsideMarkedOperations)
        {
            var type = App.GetType(owner)!;
            var implemented = type.GetInterfaces()
                .SelectMany(i => MutatingMethods.Of(i).Select(kv => (Map: type.GetInterfaceMap(i), kv.Key, kv.Value)))
                .Where(x => x.Map.TargetMethods[Array.IndexOf(x.Map.InterfaceMethods, x.Key)].Name == handler)
                .Select(x => x.Value);
            Assert.Contains(capability, implemented);
        }
        foreach (var (owner, handler, _) in PlainExecutorInPrivateHelpers)
        {
            var type = App.GetType(owner)!;
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
                                          | BindingFlags.DeclaredOnly).Where(m => m.Name == handler).ToList();
            Assert.NotEmpty(methods);
            Assert.All(methods, m => Assert.True(m.IsPrivate, $"{owner}.{handler} must stay private."));
            Assert.Contains(type.GetInterfaces(), i => MutatingMethods.Of(i).Count > 0);
        }
    }

    [Fact]
    public void Only_the_cve_monitor_runs_the_scanners()
    {
        // M2: the scanners' host side effects run unattended only as part of the CVE monitor's cycle.
        static IReadOnlyList<string> CallersOf(Func<MethodInfo, bool> match) => MutationCallScanner
            .Callers(App, match).Select(c => c.Owner.FullName!).Distinct().Order(StringComparer.Ordinal).ToList();

        var os = CallersOf(m => m.Name == "ScanAsync" && m.DeclaringType?.Name is "IOsCveScanner" or "OsCveScanner");
        var trivy = CallersOf(m => m.Name == "ScanContainerImageAsync" && m.DeclaringType?.Name is "ITrivyScanner" or "TrivyScanner");
        Assert.Equal(ScannerCallers, os.Concat(trivy).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>Who runs the CVE scanners (M2 exemption).</summary>
    private static readonly string[] ScannerCallers =
    [
        "Whiskers.Services.Cve.CveMonitorService",       // the unattended scan cycle (S-06)
        "Whiskers.Services.ImageUpdate.UpdateRiskService", // inside an image.assess_update decision (M-47)
    ];

    /// <summary>Plain-executor calls that read (the command changes nothing on the host).</summary>
    public static readonly (string Owner, string Handler, string Why)[] PlainExecutorReads =
    [
        ("Whiskers.Components.Pages.ContainerDatabasePanel", "ReadMigrationStatusAsync", "artisan migrate:status, a fixed command; the writes go through ExecInContainerViaHost and the primitive"),
        ("Whiskers.Components.Pages.ContainerDetail", "LoadEnvVars", "finds the compose directory and reads its .env"),
        ("Whiskers.Mcp.Tools.MonitoringTools", "GetServerLogs", "journalctl / log tail"),
        ("Whiskers.Services.Backup.VolumeBackupService", "ListVolumesAsync", "docker volume ls"),
        ("Whiskers.Services.Cve.OsCveScanner", "GetCveIdsFromChangelogAsync", "apt changelog of an installed package"),
        ("Whiskers.Services.LogMonitor.Hygiene.LogInventory", "FreeDiskBytesAsync", "df"),
        ("Whiskers.Services.LogMonitor.Hygiene.LogInventory", "ReadSizeAsync", "du / stat of log files"),
        ("Whiskers.Services.Metrics.MetricsCollectorService", "GetDiskUsageAsync", "df"),
        ("Whiskers.Services.Mutations.ComposePlanner", "ConfigAsync", "docker compose config in a temporary directory (read-only planning, R1-16; contract 0006 reads its JSON)"),
        ("Whiskers.Services.Mutations.ComposePlanner", "DiscoverAsync", "finds compose files"),
        ("Whiskers.Services.Mutations.ComposePlanner", "InspectContainerAsync", "docker inspect of a container a namespace is shared with (contract 0006)"),
        ("Whiskers.Services.Mutations.ComposePlanner", "InspectImagesAsync", "docker image inspect of the planned images (contract 0006)"),
        ("Whiskers.Services.Mutations.ComposePlanner", "MountsOfContainerAsync", "docker inspect"),
        ("Whiskers.Services.Mutations.ComposePlanner", "ReadAsync", "reads compose files and .env"),
        ("Whiskers.Services.Mutations.ComposePlanner", "WorkingDirectoryAsync", "docker inspect labels"),
        ("Whiskers.Services.Mutations.ComposeRunner", "CgroupnsDefaultAsync", "docker info / daemon.json: the daemon's default cgroup namespace mode (contract 0006, review M1)"),
        ("Whiskers.Services.Mutations.ComposeRunner", "InspectImagesAsync", "step 4: docker image inspect of the images the run starts (contract 0006)"),
        ("Whiskers.Services.Mutations.ComposeRunner", "PinAsync", "docker image inspect of the pins (contract 0013, D11); the pin file is written through the run's mutate delegate"),
        ("Whiskers.Services.Mutations.ComposeRunner", "RecheckAsync", "docker inspect of the containers the run created (contract 0006); the stop goes through ComposeExposureStopper"),
        ("Whiskers.Services.Mutations.ComposeRunner", "RestoreTagsAsync", "docker image inspect before the tags are put back; the docker tag itself runs through the run's mutate delegate"),
        ("Whiskers.Services.Mutations.VerifierSupport", "HostReadAsync", "the verifiers' read-back (R1-37)"),
        ("Whiskers.Services.Scheduler.TaskExecutor", "ApplyRetention", "ls -1Aq of the DB backup directory, inside db.backup_delete (INV-19 review); the rm of the chosen dumps runs on the primitive"),
        ("Whiskers.Services.Server.FirewallService", "GetStatusAsync", "ufw status"),
        ("Whiskers.Services.Server.NginxService", "GetSiteConfigAsync", "cat of a site file"),
        ("Whiskers.Services.Server.NginxService", "ListSitesAsync", "ls of sites-available / -enabled"),
        ("Whiskers.Services.Server.NginxService", "TestConfigAsync", "nginx -t"),
        ("Whiskers.Services.Server.SslCertService", "ListCertificatesAsync", "certbot certificates"),
        ("Whiskers.Services.Server.SystemdService", "GetJournalAsync", "journalctl"),
        ("Whiskers.Services.Server.SystemdService", "GetStatusAsync", "systemctl status"),
        ("Whiskers.Services.Server.SystemdService", "ListServicesAsync", "systemctl list-units"),
        // The primitive's own default body hands the command to the transport: it is the guarded call itself.
        ("Whiskers.Services.Server.IHostCommandExecutor", "ExecuteMutationAsync", "the primitive's body (guarded by [MutationPrimitive])"),
    ];

    /// <summary>Plain-executor calls inside the implementation of a marked operation of the same type: the operation's
    /// own decision covers them (the type is guarded and the scanner checks its calls).</summary>
    public static readonly (string Owner, string Handler, string Capability)[] PlainExecutorInsideMarkedOperations =
    [
        ("Whiskers.Services.AutoPatch.ScriptAutoPatchRunner", "PatchAsync", "container.update"),
        ("Whiskers.Services.Backup.VolumeBackupService", "BackupVolumeAsync", "volume.backup"),
        ("Whiskers.Services.Backup.VolumeBackupService", "DeleteBackupAsync", "volume.backup_delete"),
        ("Whiskers.Services.Backup.VolumeBackupService", "RestoreVolumeAsync", "volume.restore"),
        ("Whiskers.Services.Database.DatabaseService", "BackupDatabaseAsync", "db.backup"),
        ("Whiskers.Services.GitDeploy.GitDeployService", "DeleteAppAsync", "gitdeploy.delete"),
        ("Whiskers.Services.GitDeploy.GitDeployService", "DeployAsync", "gitdeploy.deploy"),   // contract 0006: the compose run's pull, build and up
        ("Whiskers.Services.Server.FirewallService", "AddRuleAsync", "firewall.rule_add"),
        ("Whiskers.Services.Server.FirewallService", "RemoveRuleAsync", "firewall.rule_remove"),
        ("Whiskers.Services.Server.FirewallService", "SetStatusAsync", "firewall.set_status"),
        ("Whiskers.Services.Server.NginxService", "DisableSiteAsync", "nginx.site_disable"),
        ("Whiskers.Services.Server.NginxService", "EnableSiteAsync", "nginx.site_enable"),
        ("Whiskers.Services.Server.NginxService", "ReloadAsync", "nginx.reload"),
        ("Whiskers.Services.Server.NginxService", "UpdateSiteConfigAsync", "nginx.update"),
        ("Whiskers.Services.Server.SslCertService", "RenewAllAsync", "ssl.renew"),
        ("Whiskers.Services.Server.SslCertService", "RenewAsync", "ssl.renew"),
        ("Whiskers.Services.Server.SystemdService", "DisableAsync", "systemd.manage"),
        ("Whiskers.Services.Server.SystemdService", "EnableAsync", "systemd.manage"),
        ("Whiskers.Services.Server.SystemdService", "RestartAsync", "systemd.manage"),
        ("Whiskers.Services.Server.SystemdService", "StartAsync", "systemd.manage"),
        ("Whiskers.Services.Server.SystemdService", "StopAsync", "systemd.manage"),
    ];

    /// <summary>Plain-executor calls in a private helper of a guarded service (nothing outside the type can call it); the
    /// helper serves the type's marked operations and its reads.</summary>
    public static readonly (string Owner, string Handler, string Why)[] PlainExecutorInPrivateHelpers =
    [
        ("Whiskers.Services.Certificates.MtlsCertificateService", "ConfirmAsync", "certificate renewal: confirms the installed certificate"),
        ("Whiskers.Services.Certificates.MtlsCertificateService", "RunAsync", "certificate renewal / issue: installs the certificate"),
        ("Whiskers.Services.Certificates.MtlsClientCredentialStore", "SignAsync", "R1-44: step-ca on local signs a per-host client CSR (certificate.issue_mtls)"),
        ("Whiskers.Services.Database.DatabaseService", "ExecInContainer", "db.query / db.backup and the database listings"),
        ("Whiskers.Services.GitDeploy.GitDeployService", "Sh", "gitdeploy.deploy / gitdeploy.delete: checkout, build, up"),
        ("Whiskers.Services.Onboarding.OnboardingService", "ResolveVmEndpoint", "server.onboard: reads the host's address"),
        ("Whiskers.Services.Onboarding.OnboardingService", "Sh", "server.onboard: installs the socket proxy and the certificate"),
        ("Whiskers.Services.Onboarding.OnboardingService", "ShTry", "server.onboard: best-effort steps"),
    ];

    /// <summary>
    /// M2 (R1-48 review), a designed exemption: the CVE scanners' background runs change the host's caches, not its
    /// workloads — `apt-get update` refreshes the package index (OsCveScanner) and Trivy pulls its scanner image, runs it
    /// as a throw-away container (`--rm`) and keeps its vulnerability database in a cache volume (TrivyScanner). They run
    /// unattended as part of monitoring (PRODUCT_CONTRACT S-06), touch no container, file or setting a person manages, and
    /// are pinned here to exactly these methods; the scanners are called only by the CVE monitor.
    /// </summary>
    public static readonly (string Owner, string Handler, string What)[] ScannerHostSideEffects =
    [
        ("Whiskers.Services.Cve.OsCveScanner", "ScanAsync", "apt-get update (package index), then reads"),
        ("Whiskers.Services.Cve.TrivyScanner", "ScanContainerImageAsync", "docker run --rm trivy with a cache volume (pulls the scanner image)"),
    ];
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
    public void Only_the_host_shell_drives_its_docker_steps()
    {
        // R1-57: the helper container's steps sit behind IHostShellDocker. Creating that privileged nsenter helper from
        // anywhere else would be a host-command path past RunHostShellAsync and its pinned caller above.
        var hostShell = typeof(Whiskers.Services.Docker.Operations.HostShellOperations).FullName!;

        var callers = MutationCallScanner
            .Callers(App, m => m.DeclaringType == typeof(Whiskers.Services.Docker.Operations.IHostShellDocker))
            .Select(c => c.Owner.FullName!)
            .Distinct()
            .Order();
        Assert.Equal([hostShell], callers);

        var constructors = MutationCallScanner
            .Constructors(App, typeof(Whiskers.Services.Docker.Operations.DockerClientHostShell))
            .Select(t => t.FullName!)
            .Distinct()
            .Order();
        Assert.Equal([hostShell], constructors);

        // R1-44: the one-off helper over a client built from a not-yet-active credential has exactly one caller, the
        // allow-list confirmation that must travel through the NEW client credential (contract 0007, MI-8).
        var oneOff = MutationCallScanner
            .Callers(App, m => m.DeclaringType == typeof(Whiskers.Services.Docker.Operations.HostShellOperations)
                               && m.Name == nameof(Whiskers.Services.Docker.Operations.HostShellOperations.ForOneOffClient))
            .Select(c => c.Owner.FullName!)
            .Distinct()
            .Order();
        Assert.Equal([typeof(Whiskers.Services.Certificates.MtlsClientProbe).FullName!], oneOff);

        // The interface pinned above has exactly one implementation in the app: another one would be a second way to
        // drive Docker that the caller pin does not see.
        var implementations = App.GetTypes()
            .Where(t => !t.IsInterface && typeof(Whiskers.Services.Docker.Operations.IHostShellDocker).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .Order();
        Assert.Equal([typeof(Whiskers.Services.Docker.Operations.DockerClientHostShell).FullName!], implementations);
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

    /// <summary>Method names that say "this changes something".</summary>
    private static readonly System.Text.RegularExpressions.Regex WriteName = new(
        "^(Save|Set|Add|Remove|Delete|Create|Update|Start|Stop|Restart|Deploy|Rotate|Revoke|Enable|Disable|Apply|Run|" +
        "Execute|Install|Purge|Prune|Write|Put|Import|Restore|Take|Adopt|Register|Pause|Resume|Suspend|Kill|Reset|" +
        "Clear|Rename|Upload|Move|Grant|Approve|Reject|Issue|Renew|Recreate|Rollback|Pull|Push|Connect|Disconnect|" +
        "Patch|Change|Begin|Mark|Record|Store|Seed|Onboard|Enroll|Toggle|Assign|Unassign|Attach|Detach|Mount|Open|Close|" +
        "Redeem|Recover|Consume|Lock|Unlock|Acquire|TryAcquire)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    [Fact]
    public void Every_write_named_interface_method_is_marked_or_explained()
    {
        // B1 (R1-48 review): an unmarked write is invisible to both the guard and the scanner. Every interface method of
        // Whiskers whose name says it writes is marked — or listed in WriteNamedButNotGated with the reason.
        var unexplained = App.GetTypes()
            .Where(t => t.IsInterface && t.Namespace?.StartsWith("Whiskers", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetMethods().Select(m => (Type: t, Method: m)))
            .Where(x => !x.Method.IsSpecialName && WriteName.IsMatch(x.Method.Name))
            .Where(x => !x.Method.IsDefined(typeof(MutatingOperationAttribute), false)
                        && !x.Method.IsDefined(typeof(MutationPrimitiveAttribute), false))
            .Select(x => $"{x.Type.Name}.{x.Method.Name}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        var listed = WriteNamedButNotGated.Select(w => w.Method).ToList();
        var added = unexplained.Except(listed).ToList();
        var stale = listed.Except(unexplained).ToList();
        Assert.True(added.Count == 0,
            "Write-named interface methods without [MutatingOperation] and without a reason:\n  " + string.Join("\n  ", added));
        Assert.True(stale.Count == 0,
            "No longer unmarked (or gone) — remove from WriteNamedButNotGated:\n  " + string.Join("\n  ", stale));
        Assert.Equal(listed.Count, listed.Distinct().Count());
    }

    /// <summary>Write-named interface methods that are deliberately not gateway operations, each with its reason.</summary>
    private static readonly (string Method, string Why)[] WriteNamedButNotGated =
    [
        // The gateway and its records.
        ("IActionOutcomeService.RecordAsync", "the Mutation Gateway itself, or its own records (decision, approval, audit, journal)"),
        ("IApprovalGate.OpenOperationOf", "a lookup of the open approval of an operation (no write)"),
        ("IApprovalGate.OpenApprovalOf", "a lookup of the open approval of an operation, for the event of a cancelled call (no write)"),
        ("IApprovalPersistence.ConsumeAsync", "the gateway uses up an approval (R1-14), its own record"),
        ("IAuditRecovery.RecoverAsync", "settles and re-writes the gateway's own audit events (R1-19)"),
        ("IApprovalStore.CreateAsync", "the Mutation Gateway itself, or its own records (decision, approval, audit, journal)"),
        ("IAuditChain.PruneAsync", "audit retention (S-08), the audit trail's own housekeeping"),
        ("IAuditChain.UpdateAsync", "the Mutation Gateway itself, or its own records (decision, approval, audit, journal)"),
        ("IAuditChain.WriteAsync", "the Mutation Gateway itself, or its own records (decision, approval, audit, journal)"),
        ("IAutomationMutationRunner.RunAsync", "the gateway entry for unattended runs"),
        ("IMcpCallLogStore.RecordAsync", "the Mutation Gateway itself, or its own records (decision, approval, audit, journal)"),
        ("IMutationAuditSink.BeginAsync", "the Mutation Gateway itself, or its own records (decision, approval, audit, journal)"),
        ("IMutationAuditSink.RecordDeniedAsync", "the Mutation Gateway itself, or its own records (decision, approval, audit, journal)"),
        ("IMutationGateway.ExecuteAsync", "the gateway itself"),
        ("IPolicyDecisionStore.RecordAsync", "the Mutation Gateway itself, or its own records (decision, approval, audit, journal)"),
        ("IUiMutationRunner.RunAsync", "the gateway entry for the UI"),
        // Designed exemptions with pinned callers (start-up, before the gateway can decide; the first admin).
        ("IMtlsClientCredentialStore.WriteJournalAsync", "R1-44: the rollout journal, Whiskers' own record of the phase a host reached (written inside mtls.client_migrate / mtls.client_renew / server.onboard decisions)"),
        ("IAgentSettingsStore.AdoptApiKeyRefAsync", "start-up credential migration (R1-30), pinned by CredentialAdoptionCallersTests"),
        ("IAppSettingsStore.AdoptDnsTokenRefAsync", "start-up credential migration (R1-29), pinned by CredentialAdoptionCallersTests"),
        ("IAppSettingsStore.AdoptSettingRefAsync", "start-up credential migration (R1-30), pinned by CredentialAdoptionCallersTests"),
        ("IBackupService.BeginHostRestoreAsync", "S-10 command-line restore, pinned (The_start_up_backup_paths_have_exactly_their_one_caller_each)"),
        ("IBackupService.CreatePreUpgradeBackupAsync", "S-09 pre-upgrade backup, pinned"),
        ("IBackupService.DeletePreUpgradeBackupAsync", "S-09 upgrade pruning, pre-upgrade backups only, pinned"),
        ("IRoleService.SetFirstAdminAsync", "first-run wizard, refuses once an Admin exists, pinned to SetupStateService"),
        ("IServerConfigService.AdoptCloudKeyRefAsync", "start-up credential migration (R1-29), pinned by CredentialAdoptionCallersTests"),
        ("IVaultService.AdoptLegacyCredentialAsync", "start-up credential migration, pinned by CredentialAdoptionCallersTests"),
        ("IWhitelistService.AddFirstAdminAsync", "first-run wizard, refuses once an Admin exists, pinned to SetupStateService"),
        ("IIdentityBindingStore.RecordPendingAsync", "R1-27: a refused Google/OIDC sign-in records its binding request for a configured address — nobody exists yet the gateway could decide for; it grants nothing, pinned to FederatedAdmission"),
        ("IIdentityBindingStore.RecordSignInAsync", "R1-27: the day of an admitted sign-in on its binding (housekeeping of the admission), pinned to FederatedAdmission"),
        ("IIdentityBindingStore.PruneOrphansAsync", "R1-27: removes bindings of addresses that are no entries, before every decided config.roles / config.whitelist write that adds or removes one and the wizard's first-admin writes (the start-up prune goes through RefreshAsync); it only takes access away"),
        ("ILocalAccountService.RedeemRecoveryCodeAsync", "S-12 (R1-49): the holder of a one-time recovery code sets that account's own password on the anonymous /account/recover page — no person exists the gateway could decide for; audited, rate-limited, pinned to AccountRecover"),
        // Execution plumbing beneath a marked operation or the primitive.
        ("IDockerConnectionManager.ExecuteAsync", "the Docker API transport the marked IDockerService operations run on"),
        ("IDockerConnectionManager.ExecuteGuardedAsync", "the Docker API transport the marked IDockerService operations run on"),
        ("IDockerService.RunHostShellAsync", "the host executor's mTLS backend; only HostCommandExecutor calls it (pinned)"),
        ("IHostShellDocker.CreateAsync", "a step of the helper container behind IDockerService.RunHostShellAsync (R1-57 seam; pinned)"),
        ("IHostShellDocker.KillAsync", "a step of the helper container behind IDockerService.RunHostShellAsync (R1-57 seam; pinned)"),
        ("IHostShellDocker.PullImageAsync", "a step of the helper container behind IDockerService.RunHostShellAsync (R1-57 seam; pinned)"),
        ("IHostShellDocker.RemoveAsync", "a step of the helper container behind IDockerService.RunHostShellAsync (R1-57 seam; pinned)"),
        ("IHostShellDocker.StartAsync", "a step of the helper container behind IDockerService.RunHostShellAsync (R1-57 seam; pinned)"),
        ("IHostCommandExecutor.ExecuteAsync", "the plain read path; every caller is listed (The_plain_executor_is_called_only_for_reads_or_inside_a_marked_operation)"),
        ("IServerBudget.RunAsync", "the per-server concurrency lane work runs in"),
        ("IComposeRunner.RunAsync", "runs a compose plan inside its caller's decision: every host write goes through the mutate delegate the caller passes (the primitive, or Git deploy's own executor inside gitdeploy.deploy) — contract 0006"),
        ("ITaskExecutor.ExecuteAsync", "runs a scheduled task inside the scheduler's automation-runner decision; each step it takes is marked or the primitive"),
        ("IVpnProcessRunner.RunAsync", "the process runner behind IVpnProvider.EnsureUpAsync (vpn.bootstrap); protected VPN layer"),
        ("IVpnProcessRunner.StartDetached", "the process runner behind IVpnProvider.EnsureUpAsync (vpn.bootstrap); protected VPN layer"),
        ("IOutboundHttp.CreateClient", "builds an HTTP client; no state"),
        ("IOutboundHttp.CreatePrimaryHandler", "builds an HTTP handler; no state"),
        ("IOutboundTargetGuard.ConnectAsync", "opens an outbound connection after the target check; no state"),
        ("ISshTunnelManager.CloseTunnel", "connection housekeeping"),
        // Terminals: opening is decided (terminal.*_open); a session's keystrokes and its end belong to that decision,
        // and the kill switch ends running sessions (R1-17).
        ("ITerminalLauncher.Start", "starts the process of a terminal session the gateway decided to open"),
        ("ITerminalProcess.WriteAsync", "keystrokes into a decided terminal session"),
        ("ITerminalSessionManager.CloseAsync", "ends a session"),
        // The agent: a session is a conversation; each tool call it makes is decided by the gateway.
        ("IAgentService.ResumeSessionAsync", "resumes a conversation; every tool call goes through the gateway"),
        ("IAgentService.StartSessionAsync", "starts a conversation; every tool call goes through the gateway"),
        ("IClaudeCodeRuntime.RunAsync", "runs the CLI agent, whose tools are MCP calls through the gateway (A-04)"),
        ("IAiTriggerAuthority.BeginRunAsync", "bookkeeping of a trigger run; its mutations are decided per call"),
        // Fail-closed reactions: Whiskers switches off what lost its authority — they only stop things.
        ("IAiTriggerStore.SuspendAsync", "suspends a trigger whose owner lost authority (fail closed)"),
        ("IAutomationOwnerStore.SuspendAsync", "suspends an automation whose owner lost authority (fail closed)"),
        ("IScheduledTaskSuspender.SuspendAsync", "suspends a task whose owner lost authority (fail closed)"),
        // Whiskers' own runtime state.
        ("IAgentTranscriptStore.ClearAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IAgentTranscriptStore.SaveAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IAlertBreachStore.SaveAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IEndpointCheckStore.RecordAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IEndpointCheckStore.PruneAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IEndpointCheckStore.SuspendAsync", "R1-57: switches a check off because its owner lost the authority to run it (INV-04), as ScheduledTaskSuspender does — it never runs or widens anything"),
        ("IEndpointCheckSlots.TryAcquire", "R1-57: in-memory run slot of the endpoint checks (4 fleet-wide, 1 per server); it only limits runs, each of which is its own endpointcheck.run decision"),
        ("IEndpointCheckRunner.RunAsync", "R1-57: runs inside its own endpointcheck.run decision (IAutomationMutationRunner); the host command is the guarded primitive"),
        ("IEndpointCheckRunner.RunInDecisionAsync", "R1-57: runs only inside an endpointcheck.run decision the caller holds; outside one the scope guard refuses its host command"),
        // R1-58: the disk-health monitor's last readings and open alerts, kept across a restart.
        ("IDiskHealthStateStore.SaveAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IAlertSuppressionStore.PruneAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IAutoPatchNightlyStore.SaveAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IChatHistoryStore.ClearAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IChatHistoryStore.SaveAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ICveAgeStore.PruneStaleAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ICveAgeStore.RecordSeenAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ICveFindingsStore.Clear", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ICveFindingsStore.PruneRemovedServers", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ICveFindingsStore.PruneServer", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ICveFindingsStore.Remove", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ICveFindingsStore.SaveAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ICveFindingsStore.Set", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IFleetSnapshotCache.PruneRemovedServers", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IFleetSnapshotCache.Put", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IHealthStore.AddRecord", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IImageUpdateStore.Clear", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IImageUpdateStore.Remove", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IImageUpdateStore.SaveAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IImageUpdateStore.Set", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IInAppNotificationStore.Add", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ILogMonitorService.SuspendedContainers", "a read (the list of suspended containers)"),
        ("IMetricRollupService.PruneAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("INotificationOutbox.AddAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("INotificationOutbox.AddOnceAsync", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IRecoveryAttemptLimiter.TryAcquire", "in-memory attempt counter of the recovery page (R1-49)"),
        ("ISelfMetrics.RecordApiCall", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ISelfMetrics.RecordCycle", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ISelfMetrics.RecordSkip", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("ISelfMetrics.Restore", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IServerCircuitBreaker.RecordFailure", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IServerCircuitBreaker.RecordSuccess", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
        ("IVpnBootstrapState.Set", "Whiskers' own runtime state (cache, history, counter), not infrastructure and not configuration a person manages"),
    ];

    [Fact]
    public void No_constructor_takes_the_concrete_implementation_of_a_guarded_interface()
    {
        // m2 (R1-48 review): the scope guard wraps the interface registration. A class that injects the implementation
        // itself (HetznerCloudProvider instead of ICloudProvider / IHetznerExtensions) would hold the unguarded object.
        static bool Guarded(Type i) => MutatingMethods.Of(i).Count > 0 || MutatingMethods.PrimitivesOf(i).Count > 0;
        var offenders = App.GetTypes()
            .SelectMany(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters().Select(p => (Type: t, Parameter: p))))
            .Select(x => (x.Type, x.Parameter, Element: ElementOf(x.Parameter.ParameterType)))
            .Where(x => x.Element is { IsClass: true } e && e.GetInterfaces().Any(Guarded))
            .Where(x => !x.Type.IsGenericType || x.Type.GetGenericTypeDefinition() != typeof(Whiskers.Startup.GuardedInstance<>))
            .Select(x => $"{x.Type.FullName}({x.Parameter.ParameterType.Name} {x.Parameter.Name})")
            .ToList();

        Assert.True(offenders.Count == 0, "Inject the guarded interface instead:\n  " + string.Join("\n  ", offenders));

        static Type ElementOf(Type t) =>
            t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? t.GetGenericArguments()[0]
            : t.IsArray ? t.GetElementType()! : t;
    }

    [Fact]
    public void Only_the_trigger_dispatcher_detaches_a_scope()
    {
        // MutationScope.Detach drops the whole open scope — its authority and the restrictions nested calls inherit
        // from it (pinned preset, trigger id). That is safe only for work that brings its own principal and pin: an AI
        // trigger run. Anything else that starts work outside a decision suppresses the flow (test below).
        var callers = MutationCallScanner.Callers(App, m => m.DeclaringType == typeof(MutationScope)
                                                             && m.Name == nameof(MutationScope.Detach))
            .Select(c => $"{c.Owner.FullName}.{c.Handler}").Distinct().Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["Whiskers.Services.Agent.Triggers.AiTriggerDispatcher.OnEventAsync"], callers);
    }

    [Fact]
    public void Work_started_in_the_background_from_a_decision_does_not_inherit_its_scope()
    {
        // m3 (R1-48 review): a scope closed under work it started is refused from then on — also a nested decision begun
        // there while the outer one was still open, which then stops halfway. So background work started from code that
        // can run inside a decision suppresses the flow (ExecutionContext.SuppressFlow) and decides for itself. Every
        // place that starts background work either suppresses the flow in the same handler or is listed with the reason
        // it never runs inside a decision.
        static bool StartsBackground(MethodInfo m) =>
            (m.DeclaringType == typeof(Task) && m.Name == nameof(Task.Run))
            || (m.DeclaringType == typeof(TaskFactory) && m.Name == nameof(TaskFactory.StartNew))
            || (m.DeclaringType == typeof(ThreadPool) && m.Name.StartsWith("QueueUserWorkItem", StringComparison.Ordinal))
            || (m.DeclaringType == typeof(ThreadPool) && m.Name.StartsWith("UnsafeQueueUserWorkItem", StringComparison.Ordinal));
        var starters = MutationCallScanner.Callers(App, StartsBackground)
            .Select(c => $"{c.Owner.FullName}.{c.Handler}").Distinct().ToHashSet(StringComparer.Ordinal);
        var suppressing = MutationCallScanner.Callers(App, m => m.DeclaringType == typeof(ExecutionContext)
                                                                   && m.Name == nameof(ExecutionContext.SuppressFlow))
            .Select(c => $"{c.Owner.FullName}.{c.Handler}").ToHashSet(StringComparer.Ordinal);

        var unexplained = starters.Where(s => !suppressing.Contains(s))
            .Order(StringComparer.Ordinal).ToList();
        var listed = BackgroundStartsOutsideDecisions.Select(b => b.Handler).ToList();
        Assert.True(unexplained.Except(listed).ToList() is var added && added.Count == 0,
            "Background work started without ExecutionContext.SuppressFlow and without a reason:\n  " + string.Join("\n  ", added));
        Assert.True(listed.Except(unexplained).ToList() is var stale && stale.Count == 0,
            "Suppresses the flow now, or gone — remove from BackgroundStartsOutsideDecisions:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>Places that start background work without suppressing the flow, because they never run inside a gateway
    /// decision (host start-up, hosted services, request handling before any decision, reads).</summary>
    private static readonly (string Handler, string Why)[] BackgroundStartsOutsideDecisions =
    [
        ("Whiskers.Components.Pages.Cves.TriggerScan", "a page click that opens the cve.scan decision off the circuit: the Task.Run is outside any decision, the scan runs inside the one it opens (INV-18)"),
        ("Whiskers.Components.Pages.Dashboard.OnInitializedAsync", "page initialisation loading stats (reads)"),
        ("Whiskers.Components.Pages.Settings.TriggerCveScan", "a page click that opens the cve.scan decision off the circuit: the Task.Run is outside any decision, the scan runs inside the one it opens (INV-18)"),
        ("Whiskers.Services.Auth.AuthAuditLog.StartAsync", "hosted-service start (host start-up)"),
        ("Whiskers.Services.Docker.SshTunnelManager.EstablishTunnelAsync", "the tunnel's pump forwards bytes and calls no service; a decision's scope it may carry authorizes nothing once closed"),
        ("Whiskers.Services.Notifications.OutboundTargetStartupCheck.StartAsync", "hosted-service start (host start-up)"),
        ("Whiskers.Services.Scheduler.SchedulerService.CheckAndRunDueTasks", "the scheduler loop (a hosted service); each task run opens its own decision"),
        ("Whiskers.Services.Webhooks.WebhookService.Bounded", "runs on the webhook worker, which StartAsync starts with the flow suppressed: never inside a decision"),
        ("Whiskers.Services.Webhooks.WebhookService.ExecuteRunAsync", "runs on the webhook worker, which StartAsync starts with the flow suppressed: never inside a decision"),
        ("Whiskers.Services.Webhooks.WebhookService.GiveUpAsync", "runs on the webhook worker (started from ExecuteRunAsync after its decision ended): never inside a decision"),
        ("Whiskers.Services.Webhooks.WebhookService.StartAsync", "runs on the webhook worker, which StartAsync starts with the flow suppressed: never inside a decision"),
    ];

    /// <summary>Every guarded operation whose <c>serverId</c> may be omitted (nullable or optional).</summary>
    private static IEnumerable<MethodInfo> GuardedOperationsWithOmittableServer()
    {
        var nullability = new NullabilityInfoContext();
        return typeof(IMutationScopeMonitor).Assembly.GetTypes()
            .Where(t => t.IsInterface)
            .SelectMany(t => MutatingMethods.Of(t).Keys.Where(m => m.DeclaringType == t))
            .Distinct()
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string) && p.Name == "serverId"
                && (p.HasDefaultValue || nullability.Create(p).WriteState == NullabilityState.Nullable)));
    }

    [Fact]
    public void Every_guarded_operation_with_an_omittable_server_declares_where_it_acts_then()
    {
        // R1-48 review round 3: an omitted serverId is resolved by the operation's own declaration, which its
        // implementation routes by too — the guard never assumes a default.
        var undeclared = GuardedOperationsWithOmittableServer()
            .Where(m => OmittedServer.Of(m) is null)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(undeclared.Count == 0, "Declare [OmittedServer] on: " + string.Join(", ", undeclared));
        Assert.NotEmpty(GuardedOperationsWithOmittableServer());
    }

    [Fact]
    public void The_fixed_omitted_server_declarations_are_the_ones_their_implementations_use()
    {
        // The host-side services fall back to Whiskers' own host; they read it from their interface's declaration
        // (OmittedServer.For<T>), so the guard and the implementation cannot drift apart.
        Assert.Equal(OmittedServer.Local, OmittedServer.For<IVolumeBackupService>(null));
        Assert.Equal(OmittedServer.Local, OmittedServer.For<Whiskers.Services.Database.IDatabaseService>(null));
        Assert.Equal("server-b", OmittedServer.For<IVolumeBackupService>("server-b"));
        Assert.Throws<InvalidOperationException>(() => OmittedServer.For<Whiskers.Services.Docker.IDockerService>(null));

        // The implementations take their fallback from the declaration (no literal of their own).
        var users = MutationScanner_Callers(m => m.DeclaringType == typeof(OmittedServer) && m.Name == nameof(OmittedServer.For));
        Assert.Equal([
                "Whiskers.Services.Backup.VolumeBackupService.BackupVolumeAsync",
                "Whiskers.Services.Backup.VolumeBackupService.ListVolumesAsync",
                "Whiskers.Services.Database.DatabaseService.BackupDatabaseAsync",
                "Whiskers.Services.Database.DatabaseService.ExecInContainer"],
            users);
    }

    private static IReadOnlyList<string> MutationScanner_Callers(Func<MethodInfo, bool> match) => MutationCallScanner
        .Callers(App, match).Select(c => $"{c.Owner.FullName}.{c.Handler}").Distinct().Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void The_account_recovery_paths_have_exactly_their_callers()
    {
        // R1-49 merged with R1-48: recovery runs outside the gateway on the authority of host access (S-11, the CLI) or
        // of a one-time code (S-12, the anonymous page). Both are audited first; here they are pinned to their callers.
        static IReadOnlyList<string> CallersOf(Func<MethodInfo, bool> match) => MutationCallScanner.Callers(App, match)
            .Select(c => $"{c.Owner.FullName}.{c.Handler}").Distinct().Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["Whiskers.Components.Pages.AccountRecover.Redeem"],
            CallersOf(m => m.Name == "RedeemRecoveryCodeAsync"
                           && m.DeclaringType?.Name is "ILocalAccountService" or "LocalAccountService"));
        // Issuing a code: the host CLI (S-11), and inside the decided account.create / account.password_reset.
        Assert.Equal(["Whiskers.Services.Auth.LocalAccountRecoveryCli.RunAsync",
                "Whiskers.Services.Auth.LocalAccountService.CreateAsync",
                "Whiskers.Services.Auth.LocalAccountService.ResetPasswordAsync"],
            CallersOf(m => m.Name == "IssueAsync" && m.DeclaringType?.Name == "RecoveryCodes"));
        Assert.Equal(["Program"], MutationCallScanner
            .Callers(App, m => m.Name == "RunAsync" && m.DeclaringType?.Name == "LocalAccountRecoveryCli")
            .Select(c => c.Owner.FullName!).Distinct());
    }
    [Fact]
    public void The_identity_binding_exemptions_have_exactly_their_callers()
    {
        // R1-27 (contract 0003): the automatic binding at sign-in is a designed exemption like the setup wizard (S-13),
        // the host confirmation one like the recovery CLI (S-11). Both are audited first; here they are pinned.
        static IReadOnlyList<string> CallersOf(string method) => MutationCallScanner
            .Callers(App, m => m.Name == method && m.DeclaringType?.Name is "IIdentityBindingStore" or "IdentityBindingStore")
            .Select(c => $"{c.Owner.FullName}.{c.Handler}").Distinct().Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["Whiskers.Services.Auth.FederatedAdmission.DecideAsync"], CallersOf("BindAtSignInAsync"));
        Assert.Equal(["Whiskers.Services.Auth.FederatedAdmission.DecideAsync"], CallersOf("RecordPendingAsync"));
        Assert.Equal(["Whiskers.Services.Auth.FederatedAdmission.DecideAsync"], CallersOf("RecordSignInAsync"));
        Assert.Equal(["Whiskers.Services.Auth.IdentityBindingCli.RunAsync"], CallersOf("ConfirmFromHostAsync"));
        Assert.Equal(["Program"], MutationCallScanner
            .Callers(App, m => m.Name == "RunAsync" && m.DeclaringType?.Name == "IdentityBindingCli")
            .Select(c => c.Owner.FullName!).Distinct());
        // The prune runs only inside the role and whitelist writes (the wizard's first-admin writes included); the start-up
        // prune, the admission's recovery and the CLI re-read and prune through RefreshAsync.
        Assert.Equal(["Whiskers.Services.Auth.RoleService.PruneRemovedAsync",
                "Whiskers.Services.Auth.RoleService.SetFirstAdminAsync",
                "Whiskers.Services.Auth.WhitelistService.AddFirstAdminAsync",
                "Whiskers.Services.Auth.WhitelistService.SaveWhitelistAsync"],
            CallersOf("PruneOrphansAsync"));
        Assert.Equal(["Whiskers.Services.Auth.FederatedAdmission.DecideAsync",
                "Whiskers.Services.Auth.IdentityBindingCli.RunAsync",
                "Whiskers.Services.Auth.IdentityBindingStartupPrune.InitializeAsync"],
            CallersOf("RefreshAsync"));
    }

    [Fact]
    public void The_first_admin_writes_have_exactly_the_setup_wizard_as_caller()
    {
        // R1-48 (option a): the wizard creates the first admin before anyone exists the gateway could authorize. The two
        // narrow methods refuse once an Admin exists; nothing but the wizard may call them.
        static IReadOnlyList<string> CallersOf(Type type, string method) => MutationCallScanner
            .Callers(App, m => m.DeclaringType == type && m.Name == method)
            .Select(c => $"{c.Owner.FullName}.{c.Handler}")
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(["Whiskers.Services.Setup.SetupStateService.CompleteSetupAsync"],
            CallersOf(typeof(Whiskers.Services.Auth.IRoleService), nameof(Whiskers.Services.Auth.IRoleService.SetFirstAdminAsync)));
        Assert.Equal(["Whiskers.Services.Setup.SetupStateService.CompleteSetupAsync"],
            CallersOf(typeof(Whiskers.Services.Auth.IWhitelistService), nameof(Whiskers.Services.Auth.IWhitelistService.AddFirstAdminAsync)));
        // Nor the implementations directly.
        Assert.Empty(CallersOf(typeof(Whiskers.Services.Auth.RoleService), nameof(Whiskers.Services.Auth.RoleService.SetFirstAdminAsync)));
        Assert.Empty(CallersOf(typeof(Whiskers.Services.Auth.WhitelistService), nameof(Whiskers.Services.Auth.WhitelistService.AddFirstAdminAsync)));
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
