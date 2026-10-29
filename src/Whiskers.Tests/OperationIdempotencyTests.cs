using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-18 (INV-39): every mutation claims its operation id in the database before it runs. An identical retry, a
/// concurrent retry and a retry after a restart do not run it again — an operation cut off by a restart stays
/// "outcome unknown" instead of being replayed; an id used for different inputs is refused. Decision, approval,
/// execution, verification and audit carry the same id, and a nested operation links to its parent under an id of
/// its own.
/// </summary>
public sealed class OperationIdempotencyTests : IDisposable
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    private readonly ApprovalDatabase _db = new();
    private readonly GuardrailPolicy _policy;
    private readonly AgentToolTestHelpers.FixedGuardrailStore _store;
    private readonly ApprovalStore _approvals;
    private int _runs;

    public OperationIdempotencyTests()
    {
        _policy = GuardrailPolicy.SafeDefault();
        _policy.ProtectedResources = [];
        _policy.ForbiddenArgPatterns = [];
        _policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        _policy.RequireConfirmationForWrites = false;
        _store = new AgentToolTestHelpers.FixedGuardrailStore(_policy);
        _approvals = new ApprovalStore(policy: _store, persistence: _db.Persistence());
    }

    public void Dispose() => _db.Dispose();

    private DbOperationJournal NewJournal() => new(_db.Scopes);

    private MutationGateway Gateway(IOperationJournal journal, params IMutationVerifier[] verifiers) =>
        new(new CapabilityCatalog(), AgentToolTestHelpers.Policy(_store), new AuditLogMutationSink(_db.Scopes), verifiers,
            NullLogger<MutationGateway>.Instance, decisions: new DbPolicyDecisionStore(_db.Scopes),
            approvals: new ApprovalGate(_approvals, _store), journal: journal);

    private static MutationRequest Restart(string? operationId, string container = "web") =>
        new("container.restart", Admin, MutationSurface.Ui, new MutationTarget("container", container, container, "local"),
            Args(new { containerId = container, serverId = "local" }), OperationId: operationId);

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private Task<MutationOutcome<int>> Run(MutationGateway gateway, MutationRequest request) =>
        gateway.ExecuteAsync(request, (_, _) => Task.FromResult(Interlocked.Increment(ref _runs)));

    private async Task<OperationRecordEntity?> Row(string id)
    {
        using var scope = _db.Scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Operations.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == id);
    }

    // ---------------------------------------------------------------- retries

    [Fact]
    public async Task An_identical_retry_does_not_run_again()
    {
        var gateway = Gateway(NewJournal());
        var first = await Run(gateway, Restart("op-1"));
        var retry = await Run(gateway, Restart("op-1"));

        Assert.Equal(MutationStatus.Executed, first.Status);
        Assert.Equal(MutationStatus.Denied, retry.Status);
        Assert.Equal(MutationReasonCodes.OperationAlreadyRun, retry.ReasonCode);
        Assert.Equal(1, _runs);
        Assert.Equal("Executed", (await Row("op-1"))!.State);
    }

    [Fact]
    public async Task A_failed_operation_is_not_retried_under_the_same_id()
    {
        var gateway = Gateway(NewJournal());
        var failed = await gateway.ExecuteAsync<int>(Restart("op-f"), (_, _) => throw new InvalidOperationException("boom"));
        var retry = await Run(gateway, Restart("op-f"));

        Assert.Equal(MutationStatus.Failed, failed.Status);
        Assert.Equal(MutationReasonCodes.OperationAlreadyRun, retry.ReasonCode);
        Assert.Equal(0, _runs);
        Assert.Equal("Failed", (await Row("op-f"))!.State);
    }

    [Fact]
    public async Task Concurrent_retries_run_once()
    {
        var gateway = Gateway(NewJournal());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(() => gateway.ExecuteAsync(Restart("op-c"), async (_, _) =>
        {
            Interlocked.Increment(ref _runs);
            await release.Task;
            return 0;
        }))).ToList();

        // Let every attempt reach the claim, then let the one that runs finish.
        await Task.Delay(500);
        release.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, _runs);
        Assert.Single(outcomes, o => o.Status == MutationStatus.Executed);
        Assert.All(outcomes.Where(o => o.Status != MutationStatus.Executed), o =>
            Assert.Contains(o.ReasonCode, new[] { MutationReasonCodes.OperationRunning, MutationReasonCodes.OperationAlreadyRun }));
    }

    [Fact]
    public async Task An_id_used_for_other_inputs_is_refused()
    {
        var gateway = Gateway(NewJournal());
        await Run(gateway, Restart("op-m", "web"));
        var other = await Run(gateway, Restart("op-m", "billing"));

        Assert.Equal(MutationStatus.Denied, other.Status);
        Assert.Equal(MutationReasonCodes.OperationIdReused, other.ReasonCode);
        Assert.Equal(1, _runs);
    }

    [Fact]
    public async Task An_operation_cut_off_by_a_restart_stays_unknown_and_is_not_replayed()
    {
        // The process that claimed it stopped before it finished (the row is still Running).
        var before = NewJournal();
        Assert.Equal(OperationClaimResult.Claimed, (await before.ClaimAsync(new OperationClaimRequest("op-r", null,
            "container.restart", Admin.Id, DbOperationJournal.InputHashOf(Restart("op-r")), null, null, null))).Result);

        var afterRestart = Gateway(NewJournal());
        var retry = await Run(afterRestart, Restart("op-r"));

        Assert.Equal(MutationStatus.Denied, retry.Status);
        Assert.Equal(MutationReasonCodes.OperationOutcomeUnknown, retry.ReasonCode);
        Assert.Equal(0, _runs);
        Assert.Equal("Running", (await Row("op-r"))!.State);   // explicit, not rewritten
    }

    [Fact]
    public async Task A_new_id_after_an_unknown_outcome_runs()
    {
        var before = NewJournal();
        await before.ClaimAsync(new OperationClaimRequest("op-u", null, "container.restart", Admin.Id,
            DbOperationJournal.InputHashOf(Restart("op-u")), null, null, null));

        var outcome = await Run(Gateway(NewJournal()), Restart(null));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.NotEqual("op-u", outcome.OperationId);
    }

    [Fact]
    public async Task A_claim_given_back_before_it_ran_can_be_taken_again_once()
    {
        var journal = NewJournal();
        var claim = new OperationClaimRequest("op-x", null, "container.restart", Admin.Id, "hash", null, null, null);
        var first = await journal.ClaimAsync(claim);
        Assert.Equal(OperationClaimResult.Claimed, first.Result);
        await journal.ReleaseAsync("op-x", first.Token!);

        var again = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => journal.ClaimAsync(claim)));

        Assert.Single(again, r => r.Result == OperationClaimResult.Claimed);
    }

    [Fact]
    public async Task A_retried_release_never_frees_a_later_claim()
    {
        // A's release committed but its answer was lost; B took the id; A's release runs again (a provider retry).
        var journal = NewJournal();
        var claim = new OperationClaimRequest("op-t", null, "container.restart", Admin.Id, "hash", null, null, null);
        var a = await journal.ClaimAsync(claim);
        await journal.ReleaseAsync("op-t", a.Token!);
        var b = await journal.ClaimAsync(claim);
        Assert.Equal(OperationClaimResult.Claimed, b.Result);

        await journal.ReleaseAsync("op-t", a.Token!);
        await journal.FinishAsync("op-t", a.Token!, MutationStatus.Executed, VerificationStatus.Verified);

        // B still holds it: nobody else can claim it, and only B finishes it.
        Assert.Equal(OperationClaimResult.AlreadyRunning, (await journal.ClaimAsync(claim)).Result);
        Assert.Equal("Running", (await Row("op-t"))!.State);
        await journal.FinishAsync("op-t", b.Token!, MutationStatus.Executed, VerificationStatus.Verified);
        Assert.Equal("Executed", (await Row("op-t"))!.State);
        Assert.Equal(OperationClaimResult.AlreadyRun, (await journal.ClaimAsync(claim)).Result);
    }

    [Fact]
    public async Task A_call_waiting_for_its_approval_keeps_its_id_for_the_approved_retry()
    {
        // Policy asks for a confirmation: nothing is claimed while it waits; the approved retry runs once.
        _policy.RequireConfirmationForWrites = true;
        var gateway = Gateway(NewJournal());
        var agent = Restart("op-a") with
        {
            Principal = MutationPrincipals.Agent(new Whiskers.Models.Agent.AgentPrincipal(
                Whiskers.Models.Agent.AgentPrincipalKind.WebUser, "admin", McpPermissionLevels.Admin, null,
                UserEmail: "admin@example.com"), "chat-1"),
            Surface = MutationSurface.Agent,
            SessionId = "chat-1",
            AgentOrigin = Whiskers.Models.Agent.AgentOrigin.WebUi,
        };

        var asked = await Run(gateway, agent);
        Assert.Equal(MutationStatus.PendingApproval, asked.Status);
        Assert.Null(await Row("op-a"));

        Assert.Equal(ApprovalDecisionResult.Decided, await _approvals.DecideAsync(asked.ApprovalId!, true,
            ApprovalResolver.User("other-admin@example.com", AppRole.Admin)));
        var ran = await Run(gateway, agent);
        Assert.Equal(MutationStatus.Executed, ran.Status);

        // The same id once more: refused as an operation that ran — not sent back to the approval gate.
        var again = await Run(gateway, agent);
        Assert.Equal(MutationStatus.Denied, again.Status);
        Assert.Equal(MutationReasonCodes.OperationAlreadyRun, again.ReasonCode);
        Assert.Null(again.ApprovalId);
        Assert.Equal(1, _runs);
        Assert.Equal(asked.ApprovalId, (await Row("op-a"))!.ApprovalId);
    }

    [Fact]
    public async Task An_interrupted_operation_needing_approval_is_reported_unknown_not_sent_for_approval()
    {
        _policy.RequireConfirmationForWrites = true;
        var request = Restart("op-ia") with { Surface = MutationSurface.Mcp };
        await NewJournal().ClaimAsync(new OperationClaimRequest("op-ia", null, "container.restart", Admin.Id,
            DbOperationJournal.InputHashOf(request), null, null, null));

        var retry = await Run(Gateway(NewJournal()), request);

        Assert.Equal(MutationStatus.Denied, retry.Status);
        Assert.Equal(MutationReasonCodes.OperationOutcomeUnknown, retry.ReasonCode);
        Assert.Empty(_approvals.GetAll());
        Assert.Equal(0, _runs);
    }

    [Fact]
    public async Task Approve_then_retry_without_an_id_of_its_own_continues_one_operation()
    {
        // A direct MCP call names no operation id: the retry after the approval continues the operation that raised
        // it — decision, approval, run, verification and audit under one id.
        _policy.RequireConfirmationForWrites = true;
        var verifier = new RecordingVerifier();
        var gateway = Gateway(NewJournal(), verifier);
        var call = Restart(null) with { Surface = MutationSurface.Mcp };

        var asked = await Run(gateway, call);
        Assert.Equal(MutationStatus.PendingApproval, asked.Status);
        var id = asked.OperationId;
        Assert.Equal(id, _approvals.GetAll().Single(a => a.Id == asked.ApprovalId).CorrelationId);

        // Still pending: the same id again, nothing claimed.
        Assert.Equal(id, (await Run(gateway, call)).OperationId);
        Assert.Null(await Row(id));

        Assert.Equal(ApprovalDecisionResult.Decided, await _approvals.DecideAsync(asked.ApprovalId!, true,
            ApprovalResolver.User("other-admin@example.com", AppRole.Admin)));
        var ran = await Run(gateway, call);

        Assert.Equal(MutationStatus.Executed, ran.Status);
        Assert.Equal(id, ran.OperationId);
        Assert.Equal(id, verifier.OperationId);
        var row = (await Row(id))!;
        Assert.Equal(asked.ApprovalId, row.ApprovalId);
        using (var scope = _db.Scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            Assert.Equal(id, (await db.PolicyDecisions.AsNoTracking().SingleAsync(d => d.Id == ran.DecisionId)).OperationId);
            Assert.Equal(id, (await db.PolicyDecisions.AsNoTracking().SingleAsync(d => d.Id == asked.DecisionId)).OperationId);
            Assert.Contains($"\"operationId\":\"{id}\"", (await db.AuditLog.AsNoTracking().SingleAsync(a => a.Id == row.AuditId)).Details);
        }

        // The approval is used: the next identical call is a new operation that asks again.
        var next = await Run(gateway, call);
        Assert.Equal(MutationStatus.PendingApproval, next.Status);
        Assert.NotEqual(id, next.OperationId);
        Assert.Equal(1, _runs);
    }

    [Fact]
    public async Task Concurrent_first_calls_without_an_id_share_one_operation_and_one_approval()
    {
        _policy.RequireConfirmationForWrites = true;
        var gateway = Gateway(NewJournal());
        var call = Restart(null) with { Surface = MutationSurface.Mcp };

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Run(gateway, call))));

        Assert.All(outcomes, o => Assert.Equal(MutationStatus.PendingApproval, o.Status));
        var id = Assert.Single(outcomes.Select(o => o.OperationId).Distinct());
        var approvalId = Assert.Single(outcomes.Select(o => o.ApprovalId).Distinct());
        Assert.Equal(id, _approvals.GetAll().Single(a => a.Id == approvalId).CorrelationId);
        using var scope = _db.Scopes.CreateScope();
        var decisions = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().PolicyDecisions.AsNoTracking()
            .Select(d => d.OperationId).ToListAsync();
        Assert.All(decisions, d => Assert.Equal(id, d));
    }

    [Theory]
    [InlineData("running", MutationReasonCodes.OperationRunning)]
    [InlineData("executed", MutationReasonCodes.OperationAlreadyRun)]
    [InlineData("interrupted", MutationReasonCodes.OperationOutcomeUnknown)]
    [InlineData("other-inputs", MutationReasonCodes.OperationIdReused)]
    public async Task The_decision_for_a_known_operation_is_a_refusal_not_a_confirmation(string state, string code)
    {
        // The agent asks for the decision before it pauses for an approval: a known id must not get that far.
        _policy.RequireConfirmationForWrites = true;
        var journal = NewJournal();
        var request = Restart("op-d") with { Surface = MutationSurface.Mcp };
        var hash = DbOperationJournal.InputHashOf(request);
        var claim = new OperationClaimRequest("op-d", null, "container.restart", Admin.Id, hash, null, null, null);
        switch (state)
        {
            case "running":
                await journal.ClaimAsync(claim);
                break;
            case "executed":
                var token = (await journal.ClaimAsync(claim)).Token!;
                await journal.FinishAsync("op-d", token, MutationStatus.Executed, VerificationStatus.Verified);
                break;
            case "interrupted":
                await NewJournal().ClaimAsync(claim);
                break;
            default:
                await journal.ClaimAsync(claim with { InputHash = "other" });
                break;
        }

        var decided = await Gateway(journal).DecideAsync(request);

        Assert.Equal(MutationVerdict.Deny, decided!.Verdict);
        Assert.Equal(code, decided.ReasonCode);
        Assert.Empty(_approvals.GetAll());
    }

    [Fact]
    public async Task A_journal_that_cannot_be_written_refuses_the_call()
    {
        var gateway = Gateway(new BrokenJournal());
        var outcome = await Run(gateway, Restart("op-b"));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.OperationNotRegistered, outcome.ReasonCode);
        Assert.Equal(0, _runs);
    }

    // ---------------------------------------------------------------- one id across every step

    [Fact]
    public async Task Decision_approval_execution_verification_and_audit_share_the_id()
    {
        var verifier = new RecordingVerifier();
        var gateway = Gateway(NewJournal(), verifier);
        string? executedAs = null, scopeId = null;

        var outcome = await gateway.ExecuteAsync(Restart("op-l"), (ctx, _) =>
        {
            executedAs = ctx.OperationId;
            scopeId = MutationScope.Current?.OperationId;
            return Task.FromResult(1);
        });

        Assert.Equal("op-l", outcome.OperationId);
        Assert.Equal("op-l", executedAs);
        Assert.Equal("op-l", scopeId);
        Assert.Equal("op-l", verifier.OperationId);
        var row = (await Row("op-l"))!;
        Assert.Equal(outcome.DecisionId, row.DecisionId);
        Assert.Equal("Verified", row.Verification);
        using var scope = _db.Scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        Assert.Equal("op-l", (await db.PolicyDecisions.AsNoTracking().SingleAsync(d => d.Id == outcome.DecisionId)).OperationId);
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(a => a.Id == row.AuditId);
        Assert.Contains("\"operationId\":\"op-l\"", audit.Details);
    }

    [Fact]
    public async Task A_nested_operation_links_to_its_parent_under_an_id_of_its_own()
    {
        var gateway = Gateway(NewJournal());
        MutationOutcome<int>? child = null, reused = null;

        await gateway.ExecuteAsync(Restart("op-parent"), async (_, _) =>
        {
            child = await Run(gateway, Restart(null, "billing"));
            reused = await Run(gateway, Restart("op-parent"));   // the parent's own id, from inside it
            return 0;
        });

        Assert.Equal(MutationStatus.Executed, child!.Status);
        Assert.NotEqual("op-parent", child.OperationId);
        var row = (await Row(child.OperationId))!;
        Assert.Equal("op-parent", row.ParentId);
        Assert.Equal(MutationStatus.Denied, reused!.Status);
        Assert.Equal(MutationReasonCodes.OperationRunning, reused.ReasonCode);

        using var scope = _db.Scopes.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .SingleAsync(a => a.Id == row.AuditId);
        Assert.Contains("\"parentOperationId\":\"op-parent\"", audit.Details);
    }

    private sealed class RecordingVerifier : IMutationVerifier
    {
        public string CapabilityId => "container.restart";
        public string? OperationId { get; private set; }

        public Task<VerificationStatus> VerifyAsync(MutationContext context, object? result, CancellationToken ct)
        {
            OperationId = context.OperationId;
            return Task.FromResult(VerificationStatus.Verified);
        }
    }

    private sealed class BrokenJournal : IOperationJournal
    {
        public Task<OperationClaim> ClaimAsync(OperationClaimRequest claim, CancellationToken ct = default) =>
            throw new IOException("database unavailable");
        public Task<OperationClaimResult?> InspectAsync(string operationId, string inputHash, CancellationToken ct = default) =>
            throw new IOException("database unavailable");
        public Task FinishAsync(string operationId, string token, MutationStatus status, VerificationStatus verification,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task ReleaseAsync(string operationId, string token, CancellationToken ct = default) => Task.CompletedTask;
    }
}
