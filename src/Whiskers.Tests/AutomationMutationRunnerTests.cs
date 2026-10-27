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
    private static readonly string OpsId = PrincipalIds.User(Ops);

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
        Whitelist Whitelist, Keys Keys, AutomationOptions Options, ApprovalStore Approvals, Accounts Accounts);

    /// <summary>Local accounts: every email exists unless it was deleted.</summary>
    internal sealed class Accounts : IOwnerAccounts
    {
        public HashSet<string> Deleted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Unverified { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string email) => StateOf(email) == OwnerAccountState.Present;
        public OwnerAccountState StateOf(string email) =>
            Deleted.Contains(email) ? OwnerAccountState.Deleted
            : Unverified.Contains(email) ? OwnerAccountState.Unverified
            : OwnerAccountState.Present;
    }

    internal static Fixture Build(bool requireOwner = false, GuardrailPolicy? policy = null)
    {
        var roles = new Roles();
        var whitelist = new Whitelist();
        var keys = new Keys();
        var options = new AutomationOptions { RequireOwner = requireOwner };
        var accounts = new Accounts();
        var audit = new PrincipalAudit();
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy ?? GuardrailPolicy.SafeDefault());
        var approvals = new ApprovalStore();
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(approvals, store));
        var runner = new AutomationMutationRunner(gateway, new CapabilityCatalog(),
            new AutomationOwnerResolver(roles, whitelist, keys, accounts), new StaticOptionsMonitor<AutomationOptions>(options));
        return new Fixture(runner, audit, roles, whitelist, keys, options, approvals, accounts);
    }

    private static Fixture WithOperator(bool requireOwner = false)
    {
        var f = Build(requireOwner);
        f.Whitelist.Allowed.Add(Ops);
        f.Roles.Map[Ops] = AppRole.Operator;
        return f;
    }

    private static readonly AutomationActor Task1 = new("task:t1", "Scheduled task 'nightly'", OpsId, OpsId);

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
        Assert.Equal(OpsId, principal.OnBehalfOf?.Id);
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
            TaskId = "t3", Name = "self", TaskType = ScheduledTaskType.SelfBackup, CreatedBy = OpsId,
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
    [InlineData(null, "a@example.com", "user:a@example.com")]
    [InlineData("", " ", null)]
    public void The_recorded_owner_is_the_key_when_there_is_one(string? keyId, string? email, string? expected) =>
        Assert.Equal(expected, AutomationOwners.IdOf(keyId, email));

    [Fact]
    public void A_task_created_by_the_agent_belongs_to_whoever_the_agent_acts_for()
    {
        var user = new MutationPrincipal(MutationPrincipalKind.Human, OpsId, Ops, McpPermissionLevels.Write);
        var key = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Write, "k1");
        var trigger = new MutationPrincipal(MutationPrincipalKind.Automation, "ai-trigger:x", "x", McpPermissionLevels.Admin, "ai-trigger");
        MutationPrincipal Agent(MutationPrincipal source) =>
            new(MutationPrincipalKind.Agent, "agent:x", "agent:x", source.PermissionLevel, OnBehalfOf: source);

        Assert.Equal(OpsId, AutomationOwners.OfCaller(user));
        Assert.Equal("key:k1", AutomationOwners.OfCaller(key));
        Assert.Equal(OpsId, AutomationOwners.OfCaller(Agent(user)));
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
    public async Task A_task_whose_owner_lost_authority_is_suspended_and_an_admin_is_told()
    {
        var f = Build();   // ops@example.com is not on the whitelist → read
        var notify = new FakeNotifications();
        var executor = new TaskExecutor(new FakeDocker(), null!, null!, null!, null!, f.Runner,
            NullLogger<TaskExecutor>.Instance, notify: notify);
        var task = new ScheduledTaskEntity
        {
            TaskId = "t1", Name = "nightly", TaskType = ScheduledTaskType.ContainerRestart,
            TargetId = "web-1", CreatedBy = OpsId,
        };

        var (success, output) = await executor.ExecuteAsync(task);

        Assert.False(success);
        Assert.StartsWith("Suspended:", output);
        Assert.False(task.Enabled);
        Assert.Equal("automation_suspended", Assert.Single(notify.Events).EventType);
        Assert.Equal(MutationStatus.Denied.ToString(), f.Audit.Events.Single(e => e.StartsWith("denied:")).Split(':')[1]);
    }

    /// <summary>Takes backups; holds three old ones, so retention wants to delete.</summary>
    private sealed class Backups : Whiskers.Services.Backup.IVolumeBackupService
    {
        public List<string> Deleted { get; } = [];
        public Task<VolumeBackupEntity> BackupVolumeAsync(string volumeName, string containerName, string? serverId = null,
            string? notes = null) => Task.FromResult(new VolumeBackupEntity { VolumeName = volumeName, FileName = "new.tar.gz" });
        public Task RestoreVolumeAsync(string backupId, string? targetVolume = null) => throw new NotSupportedException();
        public Task<List<VolumeBackupEntity>> ListBackupsAsync(string? serverId = null, string? volumeName = null) =>
            Task.FromResult(Enumerable.Range(0, 3).Select(i => new VolumeBackupEntity
            {
                BackupId = $"b{i}", VolumeName = volumeName ?? "", CreatedAt = DateTime.UtcNow.AddDays(-i),
            }).ToList());
        public Task DeleteBackupAsync(string backupId) { Deleted.Add(backupId); return Task.CompletedTask; }
        public Task<List<string>> ListVolumesAsync(string? serverId = null) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_backup_task_whose_owner_can_no_longer_delete_its_old_backups_is_suspended()
    {
        // An Admin set up a backup with retention, then was demoted to Operator: the backup itself (Write) still
        // runs, but retention deletes (Admin) are refused every night. That is a lost authority for the task.
        var f = WithOperator(requireOwner: true);
        var notify = new FakeNotifications();
        var backups = new Backups();
        var executor = new TaskExecutor(new FakeDocker(), null!, backups, null!, null!, f.Runner,
            NullLogger<TaskExecutor>.Instance, notify: notify);
        var task = new ScheduledTaskEntity
        {
            TaskId = "t1", Name = "volumes", TaskType = ScheduledTaskType.VolumeBackup,
            TargetId = "data", CreatedBy = OpsId, CreatedVia = OpsId, Config = """{"maxBackups":"1"}""",
        };

        var (success, output) = await executor.ExecuteAsync(task);

        Assert.False(success);
        Assert.StartsWith("Suspended:", output);
        Assert.False(task.Enabled);
        Assert.Empty(backups.Deleted);
        Assert.Equal("automation_suspended", Assert.Single(notify.Events).EventType);
    }

    [Fact]
    public async Task An_unverified_owner_is_refused_but_the_task_is_not_suspended()
    {
        // Mixed sign-in, owner not yet seen signed in federated (R1-04): fail closed, but the task runs again as soon
        // as the owner signs in once — it is not switched off.
        var f = WithOperator(requireOwner: true);
        f.Accounts.Unverified.Add(Ops);
        var notify = new FakeNotifications();
        var executor = new TaskExecutor(new FakeDocker(), null!, null!, null!, null!, f.Runner,
            NullLogger<TaskExecutor>.Instance, notify: notify);
        var task = new ScheduledTaskEntity
        {
            TaskId = "t1", Name = "nightly", TaskType = ScheduledTaskType.ContainerRestart,
            TargetId = "web-1", CreatedBy = OpsId, CreatedVia = OpsId,
        };

        var (success, _) = await executor.ExecuteAsync(task);

        Assert.False(success);
        Assert.True(task.Enabled);
        Assert.Equal("automation_refused", Assert.Single(notify.Events).EventType);
        Assert.Null(f.Runner.LostAuthority(Task1, "container.restart"));
    }

    [Theory]
    [InlineData("OpenIdConnect", "ops@example.com")]
    [InlineData("Google", "ops@example.com")]
    [InlineData("LocalPassword", null)]
    [InlineData("AuthDisabled", null)]
    [InlineData("AgentSynthetic", null)]
    [InlineData("McpBearer", null)]
    public void Only_a_federated_sign_in_is_recorded_as_one(string authenticationType, string? expected)
    {
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, Ops)], authenticationType));
        Assert.Equal(expected, FederatedSignInRecorder.FederatedEmailOf(user));
        Assert.Null(FederatedSignInRecorder.FederatedEmailOf(new System.Security.Claims.ClaimsPrincipal()));
    }

    [Fact]
    public async Task A_task_refused_by_policy_stays_enabled_and_reports_the_refusal()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;   // kill switch: a policy refusal, not a lost authority
        var f = Build(policy: policy);
        f.Whitelist.Allowed.Add(Ops);
        f.Roles.Map[Ops] = AppRole.Operator;
        var notify = new FakeNotifications();
        var executor = new TaskExecutor(new FakeDocker(), null!, null!, null!, null!, f.Runner,
            NullLogger<TaskExecutor>.Instance, notify: notify);
        var task = new ScheduledTaskEntity
        {
            TaskId = "t1", Name = "nightly", TaskType = ScheduledTaskType.ContainerRestart,
            TargetId = "web-1", CreatedBy = OpsId,
        };

        var (success, _) = await executor.ExecuteAsync(task);

        Assert.False(success);
        Assert.True(task.Enabled);
        Assert.Equal("automation_refused", Assert.Single(notify.Events).EventType);
    }

    // --- R1-04: authority loss and delegation ----------------------------------------------------------------

    public static TheoryData<string, string> LostAuthorityCases => new()
    {
        { "account-deleted", "container.restart" },
        { "whitelist-removed", "container.restart" },
        { "demoted-to-viewer", "container.restart" },
        { "demoted-below-capability", "host.exec" },
        { "key-deleted", "container.restart" },
        { "key-disabled", "container.restart" },
        { "key-lowered", "host.exec" },
        { "unrecognised-owner", "container.restart" },
        { "no-owner", "container.restart" },
    };

    [Theory]
    [MemberData(nameof(LostAuthorityCases))]
    public async Task An_owner_who_lost_authority_is_refused_and_reported_as_lost(string change, string capability)
    {
        var f = Build(requireOwner: true);
        f.Whitelist.Allowed.Add(Ops);
        f.Roles.Map[Ops] = AppRole.Admin;
        f.Keys.Data.ApiKeys.Add(new McpApiKeyConfig { Id = "k1", Name = "ci", PermissionLevel = "admin" });
        var owner = change.StartsWith("key", StringComparison.Ordinal) ? "key:k1" : OpsId;
        var key = f.Keys.Data.ApiKeys[0];
        switch (change)
        {
            case "account-deleted": f.Accounts.Deleted.Add(Ops); break;   // whitelist and role entries remain
            case "whitelist-removed": f.Whitelist.Allowed.Clear(); break;
            case "demoted-to-viewer": f.Roles.Map[Ops] = AppRole.Viewer; break;
            case "demoted-below-capability": f.Roles.Map[Ops] = AppRole.Operator; break;
            case "key-deleted": f.Keys.Data.ApiKeys.Clear(); break;
            case "key-disabled": key.Enabled = false; break;
            case "key-lowered": key.PermissionLevel = "write"; break;
            case "unrecognised-owner": owner = "Claude Code"; break;
            case "no-owner": owner = null; break;
        }
        var actor = new AutomationActor("task:t1", "Scheduled task 'nightly'", owner);
        var ran = false;

        var outcome = await f.Runner.RunAsync(actor, capability,
            new { containerId = "web-1", serverId = "local", hasCommand = true }, _ => { ran = true; return Task.CompletedTask; });

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.NotNull(f.Runner.LostAuthority(actor, capability));
    }

    [Fact]
    public void An_owner_who_holds_the_capability_has_not_lost_authority()
    {
        var f = WithOperator();

        Assert.Null(f.Runner.LostAuthority(Task1, "container.restart"));
        Assert.NotNull(f.Runner.LostAuthority(Task1, "host.exec"));   // Operator, but the capability needs Admin
    }

    [Fact]
    public void Owners_are_required_by_default_and_the_grace_period_stays_available_explicitly()
    {
        Assert.True(new AutomationOptions().RequireOwner);
        var f = Build(requireOwner: false);
        Assert.Null(f.Runner.LostAuthority(new AutomationActor("task:t9", "legacy", null), "container.restart"));
    }

    private static AutomationActor AgentCreated(string capabilityTask = "t1") =>
        new("task:" + capabilityTask, "Scheduled task 'nightly'", OpsId, CreatedVia: "agent:sess-1");

    [Fact]
    public async Task An_agent_created_task_keeps_the_agents_confirmation_rule()
    {
        var f = WithOperator();   // SafeDefault: the agent may act autonomously only up to read

        var human = await f.Runner.RunAsync(Task1, "container.restart", new { containerId = "web-1" }, _ => Task.CompletedTask);
        var agent = await f.Runner.RunAsync(AgentCreated(), "container.restart", new { containerId = "web-1" },
            _ => Task.CompletedTask);

        Assert.Equal(MutationStatus.Executed, human.Status);   // the user scheduled it by hand
        Assert.Equal(MutationStatus.Denied, agent.Status);     // the agent's Confirm, unattended → refused
        Assert.Empty(f.Approvals.GetAll());
        Assert.Equal("agent:sess-1", f.Audit.Principals[1].DelegatedBy);
    }

    [Fact]
    public async Task An_agent_created_task_keeps_the_agents_tool_list()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        policy.ToolMode = "allow";
        policy.ToolAllowList = ["list_containers"];
        var f = Build(policy: policy);
        f.Whitelist.Allowed.Add(Ops);
        f.Roles.Map[Ops] = AppRole.Operator;

        var human = await f.Runner.RunAsync(Task1, "container.restart", new { containerId = "web-1" }, _ => Task.CompletedTask);
        var agent = await f.Runner.RunAsync(AgentCreated(), "container.restart", new { containerId = "web-1" },
            _ => Task.CompletedTask);

        Assert.Equal(MutationStatus.Executed, human.Status);
        Assert.Equal(MutationStatus.Denied, agent.Status);
    }

    [Fact]
    public async Task An_owned_task_of_unknown_provenance_keeps_the_agents_rules_until_an_admin_takes_it_over()
    {
        // Created before provenance was recorded (R1-03): it may have come from an agent, so it does not get the
        // rules for a person; taking it over records the admin as its provenance.
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        policy.ToolMode = "allow";
        policy.ToolAllowList = ["list_containers"];
        var f = Build(policy: policy);
        f.Whitelist.Allowed.Add(Ops);
        f.Roles.Map[Ops] = AppRole.Operator;

        var legacy = await f.Runner.RunAsync(Task1 with { CreatedVia = null }, "container.restart",
            new { containerId = "web-1" }, _ => Task.CompletedTask);
        var adopted = await f.Runner.RunAsync(Task1, "container.restart", new { containerId = "web-1" },
            _ => Task.CompletedTask);
        var ownerless = await Build(policy: policy).Runner.RunAsync(new AutomationActor("task:t2", "old", null),
            "container.restart", new { containerId = "web-1" }, _ => Task.CompletedTask);

        Assert.Equal(MutationStatus.Denied, legacy.Status);
        Assert.Equal(MutationStatus.Executed, adopted.Status);
        Assert.Equal(MutationStatus.Executed, ownerless.Status);   // the grace period keeps its own rules
    }

    [Fact]
    public void A_raised_level_and_a_key_tool_list_count_as_lost_authority()
    {
        // The same requirement the gateway enforces: a level raised in Settings, a key limited to other tools.
        var f = WithOperator();
        var raised = new AutomationMutationRunner(new MutationGateway(new CapabilityCatalog(),
                AgentToolTestHelpers.Policy(new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault())),
                new PrincipalAudit(), [], NullLogger<MutationGateway>.Instance),
            new CapabilityCatalog(), new AutomationOwnerResolver(f.Roles, f.Whitelist, f.Keys, f.Accounts),
            new StaticOptionsMonitor<AutomationOptions>(f.Options), new RaisedTo(McpPermissionLevels.Admin));
        Assert.NotNull(raised.LostAuthority(Task1, "container.restart"));
        Assert.Null(f.Runner.LostAuthority(Task1, "container.restart"));

        f.Keys.Data.ApiKeys.Add(new McpApiKeyConfig
        {
            Id = "k1", Name = "ci", PermissionLevel = McpPermissionLevels.Admin, Enabled = true,
            AllowedTools = ["list_containers"],
        });
        var keyTask = new AutomationActor("task:t3", "ci", PrincipalIds.Key("k1"), PrincipalIds.Key("k1"));
        Assert.Contains("may no longer use", f.Runner.LostAuthority(keyTask, "container.restart"));
    }

    private sealed class RaisedTo(string level) : ICapabilityOverrides
    {
        public CapabilityAvailability Resolve(Capability capability) => new(true, level);
        public CapabilityAvailability ResolveTool(string toolName, string codeLevel) => new(true, level);
    }

    [Fact]
    public async Task An_agent_created_task_keeps_the_agents_forbidden_arguments()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        policy.ForbiddenArgPatterns = [@"^web-1$"];
        var f = Build(policy: policy);
        f.Whitelist.Allowed.Add(Ops);
        f.Roles.Map[Ops] = AppRole.Operator;

        var human = await f.Runner.RunAsync(Task1, "container.restart", new { containerId = "web-1" }, _ => Task.CompletedTask);
        var agent = await f.Runner.RunAsync(AgentCreated(), "container.restart", new { containerId = "web-1" },
            _ => Task.CompletedTask);

        Assert.Equal(MutationStatus.Executed, human.Status);
        Assert.Equal(MutationStatus.Denied, agent.Status);
        Assert.Null(f.Runner.LostAuthority(AgentCreated(), "container.restart"));   // a policy refusal: not suspended
    }

    [Fact]
    public async Task A_suspension_that_cannot_be_saved_still_blocks_the_feature()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"owners-{Guid.NewGuid():N}");
        var paths = new DataPathOptions(dir);
        try
        {
            var store = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            await store.SetAsync(IAutomationOwnerStore.AutoUpdate, OpsId);
            Directory.CreateDirectory(paths.AutomationOwnersJson + ".tmp");   // the next write fails

            var saved = await store.SuspendAsync(IAutomationOwnerStore.AutoUpdate, OpsId, "its owner has no authority left");

            Assert.Equal(SuspendResult.NotSaved, saved);
            Assert.True(store.IsBlocked(IAutomationOwnerStore.AutoUpdate));
            Assert.Null(store.Get(IAutomationOwnerStore.AutoUpdate));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_suspended_feature_stays_blocked_across_restarts_until_taken_over()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"owners-{Guid.NewGuid():N}");
        var paths = new DataPathOptions(dir);
        try
        {
            var store = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            await store.SetAsync(IAutomationOwnerStore.AutoUpdate, OpsId);
            Assert.Equal(SuspendResult.Superseded,
                await store.SuspendAsync(IAutomationOwnerStore.AutoUpdate, "user:someone-else@example.com", "stale run"));
            Assert.Equal(SuspendResult.Suspended,
                await store.SuspendAsync(IAutomationOwnerStore.AutoUpdate, OpsId, "its owner has no authority left"));

            var restarted = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            Assert.True(restarted.IsBlocked(IAutomationOwnerStore.AutoUpdate));
            Assert.Null(restarted.Get(IAutomationOwnerStore.AutoUpdate));
            Assert.Equal(OpsId, restarted.GetBlocked(IAutomationOwnerStore.AutoUpdate)?.OwnerId);
            Assert.False(restarted.IsBlocked(IAutomationOwnerStore.AutoPatch));

            await restarted.SetAsync(IAutomationOwnerStore.AutoUpdate, "user:admin@example.com");
            Assert.False(restarted.IsBlocked(IAutomationOwnerStore.AutoUpdate));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_takeover_records_the_old_and_the_new_authority()
    {
        var f = WithOperator();
        f.Whitelist.Allowed.Add("admin@example.com");
        f.Roles.Map["admin@example.com"] = AppRole.Admin;
        f.Accounts.Deleted.Add(Ops);
        var resolver = new AutomationOwnerResolver(f.Roles, f.Whitelist, f.Keys, f.Accounts);

        Assert.Equal($"{OpsId} (AccountDeleted, read)", AutomationOwners.AuthorityOf(resolver, OpsId));
        Assert.Equal("user:admin@example.com (Active, admin)", AutomationOwners.AuthorityOf(resolver, "user:admin@example.com"));
        Assert.Equal("-", AutomationOwners.AuthorityOf(resolver, null));
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

            await store.SetAsync(IAutomationOwnerStore.AutoPatch, "user:admin@example.com");
            Assert.False(store.IsBlocked(IAutomationOwnerStore.AutoPatch));
            Assert.True(store.IsBlocked(IAutomationOwnerStore.AutoUpdate));

            var restarted = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            Assert.Equal("user:admin@example.com", restarted.Get(IAutomationOwnerStore.AutoPatch)?.OwnerId);
            Assert.True(restarted.IsBlocked(IAutomationOwnerStore.AutoUpdate));   // not "no owner": no grace period
            Assert.Null(restarted.Get(IAutomationOwnerStore.AutoUpdate));
            Assert.NotEmpty(Directory.GetFiles(dir, "automation-owners.json.unreadable-*"));

            await restarted.SetAsync(IAutomationOwnerStore.AutoUpdate, "user:admin@example.com");
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
            await first.SetAsync(IAutomationOwnerStore.AutoPatch, "user:admin@example.com");

            var second = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            Assert.Equal("user:admin@example.com", second.Get(IAutomationOwnerStore.AutoPatch)?.OwnerId);
            Assert.Null(second.Get(IAutomationOwnerStore.AutoUpdate));

            Assert.False(second.IsBlocked(IAutomationOwnerStore.AutoUpdate));   // a missing file is no failure

            await File.WriteAllTextAsync(paths.AutomationOwnersJson, "{ not json");
            var third = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);
            Assert.Null(third.Get(IAutomationOwnerStore.AutoPatch));
            Assert.True(third.IsBlocked(IAutomationOwnerStore.AutoUpdate));   // fail closed: not "unowned" (grace period)

            await File.WriteAllTextAsync(paths.AutomationOwnersJson, "{ \"autoupdate\": { \"OwnerId\": \"\" } }");
            Assert.True(new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance)
                .IsBlocked(IAutomationOwnerStore.AutoUpdate));

            await third.SetAsync(IAutomationOwnerStore.AutoUpdate, "user:admin@example.com");
            Assert.False(third.IsBlocked(IAutomationOwnerStore.AutoUpdate));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
