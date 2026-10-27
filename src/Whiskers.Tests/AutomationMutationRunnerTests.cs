using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;

namespace Whiskers.Tests;

/// <summary>
/// WP2 step 2.3 (PC-D6): unattended runs act on behalf of a recorded owner whose authority is re-read on every run
/// (INV-04). Items from before owners were recorded run in a grace period, limited to the capability they run;
/// <c>Automation:RequireOwner</c> ends it.
/// </summary>
public class AutomationMutationRunnerTests
{
    private const string Ops = "ops@example.com";

    internal sealed class Roles : IRoleService
    {
        public Dictionary<string, AppRole> Map { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public AppRole GetRole(string? email) => email is not null && Map.TryGetValue(email, out var r) ? r : AppRole.Viewer;
        public bool HasRole(string? email, AppRole requiredRole) => GetRole(email) >= requiredRole;
        public bool HasAnyRoles() => Map.Count > 0;
        public bool HasExplicitRole(string? email) => email is not null && Map.ContainsKey(email);
        public UserRoleData GetRoleData() => throw new NotSupportedException();
        public Task SaveRoleDataAsync(UserRoleData data) => throw new NotSupportedException();
        public Task SetRoleAsync(string email, AppRole role) { Map[email] = role; return Task.CompletedTask; }
        public Task RemoveRoleAsync(string email) { Map.Remove(email); return Task.CompletedTask; }
    }

    internal sealed class Whitelist : IWhitelistService
    {
        public HashSet<string> Allowed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool IsEmailAllowed(string? email) => email is not null && Allowed.Contains(email);
        public WhitelistData GetWhitelist() => throw new NotSupportedException();
        public Task SaveWhitelistAsync(WhitelistData data) => throw new NotSupportedException();
    }

    internal sealed class Keys : IMcpPermissionService
    {
        public McpPermissionData Data { get; } = new();
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public McpApiKeyConfig? ValidateKey(string key) => throw new NotSupportedException();
        public bool IsToolAllowed(string key, string toolName) => throw new NotSupportedException();
        public HashSet<string> GetAllowedTools(string key) => throw new NotSupportedException();
        public McpPermissionData GetPermissionData() => Data;
        public Task SavePermissionDataAsync(McpPermissionData data) => throw new NotSupportedException();
        public Task<McpApiKeyConfig> AddApiKeyAsync(string name, string permissionLevel) => throw new NotSupportedException();
        public Task RemoveApiKeyAsync(string id) => throw new NotSupportedException();
        public Task SetToolEnabledAsync(string toolName, bool enabled) => throw new NotSupportedException();
    }

    /// <summary>Remembers the principal of every request the gateway audits.</summary>
    internal sealed class PrincipalAudit : IMutationAuditSink
    {
        private readonly AgentToolTestHelpers.NullMutationAudit _inner = new();
        public List<string> Events => _inner.Events;
        public List<MutationPrincipal> Principals { get; } = [];

        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct)
        {
            Principals.Add(request.Principal);
            return _inner.BeginAsync(operationId, request, capability, approval, ct);
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct) =>
            _inner.CompleteAsync(auditId, operationId, status, verification, reason, duration, ct);

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct)
        {
            Principals.Add(request.Principal);
            return _inner.RecordDeniedAsync(operationId, request, capability, status, reason, ct);
        }
    }

    internal sealed record Fixture(AutomationMutationRunner Runner, PrincipalAudit Audit, Roles Roles,
        Whitelist Whitelist, Keys Keys, AutomationOptions Options, ApprovalStore Approvals);

    internal static Fixture Build(bool requireOwner = false, GuardrailPolicy? policy = null)
    {
        var roles = new Roles();
        var whitelist = new Whitelist();
        var keys = new Keys();
        var options = new AutomationOptions { RequireOwner = requireOwner };
        var audit = new PrincipalAudit();
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy ?? GuardrailPolicy.SafeDefault());
        var approvals = new ApprovalStore();
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(approvals, store));
        var runner = new AutomationMutationRunner(gateway, new CapabilityCatalog(),
            new AutomationOwnerResolver(roles, whitelist, keys), new StaticOptionsMonitor<AutomationOptions>(options));
        return new Fixture(runner, audit, roles, whitelist, keys, options, approvals);
    }

    private static Fixture WithOperator(bool requireOwner = false)
    {
        var f = Build(requireOwner);
        f.Whitelist.Allowed.Add(Ops);
        f.Roles.Map[Ops] = AppRole.Operator;
        return f;
    }

    private static readonly AutomationActor Task1 = new("task:t1", "Scheduled task 'nightly'", Ops);

    [Fact]
    public async Task An_owned_run_acts_on_behalf_of_its_owner_without_asking_for_approval()
    {
        // SafeDefault asks the agent to confirm every write; an owner already said yes by scheduling the task.
        var f = WithOperator();

        var outcome = await f.Runner.RunAsync(Task1, "container.restart", new { containerId = "web-1" },
            _ => Task.FromResult(7));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(7, outcome.Value);
        var principal = Assert.Single(f.Audit.Principals);
        Assert.Equal(MutationPrincipalKind.Automation, principal.Kind);
        Assert.Equal("task:t1", principal.Id);
        Assert.Equal(Ops, principal.OnBehalfOf?.Id);
        Assert.Equal(McpPermissionLevels.Write, principal.PermissionLevel);
    }

    [Fact]
    public async Task The_owners_level_is_the_ceiling()
    {
        var f = WithOperator();
        var ran = false;

        var outcome = await f.Runner.RunAsync(Task1, "host.exec", new { serverId = "local", hasCommand = true },
            _ => { ran = true; return Task.CompletedTask; });

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
    }

    [Fact]
    public async Task A_demoted_owner_takes_the_tasks_authority_with_them_on_the_next_run()
    {
        var f = WithOperator();
        Assert.Equal(MutationStatus.Executed,
            (await f.Runner.RunAsync(Task1, "container.restart", new { containerId = "web-1" }, _ => Task.CompletedTask)).Status);

        f.Roles.Map[Ops] = AppRole.Viewer;
        var ran = false;
        var outcome = await f.Runner.RunAsync(Task1, "container.restart", new { containerId = "web-1" },
            _ => { ran = true; return Task.CompletedTask; });

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
    }

    [Fact]
    public async Task An_owner_removed_from_the_whitelist_has_no_authority_left_whatever_role_remains()
    {
        var f = WithOperator();
        f.Whitelist.Allowed.Clear();

        var outcome = await f.Runner.RunAsync(Task1, "container.restart", new { containerId = "web-1" },
            _ => Task.CompletedTask);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("no longer allowed", Assert.Single(f.Audit.Principals).DisplayName);
    }

    /// <summary>A policy that lets keys act without confirmation, so the key's own state decides.</summary>
    private static GuardrailPolicy Unattended()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.RequireConfirmationForWrites = false;
        policy.MaxAutonomousLevel = "admin";
        return policy;
    }

    [Theory]
    [InlineData(true, "write", MutationStatus.Executed)]
    [InlineData(false, "write", MutationStatus.Denied)]   // disabled
    [InlineData(true, "read", MutationStatus.Denied)]
    public async Task A_key_owned_run_follows_the_keys_current_state(bool enabled, string level, MutationStatus expected)
    {
        var f = Build(policy: Unattended());
        f.Keys.Data.ApiKeys.Add(new McpApiKeyConfig { Id = "k1", Name = "ci", PermissionLevel = level, Enabled = enabled });
        var actor = new AutomationActor("task:t2", "Scheduled task 'ci'", AutomationOwners.IdOf("k1", "ignored@example.com"));

        var outcome = await f.Runner.RunAsync(actor, "container.restart", new { containerId = "web-1" },
            _ => Task.CompletedTask);

        Assert.Equal(expected, outcome.Status);
        Assert.Equal("key:k1", Assert.Single(f.Audit.Principals).OnBehalfOf?.Id);
    }

    [Fact]
    public async Task A_key_owned_task_keeps_the_keys_deny_list()
    {
        var policy = Unattended();
        policy.ToolDenyList.Add("restart_container");
        var f = Build(policy: policy);
        f.Keys.Data.ApiKeys.Add(new McpApiKeyConfig { Id = "k1", Name = "ci", PermissionLevel = "admin" });
        var ran = false;

        var outcome = await f.Runner.RunAsync(new AutomationActor("task:t2", "Scheduled task 'ci'", "key:k1"),
            "container.restart", new { containerId = "web-1" }, _ => { ran = true; return Task.CompletedTask; });

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
    }

    [Fact]
    public async Task A_key_owned_task_that_would_need_an_approval_is_refused_not_auto_confirmed()
    {
        var f = Build();   // SafeDefault: keys confirm every write
        f.Keys.Data.ApiKeys.Add(new McpApiKeyConfig { Id = "k1", Name = "ci", PermissionLevel = "admin" });
        var ran = false;

        var outcome = await f.Runner.RunAsync(new AutomationActor("task:t2", "Scheduled task 'ci'", "key:k1"),
            "container.restart", new { containerId = "web-1" }, _ => { ran = true; return Task.CompletedTask; });

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);   // terminal, not PendingApproval
        Assert.Empty(f.Approvals.GetAll());                    // no approval was raised for anyone to grant
    }

    [Fact]
    public async Task Approving_a_refused_occurrence_cannot_release_the_next_occurrence()
    {
        var f = Build();   // SafeDefault: keys confirm every write
        f.Keys.Data.ApiKeys.Add(new McpApiKeyConfig { Id = "k1", Name = "ci", PermissionLevel = "admin" });
        var actor = new AutomationActor("task:t9", "Scheduled task 'every 5 min'", "key:k1");
        var runs = 0;
        Task Restart(CancellationToken _) { runs++; return Task.CompletedTask; }

        var first = await f.Runner.RunAsync(actor, "container.restart", new { containerId = "web-1" }, Restart);
        // An admin tries to approve in between: nothing is pending, and an approval id from the refusal (if any had
        // leaked) or a guessed one resolves to nothing.
        Assert.Empty(f.Approvals.GetPending());
        var attempted = await f.Approvals.ResolveAsync(first.ApprovalId ?? first.OperationId, approved: true,
            resolvedBy: "admin@example.com", resolverLevel: "admin");
        Assert.False(attempted);
        var second = await f.Runner.RunAsync(actor, "container.restart", new { containerId = "web-1" }, Restart);

        Assert.Equal(MutationStatus.Denied, first.Status);
        Assert.Equal(MutationStatus.Denied, second.Status);
        Assert.Null(first.ApprovalId);
        Assert.Null(second.ApprovalId);
        Assert.Equal(0, runs);
        Assert.Empty(f.Approvals.GetAll());
        Assert.Equal(2, f.Audit.Events.Count(e => e.StartsWith("denied", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_scheduled_self_backup_follows_its_owner_too()
    {
        var f = Build();   // ops@example.com is not on the whitelist → read
        var executor = new TaskExecutor(new FakeDocker(), null!, null!, null!, null!, f.Runner,
            NullLogger<TaskExecutor>.Instance, notify: new FakeNotifications());

        var (success, _) = await executor.ExecuteAsync(new ScheduledTaskEntity
        {
            TaskId = "t3", Name = "self", TaskType = ScheduledTaskType.SelfBackup, CreatedBy = Ops,
        });

        Assert.False(success);   // refused before the (null) self-backup service is reached
        Assert.Contains(f.Audit.Events, e => e.StartsWith("denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_answered_as_a_value_is_recorded_as_failed()
    {
        var f = WithOperator();

        var outcome = await f.Runner.RunAsync(Task1, "container.restart", new { containerId = "web-1" },
            _ => Task.FromResult((false, "docker said no")));

        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.Contains("complete:Failed", f.Audit.Events);
    }

    [Fact]
    public async Task A_deleted_key_leaves_its_tasks_without_authority()
    {
        var f = Build();
        var actor = new AutomationActor("task:t2", "Scheduled task 'ci'", "key:gone");

        var outcome = await f.Runner.RunAsync(actor, "container.restart", new { containerId = "web-1" },
            _ => Task.CompletedTask);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("(deleted)", Assert.Single(f.Audit.Principals).DisplayName);
    }

    [Fact]
    public async Task An_unowned_item_keeps_running_during_the_grace_period_and_is_audited_as_such()
    {
        var f = Build();
        var actor = new AutomationActor("task:old", "Scheduled task 'old'", null);

        var outcome = await f.Runner.RunAsync(actor, "container.restart", new { containerId = "web-1" },
            _ => Task.CompletedTask);

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        var principal = Assert.Single(f.Audit.Principals);
        Assert.Equal("unowned:task:old", principal.Id);
        Assert.Contains("no owner", principal.DisplayName);
        Assert.Null(principal.OnBehalfOf);
    }

    [Fact]
    public async Task The_grace_period_still_respects_the_kill_switch_and_protected_resources()
    {
        var f = Build();
        var actor = new AutomationActor("task:old", "Scheduled task 'old'", null);

        // SafeDefault protects whiskers*.
        var outcome = await f.Runner.RunAsync(actor, "container.restart", new { containerId = "whiskers" },
            _ => Task.CompletedTask);

        Assert.Equal(MutationStatus.Denied, outcome.Status);
    }

    [Fact]
    public async Task RequireOwner_refuses_every_unowned_run()
    {
        var f = Build(requireOwner: true);
        var ran = false;

        var outcome = await f.Runner.RunAsync(new AutomationActor("autoupdate", "AutoUpdate", null),
            "container.update", new { containerId = "web-1" }, _ => { ran = true; return Task.CompletedTask; });

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
    }

    [Theory]
    [InlineData("k1", "a@example.com", "key:k1")]
    [InlineData(null, "a@example.com", "a@example.com")]
    [InlineData("", " ", null)]
    public void The_recorded_owner_is_the_key_when_there_is_one(string? keyId, string? email, string? expected) =>
        Assert.Equal(expected, AutomationOwners.IdOf(keyId, email));

    [Fact]
    public void A_task_created_by_the_agent_belongs_to_whoever_the_agent_acts_for()
    {
        var user = new MutationPrincipal(MutationPrincipalKind.Human, Ops, Ops, McpPermissionLevels.Write);
        var key = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Write, "k1");
        var trigger = new MutationPrincipal(MutationPrincipalKind.Automation, "ai-trigger:x", "x", McpPermissionLevels.Admin, "ai-trigger");
        MutationPrincipal Agent(MutationPrincipal source) =>
            new(MutationPrincipalKind.Agent, "agent:x", "agent:x", source.PermissionLevel, OnBehalfOf: source);

        Assert.Equal(Ops, AutomationOwners.OfCaller(user));
        Assert.Equal("key:k1", AutomationOwners.OfCaller(key));
        Assert.Equal(Ops, AutomationOwners.OfCaller(Agent(user)));
        Assert.Equal("key:k1", AutomationOwners.OfCaller(Agent(key)));
        // An AI trigger has no human or key behind it; its task would otherwise run unowned.
        Assert.Null(AutomationOwners.OfCaller(Agent(trigger)));
        Assert.Null(AutomationOwners.OfCaller(null));
    }

    [Fact]
    public async Task The_gateway_tells_the_running_code_who_it_runs_for()
    {
        var f = WithOperator();
        MutationPrincipal? seen = null;

        await f.Runner.RunAsync(Task1, "container.restart", new { containerId = "web-1" },
            _ => { seen = MutationScope.Current?.Principal; return Task.CompletedTask; });

        Assert.Equal("task:t1", seen?.Id);
        Assert.Null(MutationScope.Current);
    }

    [Fact]
    public async Task A_refused_scheduled_task_reports_the_refusal_and_notifies()
    {
        var f = Build();   // ops@example.com is not on the whitelist → read
        var notify = new FakeNotifications();
        var executor = new TaskExecutor(new FakeDocker(), null!, null!, null!, null!, f.Runner,
            NullLogger<TaskExecutor>.Instance, notify: notify);

        var (success, output) = await executor.ExecuteAsync(new ScheduledTaskEntity
        {
            TaskId = "t1", Name = "nightly", TaskType = ScheduledTaskType.ContainerRestart,
            TargetId = "web-1", CreatedBy = Ops,
        });

        Assert.False(success);
        Assert.False(string.IsNullOrWhiteSpace(output));
        var evt = Assert.Single(notify.Events);
        Assert.Equal("automation_refused", evt.EventType);
    }

    /// <summary>Records every command it is asked to run.</summary>
    internal sealed class CapturingHost : Whiskers.Services.Server.IHostCommandExecutor
    {
        public List<string> Commands { get; } = [];
        public Task<Whiskers.Services.Server.CommandResult> ExecuteAsync(string serverId, string command,
            TimeSpan? timeout = null, CancellationToken ct = default, int? maxOutputChars = null)
        {
            Commands.Add(command);
            return Task.FromResult(new Whiskers.Services.Server.CommandResult { ExitCode = 0, Output = "", Error = "" });
        }
    }

    [Fact]
    public async Task A_scheduled_command_meets_the_forbidden_arguments_of_today_not_of_its_creation()
    {
        var policy = Unattended();
        var f = Build(policy: policy);
        f.Keys.Data.ApiKeys.Add(new McpApiKeyConfig { Id = "k1", Name = "ci", PermissionLevel = "admin" });
        var host = new CapturingHost();
        var executor = new TaskExecutor(new FakeDocker(), null!, null!, null!, host, f.Runner,
            NullLogger<TaskExecutor>.Instance, notify: new FakeNotifications());
        var task = new ScheduledTaskEntity
        {
            TaskId = "t4", Name = "wipe", TaskType = ScheduledTaskType.CustomCommand, CreatedBy = "key:k1",
            Config = "{\"command\":\"docker image prune -af\"}",
        };

        var (before, output) = await executor.ExecuteAsync(task);
        policy.ForbiddenArgPatterns.Add(@"\bprune\s+-af\b");   // an admin tightens the policy after the task was created
        var (after, _) = await executor.ExecuteAsync(task);

        Assert.True(before, output);
        Assert.False(after);
        Assert.Single(host.Commands);
    }

    [Fact]
    public async Task A_refused_command_never_carries_its_text_into_the_result_or_the_notification()
    {
        var policy = Unattended();
        policy.ProtectedResources.Add("*prod*");   // matches the whole command, which the rule then quotes
        var f = Build(policy: policy);
        f.Keys.Data.ApiKeys.Add(new McpApiKeyConfig { Id = "k1", Name = "ci", PermissionLevel = "admin" });
        var host = new CapturingHost();
        var notify = new FakeNotifications();
        var executor = new TaskExecutor(new FakeDocker(), null!, null!, null!, host, f.Runner,
            NullLogger<TaskExecutor>.Instance, notify: notify);

        var (success, output) = await executor.ExecuteAsync(new ScheduledTaskEntity
        {
            TaskId = "t5", Name = "sync", TaskType = ScheduledTaskType.CustomCommand, CreatedBy = "key:k1",
            Config = "{\"command\":\"curl https://prod.example.com/sync -H 'X-Auth: Violet47Lantern'\"}",
        });

        Assert.False(success);
        Assert.Empty(host.Commands);
        Assert.DoesNotContain("Violet47Lantern", output);
        Assert.Contains("protected", output);
        Assert.All(notify.Events, e => Assert.DoesNotContain("Violet47Lantern", e.ImageInfo ?? ""));
        Assert.NotEmpty(notify.Events);
    }

    [Fact]
    public async Task Taking_over_one_feature_after_a_broken_owner_file_leaves_the_other_blocked_across_restarts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"owners-{Guid.NewGuid():N}");
        var paths = new DataPathOptions(dir);
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(paths.AutomationOwnersJson, "{ not json");
            var store = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            Assert.True(store.IsBlocked(IAutomationOwnerStore.AutoPatch));
            Assert.True(store.IsBlocked(IAutomationOwnerStore.AutoUpdate));

            await store.SetAsync(IAutomationOwnerStore.AutoPatch, "admin@example.com");
            Assert.False(store.IsBlocked(IAutomationOwnerStore.AutoPatch));
            Assert.True(store.IsBlocked(IAutomationOwnerStore.AutoUpdate));

            var restarted = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            Assert.Equal("admin@example.com", restarted.Get(IAutomationOwnerStore.AutoPatch)?.OwnerId);
            Assert.True(restarted.IsBlocked(IAutomationOwnerStore.AutoUpdate));   // not "no owner": no grace period
            Assert.Null(restarted.Get(IAutomationOwnerStore.AutoUpdate));
            Assert.NotEmpty(Directory.GetFiles(dir, "automation-owners.json.unreadable-*"));

            await restarted.SetAsync(IAutomationOwnerStore.AutoUpdate, "admin@example.com");
            Assert.False(new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance)
                .IsBlocked(IAutomationOwnerStore.AutoUpdate));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_directory_at_the_owner_file_path_blocks_instead_of_reading_as_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"owners-{Guid.NewGuid():N}");
        var paths = new DataPathOptions(dir);
        try
        {
            Directory.CreateDirectory(paths.AutomationOwnersJson);
            var store = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            Assert.True(store.IsBlocked(IAutomationOwnerStore.AutoUpdate));
            Assert.True(store.IsBlocked(IAutomationOwnerStore.AutoPatch));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task An_owner_file_that_cannot_be_opened_blocks_instead_of_reading_as_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"owners-{Guid.NewGuid():N}");
        var paths = new DataPathOptions(dir);
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(paths.AutomationOwnersJson,
                "{ \"autoupdate\": { \"OwnerId\": \"admin@example.com\", \"ConfirmedAt\": \"2026-10-01T00:00:00Z\" } }");
            using (new FileStream(paths.AutomationOwnersJson, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var store = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
                Assert.True(store.IsBlocked(IAutomationOwnerStore.AutoUpdate));
            }
            Assert.False(new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance)
                .IsBlocked(IAutomationOwnerStore.AutoUpdate));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task The_owner_store_survives_a_restart_and_reads_a_corrupt_file_as_blocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"owners-{Guid.NewGuid():N}");
        var paths = new DataPathOptions(dir);
        try
        {
            var first = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            await first.SetAsync(IAutomationOwnerStore.AutoPatch, "admin@example.com");

            var second = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            Assert.Equal("admin@example.com", second.Get(IAutomationOwnerStore.AutoPatch)?.OwnerId);
            Assert.Null(second.Get(IAutomationOwnerStore.AutoUpdate));

            Assert.False(second.IsBlocked(IAutomationOwnerStore.AutoUpdate));   // a missing file is no failure

            await File.WriteAllTextAsync(paths.AutomationOwnersJson, "{ not json");
            var third = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            Assert.Null(third.Get(IAutomationOwnerStore.AutoPatch));
            Assert.True(third.IsBlocked(IAutomationOwnerStore.AutoUpdate));   // fail closed: not "unowned" (grace period)

            await File.WriteAllTextAsync(paths.AutomationOwnersJson, "{ \"autoupdate\": { \"OwnerId\": \"\" } }");
            Assert.True(new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance)
                .IsBlocked(IAutomationOwnerStore.AutoUpdate));

            await third.SetAsync(IAutomationOwnerStore.AutoUpdate, "admin@example.com");
            Assert.False(third.IsBlocked(IAutomationOwnerStore.AutoUpdate));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
