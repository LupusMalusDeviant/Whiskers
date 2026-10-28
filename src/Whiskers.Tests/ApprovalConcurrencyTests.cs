using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>R1-14 (INV-35): an approval runs exactly one execution — across concurrent consumers in one process, and
/// across a restart, where a second store on the same database competes with the first.</summary>
public sealed class ApprovalConcurrencyTests : IDisposable
{
    private readonly ApprovalDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly MutationPrincipal Key = new(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Write,
        CredentialId: "k1");

    private static MutationRequest Call() =>
        new("container.restart", Key, MutationSurface.Mcp, new MutationTarget("container", "web-1", null, "srv1"),
            new Dictionary<string, JsonElement> { ["containerId"] = JsonSerializer.SerializeToElement("web-1") });

    private static MutationGateway Gateway(IApprovalStore approvals)
    {
        var store = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        return new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            approvals: new ApprovalGate(approvals, store));
    }

    [Fact]
    public async Task Concurrent_retries_of_an_approved_call_execute_once()
    {
        var approvals = await _db.StartAsync();
        var gateway = Gateway(approvals);
        var raised = await gateway.ExecuteAsync(Call(), (_, _) => Task.FromResult(0));
        await approvals.ResolveAsync(raised.ApprovalId!, true, "admin@example.com", McpPermissionLevels.Admin);
        var runs = 0;

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            gateway.ExecuteAsync(Call(), (_, _) => Task.FromResult(Interlocked.Increment(ref runs))))));

        Assert.Equal(1, runs);
        Assert.Single(outcomes, o => o.Status == MutationStatus.Executed);
        Assert.Equal(raised.ApprovalId, outcomes.Single(o => o.Status == MutationStatus.Executed).ApprovalId);
    }

    [Fact]
    public async Task Two_stores_on_one_database_consume_an_approval_once()
    {
        // The store before a restart and the one after it both hold the approved approval in memory; only the
        // database decides, and it lets exactly one use through.
        var first = await _db.StartAsync();
        var approval = await first.CreateAsync(new ApprovalRequest("s1", "call-1", "ci", "mcp-direct", "restart_container",
            McpPermissionLevels.Write, null, "needs a human", Binding: "b"), _ => Task.CompletedTask);
        await first.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin);
        var second = await _db.StartAsync();
        Assert.NotNull(second.Get(approval.Id));

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            (i % 2 == 0 ? first : second).TryConsumeAsync(approval.Id, $"op-{i}", DateTime.UtcNow))));

        Assert.Single(results, r => r);
        var row = await _db.RowAsync(approval.Id);
        Assert.NotNull(row!.ConsumedAt);
        Assert.StartsWith("op-", row.ConsumedBy);
    }

    [Fact]
    public async Task An_approval_used_before_a_restart_runs_nothing_after_it()
    {
        var before = await _db.StartAsync();
        var gateway = Gateway(before);
        var raised = await gateway.ExecuteAsync(Call(), (_, _) => Task.FromResult(0));
        await before.ResolveAsync(raised.ApprovalId!, true, "admin@example.com", McpPermissionLevels.Admin);
        // A store started while the approval was still unused (it holds it in memory as approved) …
        var stale = await _db.StartAsync();
        Assert.Equal(MutationStatus.Executed, (await gateway.ExecuteAsync(Call(), (_, _) => Task.FromResult(1))).Status);
        var runs = 0;

        // … cannot use it again: the use is on record.
        var outcome = await Gateway(stale).ExecuteAsync(Call(), (_, _) => Task.FromResult(++runs));

        Assert.NotEqual(MutationStatus.Executed, outcome.Status);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task Concurrent_decisions_of_one_approval_record_one_outcome()
    {
        var store = await _db.StartAsync();
        var approval = await store.CreateAsync(new ApprovalRequest("s1", "call-1", "ci", "mcp-direct", "restart_container",
            McpPermissionLevels.Write, null, "needs a human", Binding: "b"), _ => Task.CompletedTask);
        var other = await _db.StartAsync();

        var decisions = await Task.WhenAll(
            Task.Run(() => store.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin)),
            Task.Run(() => other.ResolveAsync(approval.Id, false, "other@example.com", McpPermissionLevels.Admin)));

        Assert.Single(decisions, d => d);
        var row = await _db.RowAsync(approval.Id);
        Assert.Contains(row!.Status, new[] { nameof(ApprovalStatus.Approved), nameof(ApprovalStatus.Rejected) });
    }
}
