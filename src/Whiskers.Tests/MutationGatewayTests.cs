using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>The gateway pipeline (ADR-0005): order of steps, fail-closed refusals, scope, deadline, audit.</summary>
public class MutationGatewayTests
{
    private static readonly MutationTarget Target = new("container", "c1", "web", "srv1");

    private static MutationRequest Request(string capability = "container.start", string level = McpPermissionLevels.Write,
        string? operationId = null) =>
        new(capability, new MutationPrincipal(MutationPrincipalKind.Human, "ops@example.com", "ops@example.com", level),
            MutationSurface.Ui, Target, new Dictionary<string, JsonElement>
            {
                ["containerId"] = JsonDocument.Parse("\"web\"").RootElement,
            }, operationId);

    private static (MutationGateway Gateway, FakeAudit Audit) Build(
        MutationVerdict verdict = MutationVerdict.Allow, FakeAudit? audit = null, IMutationVerifier? verifier = null,
        Capability? extra = null)
    {
        audit ??= new FakeAudit();
        var catalog = extra is null ? (ICapabilityCatalog)new CapabilityCatalog() : new CatalogWith(extra);
        var gateway = new MutationGateway(catalog, new FixedPolicy(verdict), audit,
            verifier is null ? [] : [verifier], NullLogger<MutationGateway>.Instance);
        return (gateway, audit);
    }

    [Fact]
    public async Task Allowed_mutation_runs_inside_a_scope_and_is_recorded_as_one_event()
    {
        var (gateway, audit) = Build();
        string? scopeCapability = null;

        var outcome = await gateway.ExecuteAsync(Request(operationId: "op-1"), (ctx, _) =>
        {
            scopeCapability = MutationScope.Current?.CapabilityId;
            return Task.FromResult(42);
        });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(42, outcome.Value);
        Assert.Equal("op-1", outcome.OperationId);
        Assert.Equal("container.start", scopeCapability);
        Assert.Null(MutationScope.Current);
        Assert.Equal(VerificationStatus.NotVerifiable, outcome.Verification);
        Assert.Equal(["begin:container.start", "complete:Executed"], audit.Events);
    }

    [Fact]
    public async Task Unknown_capability_is_denied_without_executing()
    {
        var (gateway, audit) = Build();
        var ran = false;

        var outcome = await gateway.ExecuteAsync(Request("container.explode"), (_, _) => { ran = true; return Task.FromResult(0); });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.False(ran);
        Assert.Equal(["denied:Denied"], audit.Events);
    }

    [Fact]
    public async Task Principal_below_the_capability_level_is_denied()
    {
        var (gateway, _) = Build();
        var ran = false;

        var outcome = await gateway.ExecuteAsync(Request("compose.deploy", McpPermissionLevels.Write),
            (_, _) => { ran = true; return Task.FromResult(0); });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("admin", outcome.Reason);
        Assert.False(ran);
    }

    [Theory]
    [InlineData(MutationVerdict.Deny, MutationStatus.Denied)]
    [InlineData(MutationVerdict.Confirm, MutationStatus.PendingApproval)]
    public async Task Policy_deny_or_confirm_never_executes(MutationVerdict verdict, MutationStatus expected)
    {
        var (gateway, audit) = Build(verdict);
        var ran = false;

        var outcome = await gateway.ExecuteAsync(Request(), (_, _) => { ran = true; return Task.FromResult(0); });

        Assert.Equal(expected, outcome.Status);
        Assert.False(ran);
        Assert.Equal([$"denied:{expected}"], audit.Events);
    }

    [Fact]
    public async Task A_throwing_policy_is_a_deny()
    {
        var audit = new FakeAudit();
        var gateway = new MutationGateway(new CapabilityCatalog(), new ThrowingPolicy(), audit, [],
            NullLogger<MutationGateway>.Instance);
        var ran = false;

        var outcome = await gateway.ExecuteAsync(Request(), (_, _) => { ran = true; return Task.FromResult(0); });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.False(ran);
    }

    [Fact]
    public async Task No_audit_intent_means_no_mutation()
    {
        var (gateway, _) = Build(audit: new FakeAudit { FailBegin = true });
        var ran = false;

        var outcome = await gateway.ExecuteAsync(Request(), (_, _) => { ran = true; return Task.FromResult(0); });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.False(ran);
        Assert.Contains("audit", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_outcome_write_does_not_hide_that_the_mutation_ran()
    {
        var (gateway, audit) = Build(audit: new FakeAudit { FailComplete = true });

        var outcome = await gateway.ExecuteAsync(Request(), (_, _) => Task.FromResult(7));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(7, outcome.Value);
        Assert.Equal(["begin:container.start"], audit.Events);
    }

    [Fact]
    public async Task A_throwing_delegate_is_recorded_as_failed()
    {
        var (gateway, audit) = Build();

        var outcome = await gateway.ExecuteAsync<int>(Request(), (_, _) => throw new InvalidOperationException("boom"));

        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.IsType<InvalidOperationException>(outcome.Error);
        Assert.Equal(["begin:container.start", "complete:Failed"], audit.Events);
    }

    [Fact]
    public async Task The_deadline_cancels_a_hanging_delegate()
    {
        var quick = new Capability("test.hang", McpPermissionLevels.Write, "container", CapabilityRisk.Reversible,
            null, TimeSpan.FromMilliseconds(50));
        var (gateway, audit) = Build(extra: quick);

        var outcome = await gateway.ExecuteAsync(Request("test.hang"), async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        });

        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.Contains("deadline", outcome.Reason);
        Assert.Equal(["begin:test.hang", "complete:Failed"], audit.Events);
    }

    [Theory]
    [InlineData(VerificationStatus.Verified, true)]
    [InlineData(VerificationStatus.Failed, false)]
    public async Task The_capability_verifier_decides_the_verification(VerificationStatus result, bool cleanReason)
    {
        var (gateway, _) = Build(verifier: new FixedVerifier("container.start", result));

        var outcome = await gateway.ExecuteAsync(Request(), (_, _) => Task.FromResult(1));

        // R1-36: a call whose effect is not there is no success — Failed, the value withheld; the verification says
        // the call itself ran.
        Assert.Equal(cleanReason ? MutationStatus.Executed : MutationStatus.Failed, outcome.Status);
        Assert.Equal(result, outcome.Verification);
        Assert.Equal(cleanReason, outcome.Reason is null);
        Assert.Equal(cleanReason ? 1 : 0, outcome.Value);
        Assert.Equal(cleanReason, outcome.Error is null);
        if (!cleanReason) Assert.IsType<MutationVerificationFailedException>(outcome.Error);
    }

    [Fact]
    public async Task A_missing_policy_engine_refuses_every_mutation()
    {
        var policy = new GuardrailMutationPolicy(new ServiceCollection().BuildServiceProvider());
        var capability = new CapabilityCatalog().Find("container.start")!;

        var decision = policy.Evaluate(Request(), capability);

        Assert.Equal(MutationVerdict.Deny, decision.Verdict);
    }

    private sealed class FixedPolicy(MutationVerdict verdict) : IMutationPolicy
    {
        public MutationPolicyDecision Evaluate(MutationRequest request, Capability capability) =>
            new(verdict, $"fixed {verdict}", []);
    }

    private sealed class ThrowingPolicy : IMutationPolicy
    {
        public MutationPolicyDecision Evaluate(MutationRequest request, Capability capability) =>
            throw new InvalidOperationException("policy store unreadable");
    }

    private sealed class FixedVerifier(string capabilityId, VerificationStatus result) : IMutationVerifier
    {
        public string CapabilityId => capabilityId;

        public Task<VerificationStatus> VerifyAsync(MutationContext context, object? value, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class CatalogWith(Capability extra) : ICapabilityCatalog
    {
        private readonly CapabilityCatalog _inner = new();
        public IReadOnlyCollection<Capability> All => [.. _inner.All, extra];
        public Capability? Find(string capabilityId) => capabilityId == extra.Id ? extra : _inner.Find(capabilityId);
        public Capability? FindByMcpTool(string toolName) => _inner.FindByMcpTool(toolName);
    }

    private sealed class FakeAudit : IMutationAuditSink
    {
        public bool FailBegin { get; init; }
        public bool FailComplete { get; init; }
        public List<string> Events { get; } = [];

        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct)
        {
            if (FailBegin) throw new InvalidOperationException("database is locked");
            Events.Add($"begin:{capability.Id}");
            return Task.FromResult(1L);
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct)
        {
            if (FailComplete) throw new InvalidOperationException("disk full");
            Events.Add($"complete:{status}");
            return Task.CompletedTask;
        }

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct)
        {
            Events.Add($"denied:{status}");
            return Task.CompletedTask;
        }
    }
}
