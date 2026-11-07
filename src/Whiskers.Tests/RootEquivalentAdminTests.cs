using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Database;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;
using Whiskers.Services.Server;
using Whiskers.Utils;
using Xunit;

namespace Whiskers.Tests;

/// <summary>
/// INV-19: root-equivalent capabilities require Admin on every surface. This class pins the set
/// (<see cref="CapabilityCatalog.RootEquivalent"/>), that every member is Admin in the one table and on the MCP level
/// map, that the two scheduler capabilities resolve a command task to Admin per call, that no stored setting can lower
/// a member, that every execution sink of caller-chosen code belongs to a member, and that an approval below Admin
/// never covers one. <see cref="RootEquivalentAdminWireTests"/> drives the same set through every surface of the booted
/// application.
/// </summary>
public class RootEquivalentAdminTests
{
    private static readonly CapabilityCatalog Catalog = new();

    /// <summary>The membership, written out: a capability that loses its root-equivalent mark, or gains one, fails
    /// here and must be decided on in review.</summary>
    public static readonly string[] Pinned =
    [
        // Shells and commands on a host or in a container
        "host.exec", "container.exec", "terminal.host_open", "terminal.container_open", "endpointcheck.run",
        // Deployments and container definitions (any image, bind mount, command)
        "app.deploy", "compose.deploy", "gitdeploy.deploy", "gitdeploy.save", "gitdeploy.set_token", "webhook.save",
        "container.create", "container.env_set", "container.env_file_save",
        // Scheduled custom commands (create, change, run)
        "scheduler.create", "scheduler.update", "scheduler.run",
        // Arbitrary SQL as the database superuser
        "db.query",
        // Root-run configuration and trust on a host
        "nginx.update", "certificate.renew_mtls", "certificate.issue_mtls", "mtls.client_migrate", "mtls.client_renew",
        "server.onboard", "server.save", "hetzner.rescue_enable", "vpn.bootstrap", "selfbackup.restore", "volume.restore",
        // Who and what may do any of the above
        "aitrigger.save", "automation.owner_set", "config.save", "config.whitelist", "config.roles",
        "config.mcp_permissions", "config.agent", "mcp.key_create", "guardrails.save", "guardrails.kill_switch_off",
        "vault.secret_set", "registry.save", "account.create", "account.password_reset", "identity.binding_confirm",
    ];

    public static IEnumerable<object[]> Members() => CapabilityCatalog.RootEquivalent.Order(StringComparer.Ordinal)
        .Select(id => new object[] { id });

    [Fact]
    public void The_root_equivalent_set_is_the_pinned_one() =>
        Assert.Equal(Pinned.Order(StringComparer.Ordinal), CapabilityCatalog.RootEquivalent.Order(StringComparer.Ordinal));

    [Fact]
    public void The_set_is_read_from_the_risk_column_of_the_one_table()
    {
        Assert.Equal(Catalog.All.Where(c => c.Risk == CapabilityRisk.RootEquivalent).Select(c => c.Id).Order(StringComparer.Ordinal),
            CapabilityCatalog.RootEquivalent.Order(StringComparer.Ordinal));
        Assert.Subset(CapabilityCatalog.RootEquivalent.ToHashSet(), CapabilityCatalog.RootEquivalentPerCall.ToHashSet());
    }

    /// <summary>Every surface the invariant names is a member.</summary>
    [Theory]
    [InlineData("terminal.host_open")]        // host shell
    [InlineData("terminal.container_open")]   // container shell
    [InlineData("host.exec")]                 // execute_command
    [InlineData("compose.deploy")]            // compose deploy
    [InlineData("app.deploy")]                // app deploy
    [InlineData("gitdeploy.deploy")]          // Git deploy
    [InlineData("scheduler.create")]          // scheduled custom command: create
    [InlineData("scheduler.update")]          //   … change
    [InlineData("scheduler.run")]             //   … run
    [InlineData("db.query")]                  // arbitrary SQL
    public void Every_surface_the_invariant_names_is_root_equivalent(string id) =>
        Assert.Contains(id, CapabilityCatalog.RootEquivalent);

    [Theory]
    [MemberData(nameof(Members))]
    public void Every_member_needs_admin_in_the_table_on_the_mcp_level_map_and_on_a_page(string id)
    {
        var capability = Catalog.Find(id)!;
        if (CapabilityCatalog.RootEquivalentPerCall.Contains(id))
        {
            // The catalog level is the floor for the harmless task types; a command task is raised per call.
            Assert.Equal(McpPermissionLevels.Write, capability.RequiredLevel);
            return;
        }
        Assert.Equal(McpPermissionLevels.Admin, capability.RequiredLevel);
        Assert.Equal(AppRole.Admin, CapabilityCatalog.RoleOf(id));
        if (capability.McpTool is { } tool)
        {
            Assert.Equal(McpPermissionLevels.Admin, McpPermissionLevels.DefaultToolLevels[tool]);
            Assert.Equal(McpPermissionLevels.Admin, CapabilityCatalog.McpToolLevels[tool]);
        }
    }

    [Fact]
    public void Only_the_two_scheduler_capabilities_resolve_their_level_per_call() =>
        Assert.Equal(["scheduler.create", "scheduler.run"], CapabilityCatalog.RootEquivalentPerCall.Order(StringComparer.Ordinal));

    /// <summary>The per-call resolution, with the real resolver: a command task is Admin to create and to run, an
    /// unknown task type or task id is Admin (fail closed), and the run reads the stored task, not the caller's word.</summary>
    [Fact]
    public async Task A_command_task_is_admin_to_create_and_to_run()
    {
        var command = new ScheduledTaskEntity { TaskId = "t-cmd", TaskType = ScheduledTaskType.CustomCommand, Config = "{\"command\":\"id\"}" };
        var restart = new ScheduledTaskEntity { TaskId = "t-restart", TaskType = ScheduledTaskType.ContainerRestart };
        var resolver = new SchedulerRequirementResolver(Catalog, new SchedulerOnly(
            ApprovalSeparationOfDutyTests.SchedulerProxy.Create([command, restart])));
        Task<string> Resolve(string id, object args) => resolver.ResolveAsync(Catalog.Find(id)!, Args(args), CancellationToken.None);

        Assert.Equal(McpPermissionLevels.Admin, await Resolve("scheduler.create", new { taskType = "CustomCommand", command = "id" }));
        Assert.Equal(McpPermissionLevels.Admin, await Resolve("scheduler.create", new { taskType = 3 }));
        Assert.Equal(McpPermissionLevels.Admin, await Resolve("scheduler.create", new { taskType = "NoSuchType" }));
        Assert.Equal(McpPermissionLevels.Admin, await Resolve("scheduler.run", new { taskId = "t-cmd" }));
        Assert.Equal(McpPermissionLevels.Admin, await Resolve("scheduler.run", new { taskId = "no-such-task" }));
        // The caller cannot talk a command task down by naming another type: the stored task decides.
        Assert.Equal(McpPermissionLevels.Admin, await Resolve("scheduler.run", new { taskId = "t-cmd", taskType = "ContainerRestart" }));
        // …and a harmless task stays at its own level, so the exception is exactly the command.
        Assert.Equal(McpPermissionLevels.Write, await Resolve("scheduler.run", new { taskId = "t-restart" }));
        Assert.Equal(McpPermissionLevels.Admin, resolver.LevelFor(ScheduledTaskType.CustomCommand, retention: false));
    }

    /// <summary>Settings → MCP can only raise a level (INV-15): a stored <c>read</c> for a member's tool leaves it Admin.</summary>
    [Theory]
    [MemberData(nameof(Members))]
    public void A_stored_tool_level_cannot_lower_a_member(string id)
    {
        var capability = Catalog.Find(id)!;
        if (capability.McpTool is not { } tool) return;
        var keys = new AutomationMutationRunnerTests.Keys();
        keys.Data.Tools[tool] = new McpToolConfig { Enabled = true, RequiredLevel = McpPermissionLevels.Read };
        var resolved = new McpToolCapabilityOverrides(keys).Resolve(capability);
        Assert.Equal(capability.RequiredLevel, resolved.RequiredLevel);
    }

    // ── The sinks: whatever runs caller-chosen code belongs to a member ────────────────────────────────────────────

    /// <summary>The named interface operations that run code the caller chooses — a command, a shell, SQL, a compose
    /// file or image, a site configuration nginx loads as root. Each is marked with a member capability, so the gateway
    /// decides it at Admin before it runs. This is a reviewed list, not a discovery: a new such operation must be
    /// added here by hand (the raw-command primitive's callers, by contrast, are found by
    /// <c>MutationGatewayCoverageTests</c>). Operations that only hand a caller's name to a command (a database, a
    /// volume, a site, a service) check it with <c>CommandOperands</c> or an equivalent allow-list instead.</summary>
    public static readonly (Type Interface, string Method)[] CallerChosenCodeSinks =
    [
        (typeof(IDatabaseService), nameof(IDatabaseService.ExecuteQueryAsync)),
        (typeof(Whiskers.Services.Terminal.ITerminalSessionManager), nameof(Whiskers.Services.Terminal.ITerminalSessionManager.OpenHostShellAsync)),
        (typeof(Whiskers.Services.Terminal.ITerminalSessionManager), nameof(Whiskers.Services.Terminal.ITerminalSessionManager.OpenContainerShellAsync)),
        (typeof(Whiskers.Services.Deployment.IDeploymentService), nameof(Whiskers.Services.Deployment.IDeploymentService.DeployFromFormAsync)),
        (typeof(Whiskers.Services.Deployment.IDeploymentService), nameof(Whiskers.Services.Deployment.IDeploymentService.DeployFromComposeAsync)),
        (typeof(Whiskers.Services.GitDeploy.IGitDeployService), nameof(Whiskers.Services.GitDeploy.IGitDeployService.DeployAsync)),
        (typeof(INginxService), nameof(INginxService.UpdateSiteConfigAsync)),
    ];

    [Fact]
    public void Every_operation_that_runs_caller_chosen_code_is_marked_with_a_member()
    {
        foreach (var (type, name) in CallerChosenCodeSinks)
        {
            var methods = type.GetMethods().Where(m => m.Name == name).ToList();
            Assert.True(methods.Count > 0, $"{type.Name}.{name} does not exist");
            foreach (var method in methods)
            {
                var marked = method.GetCustomAttribute<MutatingOperationAttribute>();
                Assert.True(marked is not null, $"{type.Name}.{name} is not a marked operation");
                Assert.True(CapabilityCatalog.RootEquivalent.Contains(marked!.CapabilityId),
                    $"{type.Name}.{name} runs caller-chosen code as '{marked.CapabilityId}', which is not root-equivalent");
                Assert.Equal(McpPermissionLevels.Admin, Catalog.Find(marked.CapabilityId)!.RequiredLevel);
            }
        }
    }

    /// <summary>The raw host-command primitive (<see cref="IHostCommandExecutor.ExecuteMutationAsync"/>) runs only
    /// under <see cref="MutationCompositions.RawHostCommands"/>, and <c>MutationGatewayCoverageTests</c> pins every
    /// caller. Each of those capabilities is a member, except the two whose command the code builds itself with no
    /// caller text in it — and those are named here, so a third cannot join silently.</summary>
    [Fact]
    public void Every_capability_that_may_run_a_raw_host_command_is_a_member_or_a_fixed_command()
    {
        string[] fixedCommands =
        [
            "host.prune",         // TaskExecutor.CleanupCommandFor: docker rm of listed ids (quoted), image / builder prune
            "db.backup_delete",   // TaskExecutor.ApplyRetention: ls of the backup dir, rm of exactly that database's quoted dump names (DbDumpFiles)
        ];
        var outside = MutationCompositions.RawHostCommands.Where(c => !CapabilityCatalog.RootEquivalent.Contains(c))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(fixedCommands.Order(StringComparer.Ordinal), outside);
        Assert.All(MutationCompositions.RawHostCommands.Where(CapabilityCatalog.RootEquivalent.Contains),
            c => Assert.Equal(McpPermissionLevels.Admin, Catalog.Find(c)!.RequiredLevel));
        // The pinned primitive callers run under exactly these capabilities.
        var underCallers = MutationGatewayCoverageTests.PrimitiveCallers
            .SelectMany(c => c.Capabilities.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Subset(MutationCompositions.RawHostCommands.ToHashSet(StringComparer.Ordinal), underCallers);
    }

    [Fact]
    public void The_fixed_cleanup_command_carries_only_quoted_container_ids()
    {
        var command = TaskExecutor.CleanupCommandFor(["abc123", "x; rm -rf /"]);
        Assert.StartsWith("docker rm 'abc123' 'x; rm -rf /' 2>&1 && docker image prune -f", command);
    }

    /// <summary>A read listing of the database panel and the MCP tools <c>list_tables</c> / <c>get_schema</c> (Read)
    /// hands the database name to the client as an operand. Found while closing INV-19: a name like
    /// <c>--eval=…</c> was read by mongosh as an option and run — arbitrary code in the container at Read level. The
    /// name is now checked before the command is built; nothing reaches the host.</summary>
    [Theory]
    [InlineData("--eval=require('child_process').execSync('id')")]
    [InlineData("-c")]
    [InlineData("host=attacker.example dbname=app")]
    [InlineData("postgresql://attacker.example/app")]
    [InlineData("mongodb://attacker.example/app")]
    [InlineData("app db")]
    [InlineData("")]
    public async Task A_database_listing_refuses_a_name_the_client_could_read_as_an_option(string database)
    {
        var host = new RecordingHost();
        var service = new DatabaseService(host, NullLogger<DatabaseService>.Instance);
        var creds = new DatabaseCredentials { User = "app" };

        foreach (var type in new[] { DatabaseType.MongoDB, DatabaseType.PostgreSQL, DatabaseType.MySQL })
        {
            await Assert.ThrowsAsync<UnsafeOperandException>(() => service.GetTablesAsync("db-1", database, type, creds, "local"));
            await Assert.ThrowsAsync<UnsafeOperandException>(() => service.GetSchemaAsync("db-1", database, "users", type, creds, "local"));
        }
        Assert.Empty(host.Commands);
    }

    [Theory]
    [InlineData("app")]
    [InlineData("app_db")]
    [InlineData("my-app_v2")]
    public async Task A_plain_database_name_is_listed(string database)
    {
        var host = new RecordingHost();
        var service = new DatabaseService(host, NullLogger<DatabaseService>.Instance);
        await service.GetTablesAsync("db-1", database, DatabaseType.MongoDB, new DatabaseCredentials { User = "app" }, "local");
        var command = Assert.Single(host.Commands);
        Assert.Contains($" 'mongosh' '--quiet' '{database}' '--eval' ", command);
    }

    /// <summary>Review M1: the dump path takes the same name — as an operand of pg_dump / mysqldump / mongodump (a
    /// <c>--file=…</c> would choose where the dump is written) and as part of the file name <c>docker cp</c> writes as
    /// root on the host (a <c>../</c> would leave the backup directory). Both are refused before anything runs, and so
    /// is the database a query runs in.</summary>
    [Theory]
    [InlineData("../x")]
    [InlineData("../../../../etc/logrotate.d/x")]
    [InlineData("--file=x")]
    [InlineData("--result-file=/etc/cron.d/x")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task A_dump_or_a_query_refuses_a_name_that_is_an_option_or_a_path(string database)
    {
        var host = new RecordingHost();
        var service = new DatabaseService(host, NullLogger<DatabaseService>.Instance);
        foreach (var type in new[] { DatabaseType.MongoDB, DatabaseType.PostgreSQL, DatabaseType.MySQL })
        {
            await Assert.ThrowsAsync<UnsafeOperandException>(() =>
                service.BackupDatabaseAsync("db-1", database, type, new DatabaseCredentials { User = "app" }, "local"));
            if (database.Length > 0)
                await Assert.ThrowsAsync<UnsafeOperandException>(() => service.ExecuteQueryAsync("db-1", "select 1", type,
                    new DatabaseCredentials { User = "app", Database = database }, "local"));
        }
        Assert.Empty(host.Commands);
    }

    [Fact]
    public async Task A_plain_name_is_dumped_into_the_backup_directory()
    {
        var host = new RecordingHost(exitCode: 0);
        var service = new DatabaseService(host, NullLogger<DatabaseService>.Instance);
        await service.BackupDatabaseAsync("db-1", "app_db", DatabaseType.PostgreSQL, new DatabaseCredentials { User = "app" }, "local");
        var copy = Assert.Single(host.Commands, c => c.StartsWith("docker cp -- ", StringComparison.Ordinal));
        Assert.Matches(@"backups/app_db_\d{8}_\d{6}\.sql' 2>&1\z", copy);
        // A query without a database of its own keeps the client's default, as before.
        await service.ExecuteQueryAsync("db-1", "select 1", DatabaseType.PostgreSQL, new DatabaseCredentials { User = "app" }, "local");
    }

    /// <summary>Review m2: MongoDB forbids <c>.</c> in a database name, and the mongo shells read a dotted positional
    /// argument as a host; a dotted name is refused for MongoDB on every database path, kept for the others.</summary>
    [Theory]
    [InlineData("attacker.example")]
    [InlineData("10.0.0.5")]
    [InlineData("db.example.com")]
    public async Task A_dotted_name_is_refused_for_mongodb(string database)
    {
        var host = new RecordingHost();
        var service = new DatabaseService(host, NullLogger<DatabaseService>.Instance);
        var creds = new DatabaseCredentials { User = "app", Database = database };
        await Assert.ThrowsAsync<UnsafeOperandException>(() => service.GetTablesAsync("db-1", database, DatabaseType.MongoDB, creds, "local"));
        await Assert.ThrowsAsync<UnsafeOperandException>(() => service.BackupDatabaseAsync("db-1", database, DatabaseType.MongoDB, creds, "local"));
        await Assert.ThrowsAsync<UnsafeOperandException>(() => service.ExecuteQueryAsync("db-1", "db.x.find()", DatabaseType.MongoDB, creds, "local"));
        Assert.Empty(host.Commands);
        await service.GetTablesAsync("db-1", database, DatabaseType.PostgreSQL, creds, "local");
        Assert.Single(host.Commands);
    }

    // ── Approvals: nothing below Admin covers a member ─────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> McpMembers() => CapabilityCatalog.RootEquivalent.Order(StringComparer.Ordinal)
        .Where(id => Catalog.Find(id)!.McpTool is not null)
        .Select(id => new object[] { id });

    private static readonly ScheduledTaskEntity CommandTask =
        new() { TaskId = "t-cmd", TaskType = ScheduledTaskType.CustomCommand, Config = "{\"command\":\"id\"}" };

    private static readonly MutationPrincipal AgentForAdmin = new(MutationPrincipalKind.Agent, "agent:s1", "agent:s1",
        McpPermissionLevels.Admin, SessionId: "s1",
        OnBehalfOf: new MutationPrincipal(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com",
            McpPermissionLevels.Admin));

    /// <summary>The arguments of a member's call: a command task for the scheduler, otherwise the usual keys.</summary>
    private static object ArgumentsOf(string id) => id switch
    {
        "scheduler.create" => new { name = "n", cronExpression = "0 2 * * *", taskType = "CustomCommand", command = "id" },
        "scheduler.run" => new { taskId = CommandTask.TaskId },
        _ => new { serverId = "local", containerId = "web-1", command = "id", query = "select 1", yaml = "services: {}" },
    };

    private static (MutationGateway Gateway, ApprovalStore Approvals, AgentToolTestHelpers.NullMutationAudit Audit) Build()
    {
        var approvals = new ApprovalStore();
        var guardrails = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());   // writes need a human
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var resolver = new SchedulerRequirementResolver(Catalog, new SchedulerOnly(
            ApprovalSeparationOfDutyTests.SchedulerProxy.Create([CommandTask])));
        var gateway = new MutationGateway(Catalog, AgentToolTestHelpers.Policy(guardrails), audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(approvals, guardrails), requirements: resolver);
        return (gateway, approvals, audit);
    }

    private static MutationRequest Request(string id, string operation) =>
        new(id, AgentForAdmin, MutationSurface.Agent, new MutationTarget(Catalog.Find(id)!.TargetKind, "t"),
            Args(ArgumentsOf(id)), operation, "s1");

    /// <summary>The real gateway, approval gate and store: a member's call by the agent raises its approval at Admin,
    /// an Operator can neither decide it nor lend it an approval of their own for exactly this call, and nothing runs.</summary>
    [Theory]
    [MemberData(nameof(McpMembers))]
    public async Task A_member_is_approved_only_at_admin_and_a_write_approval_never_covers_it(string id)
    {
        var (gateway, approvals, audit) = Build();
        var runs = 0;
        Task<int> Run(MutationContext _, CancellationToken __) => Task.FromResult(++runs);

        var pending = await gateway.ExecuteAsync(Request(id, "op-1"), Run);
        Assert.Equal(MutationStatus.PendingApproval, pending.Status);
        var approval = approvals.Get(pending.ApprovalId!)!;
        Assert.Equal(McpPermissionLevels.Admin, approval.Level);
        Assert.False(await approvals.ResolveAsync(approval.Id, approved: true, "operator@example.com", McpPermissionLevels.Write));
        Assert.NotEqual(MutationStatus.Executed, (await gateway.ExecuteAsync(Request(id, "op-1"), Run)).Status);

        // A Write approval an Operator granted for exactly this call — same session, operation, tool, arguments and the
        // binding the gateway itself computes for it — is never used, and the reason is its level (review m3).
        var (gateway2, approvals2, audit2) = Build();
        await PlantAsync(gateway2, approvals2, id, "op-2", McpPermissionLevels.Write, "operator@example.com");
        var outcome = await gateway2.ExecuteAsync(Request(id, "op-2"), Run);
        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
        Assert.Contains("was given at level 'write'", outcome.Reason);
        Assert.Contains("requires 'admin'", outcome.Reason);

        Assert.Equal(0, runs);
        Assert.DoesNotContain(audit.Events, e => e.StartsWith("begin:", StringComparison.Ordinal));
        Assert.DoesNotContain(audit2.Events, e => e.StartsWith("begin:", StringComparison.Ordinal));

        // Positive control: the identical planted approval at Admin, decided by another Admin, is used for exactly this
        // call, once — so the only thing that kept the Write one out above was its level.
        var (gateway3, approvals3, audit3) = Build();
        await PlantAsync(gateway3, approvals3, id, "op-3", McpPermissionLevels.Admin, "second-admin@example.com");
        Assert.Equal(MutationStatus.Executed, (await gateway3.ExecuteAsync(Request(id, "op-3"), Run)).Status);
        Assert.NotEqual(MutationStatus.Executed, (await gateway3.ExecuteAsync(Request(id, "op-3"), Run)).Status);
        Assert.Equal(1, runs);
        Assert.Contains(audit3.Events, e => e.StartsWith($"begin:{id}:", StringComparison.Ordinal));

        // …and the approval the gateway raised itself runs once after another Admin decided it.
        Assert.True(await approvals.ResolveAsync(approval.Id, approved: true, "second-admin@example.com", McpPermissionLevels.Admin));
        Assert.Equal(MutationStatus.Executed, (await gateway.ExecuteAsync(Request(id, "op-1"), Run)).Status);
        Assert.Equal(2, runs);
    }

    /// <summary>Plants a decided approval for exactly the call <c>Request(id, operation)</c> at <paramref name="level"/>,
    /// bound as the gateway binds it (its own <c>DecideAsync</c>).</summary>
    private static async Task PlantAsync(MutationGateway gateway, ApprovalStore approvals, string id, string operation,
        string level, string decider)
    {
        var decided = await gateway.DecideAsync(Request(id, operation));
        Assert.NotNull(decided?.Binding);
        var planted = await approvals.CreateAsync(new ApprovalRequest("s1", "call-1", "admin@example.com", "agent-web",
            Catalog.Find(id)!.McpTool!, level, null, "needs a human", CorrelationId: operation,
            ArgumentsHash: ApprovalFingerprint.OfArguments(Args(ArgumentsOf(id))), Binding: decided!.Binding,
            Requesters: ["key:test-requester"]), _ => Task.CompletedTask);
        Assert.True(await approvals.ResolveAsync(planted.Id, approved: true, decider, level));
    }

    private static Dictionary<string, JsonElement> Args(object args) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!;

    private sealed class SchedulerOnly(object scheduler) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ISchedulerService) ? scheduler : null;
    }

    private sealed class RecordingHost(int exitCode = 1) : IHostCommandExecutor
    {
        public List<string> Commands { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Commands.Add(command);
            return Task.FromResult(new CommandResult { ExitCode = exitCode });
        }
    }
}
