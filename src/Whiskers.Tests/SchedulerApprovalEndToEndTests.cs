using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;
using Xunit;

namespace Whiskers.Tests;

/// <summary>
/// R1-05 end to end through the real gateway: <see cref="MutationGateway"/> with the real <see cref="ApprovalGate"/>,
/// <see cref="ApprovalStore"/> and <see cref="SchedulerRequirementResolver"/>. A scheduler call whose task runs as
/// root (custom command), deletes backups (retention) or backs up Whiskers itself raises its approval at Admin; a
/// Write approver can neither decide it nor lend an approval of their own to it, and nothing runs before an
/// adequate approval.
/// </summary>
public class SchedulerApprovalEndToEndTests
{
    private static readonly CapabilityCatalog Catalog = new();

    private static readonly MutationPrincipal Agent = new(MutationPrincipalKind.Agent, "agent:s1", "agent:s1",
        McpPermissionLevels.Admin, SessionId: "s1",
        OnBehalfOf: new MutationPrincipal(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com",
            McpPermissionLevels.Admin));

    private sealed class Services(object scheduler) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ISchedulerService) ? scheduler : null;
    }

    private static readonly ScheduledTaskEntity CommandTask =
        new() { TaskId = "t-cmd", TaskType = ScheduledTaskType.CustomCommand, Config = "{\"command\":\"id\"}" };
    private static readonly ScheduledTaskEntity RetentionTask =
        new() { TaskId = "t-ret", TaskType = ScheduledTaskType.VolumeBackup, Config = "{\"maxBackups\":\"3\"}" };
    private static readonly ScheduledTaskEntity SelfBackupTask =
        new() { TaskId = "t-self", TaskType = ScheduledTaskType.SelfBackup };

    private static (MutationGateway Gateway, ApprovalStore Approvals, AgentToolTestHelpers.NullMutationAudit Audit) Build()
    {
        var approvals = new ApprovalStore();
        var guardrails = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());   // writes need a human
        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var resolver = new SchedulerRequirementResolver(Catalog, new Services(
            ApprovalSeparationOfDutyTests.SchedulerProxy.Create([CommandTask, RetentionTask, SelfBackupTask])));
        var gateway = new MutationGateway(Catalog, AgentToolTestHelpers.Policy(guardrails), audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(approvals, guardrails), requirements: resolver);
        return (gateway, approvals, audit);
    }

    private static Dictionary<string, JsonElement> Args(object args) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!;

    private static MutationRequest Request(string capabilityId, object args, string operation) =>
        new(capabilityId, Agent, MutationSurface.Agent, new MutationTarget("scheduled-task", ""), Args(args),
            operation, "s1");

    public static IEnumerable<object[]> AdminCalls()
    {
        yield return ["scheduler.create", new { name = "n", cronExpression = "0 2 * * *", taskType = "CustomCommand", command = "id" }];
        yield return ["scheduler.run", new { taskId = "t-cmd" }];
        yield return ["scheduler.create", new { name = "n", cronExpression = "0 2 * * *", taskType = "VolumeBackup", targetId = "data", maxBackups = "3" }];
        yield return ["scheduler.run", new { taskId = "t-ret" }];
        yield return ["scheduler.create", new { name = "n", cronExpression = "0 2 * * *", taskType = "5" }];   // SelfBackup by number
        yield return ["scheduler.run", new { taskId = "t-self" }];
    }

    [Theory]
    [MemberData(nameof(AdminCalls))]
    public async Task The_approval_is_raised_at_admin_and_a_write_approver_cannot_decide_it(string capabilityId, object args)
    {
        var (gateway, approvals, _) = Build();
        var runs = 0;
        Task<int> Run(MutationContext _, CancellationToken __) => Task.FromResult(++runs);

        var pending = await gateway.ExecuteAsync(Request(capabilityId, args, "op-1"), Run);

        Assert.Equal(MutationStatus.PendingApproval, pending.Status);
        var approval = approvals.Get(pending.ApprovalId!)!;
        Assert.Equal(McpPermissionLevels.Admin, approval.Level);
        Assert.False(await approvals.ResolveAsync(approval.Id, approved: true, "operator@example.com", McpPermissionLevels.Write));

        var retried = await gateway.ExecuteAsync(Request(capabilityId, args, "op-1"), Run);
        Assert.NotEqual(MutationStatus.Executed, retried.Status);
        Assert.Equal(0, runs);
    }

    [Theory]
    [MemberData(nameof(AdminCalls))]
    public async Task An_admin_approval_runs_it_exactly_once(string capabilityId, object args)
    {
        var (gateway, approvals, audit) = Build();
        var runs = 0;
        Task<int> Run(MutationContext _, CancellationToken __) => Task.FromResult(++runs);

        var pending = await gateway.ExecuteAsync(Request(capabilityId, args, "op-1"), Run);
        Assert.Equal(0, runs);
        Assert.True(await approvals.ResolveAsync(pending.ApprovalId!, approved: true, "admin@example.com", McpPermissionLevels.Admin));

        var executed = await gateway.ExecuteAsync(Request(capabilityId, args, "op-1"), Run);
        var again = await gateway.ExecuteAsync(Request(capabilityId, args, "op-1"), Run);

        Assert.Equal(MutationStatus.Executed, executed.Status);
        Assert.Contains($"begin:{capabilityId}:{pending.ApprovalId}", audit.Events);
        Assert.NotEqual(MutationStatus.Executed, again.Status);
        Assert.Equal(1, runs);
    }

    [Theory]
    [MemberData(nameof(AdminCalls))]
    public async Task A_write_approval_for_the_same_call_is_never_used(string capabilityId, object args)
    {
        // What a coordinator without the effective requirement would have raised: the outer tool's Write level,
        // granted by an Operator for exactly this session, operation and arguments.
        var (gateway, approvals, audit) = Build();
        var tool = Catalog.Find(capabilityId)!.McpTool!;
        var lowered = approvals.Create(new ApprovalRequest("s1", "call-1", "admin@example.com", "agent-web", tool,
            McpPermissionLevels.Write, null, "needs a human", CorrelationId: "op-1",
            ArgumentsHash: ApprovalFingerprint.OfArguments(Args(args))), _ => Task.CompletedTask);
        Assert.True(await approvals.ResolveAsync(lowered.Id, approved: true, "operator@example.com", McpPermissionLevels.Write));
        var runs = 0;

        var outcome = await gateway.ExecuteAsync(Request(capabilityId, args, "op-1"),
            (_, _) => Task.FromResult(++runs));

        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
        Assert.Contains("admin", outcome.Reason);   // refused because the approval is below the requirement
        Assert.DoesNotContain(audit.Events, e => e.StartsWith("begin:", StringComparison.Ordinal));
        Assert.Equal(0, runs);
    }
}
