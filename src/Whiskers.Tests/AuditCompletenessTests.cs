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
/// R1-19 (INV-45): every mutation attempt — allowed, denied, waiting for approval, failed — is exactly one audit event
/// carrying every security field as a column of its own: principal, surface, capability, resolved target, redacted
/// arguments, decision id, approval id and approver, outcome, verification, duration, operation id. None of them can
/// be lost by the capped details being shortened.
/// </summary>
public sealed class AuditCompletenessTests : IDisposable
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    private readonly ApprovalDatabase _db = new();
    private readonly GuardrailPolicy _policy;
    private readonly AgentToolTestHelpers.FixedGuardrailStore _store;
    private readonly ApprovalStore _approvals;

    public AuditCompletenessTests()
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

    private MutationGateway Gateway() =>
        new(new CapabilityCatalog(), AgentToolTestHelpers.Policy(_store), new AuditLogMutationSink(_db.Scopes), [],
            NullLogger<MutationGateway>.Instance, decisions: new DbPolicyDecisionStore(_db.Scopes),
            approvals: new ApprovalGate(_approvals, _store), journal: new DbOperationJournal(_db.Scopes));

    private static MutationRequest Restart(string operationId, string container = "web", MutationPrincipal? principal = null) =>
        new("container.restart", principal ?? Admin, MutationSurface.Ui,
            new MutationTarget("container", container, container, "local"),
            Args(new { containerId = container, serverId = "local" }), OperationId: operationId);

    private static MutationRequest AgentRestart(string operationId) => Restart(operationId) with
    {
        Principal = MutationPrincipals.Agent(new Whiskers.Models.Agent.AgentPrincipal(
            Whiskers.Models.Agent.AgentPrincipalKind.WebUser, "admin", McpPermissionLevels.Admin, null,
            UserEmail: "admin@example.com"), "chat-1"),
        Surface = MutationSurface.Agent,
        SessionId = "chat-1",
        AgentOrigin = Whiskers.Models.Agent.AgentOrigin.WebUi,
    };

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private async Task<AuditLogEntity> EventOf(string operationId)
    {
        using var scope = _db.Scopes.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .Where(a => a.OperationId == operationId).ToListAsync();
        // Exactly one event per attempt.
        return Assert.Single(rows);
    }

    /// <summary>The fields every event has, whatever its outcome.</summary>
    private static void AssertCommon(AuditLogEntity row, string operationId, MutationPrincipal principal, string surface)
    {
        Assert.Equal(operationId, row.OperationId);
        Assert.Equal("outcome", row.Phase);
        Assert.Equal("container.restart", row.Action);
        Assert.Equal(surface, row.Surface);
        Assert.Equal(principal.Kind.ToString(), row.PrincipalKind);
        Assert.Equal(principal.Id, row.PrincipalId);
        Assert.Equal(principal.CredentialId, row.CredentialId);
        Assert.Equal(principal.OnBehalfOf?.Id, row.OnBehalfOfId);
        Assert.NotNull(row.EventId);
        Assert.Equal("container", row.TargetType);
        Assert.Equal("web", row.TargetId);
        Assert.Equal("local", row.ServerId);
        Assert.NotNull(row.Verification);
        Assert.NotNull(row.DurationMs);
        // The redacted arguments, complete, in a column of their own; the details stay valid JSON.
        using var arguments = JsonDocument.Parse(row.Arguments!);
        Assert.Equal("web", arguments.RootElement.GetProperty("containerId").GetString());
        Assert.Equal("local", arguments.RootElement.GetProperty("serverId").GetString());
        using var details = JsonDocument.Parse(row.Details!);
    }

    [Fact]
    public async Task An_allowed_mutation_is_one_complete_event()
    {
        var outcome = await Gateway().ExecuteAsync(Restart("op-ok"), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        var row = await EventOf("op-ok");
        AssertCommon(row, "op-ok", Admin, "Ui");
        Assert.Equal("Executed", row.Outcome);
        Assert.Equal(outcome.DecisionId, row.DecisionId);
        Assert.NotNull(row.DecisionId);
        Assert.True(row.Success);
    }

    [Fact]
    public async Task A_denied_mutation_is_one_complete_event()
    {
        // An agent's call with a forbidden argument: the policy refuses it.
        _policy.ForbiddenArgPatterns = [@"^web$"];
        var request = AgentRestart("op-deny");

        var outcome = await Gateway().ExecuteAsync(request, (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        var row = await EventOf("op-deny");
        AssertCommon(row, "op-deny", request.Principal, "Agent");
        Assert.Equal("Denied", row.Outcome);
        Assert.Equal("NotRun", row.Verification);
        Assert.NotNull(row.DecisionId);
        Assert.False(row.Success);
    }

    [Fact]
    public async Task A_mutation_waiting_for_approval_is_one_complete_event_naming_its_approval()
    {
        _policy.RequireConfirmationForWrites = true;
        var request = AgentRestart("op-wait");

        var outcome = await Gateway().ExecuteAsync(request, (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        var row = await EventOf("op-wait");
        AssertCommon(row, "op-wait", request.Principal, "Agent");
        Assert.Equal("PendingApproval", row.Outcome);
        Assert.Equal(outcome.ApprovalId, row.ApprovalId);
        Assert.NotNull(row.ApprovalId);
        Assert.Null(row.ApprovedBy);
        Assert.NotNull(row.DecisionId);
    }

    [Fact]
    public async Task An_approved_mutation_names_its_approval_and_approver()
    {
        _policy.RequireConfirmationForWrites = true;
        var gateway = Gateway();
        var asked = await gateway.ExecuteAsync(AgentRestart("op-appr"), (_, _) => Task.FromResult(1));
        await _approvals.DecideAsync(asked.ApprovalId!, true, ApprovalResolver.User("other-admin@example.com", AppRole.Admin));

        var ran = await gateway.ExecuteAsync(AgentRestart("op-appr"), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Executed, ran.Status);
        // Two attempts, two events: the one that waited and the one that ran.
        using var scope = _db.Scopes.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .Where(a => a.OperationId == "op-appr").ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.Outcome == "PendingApproval");
        var row = Assert.Single(rows, r => r.Outcome == "Executed");
        Assert.Equal(asked.ApprovalId, row.ApprovalId);
        Assert.Equal("other-admin@example.com", row.ApprovedBy);
        Assert.Equal("Executed", row.Outcome);
    }

    [Fact]
    public async Task A_failed_mutation_is_one_complete_event()
    {
        var outcome = await Gateway().ExecuteAsync<int>(Restart("op-fail"),
            (_, _) => throw new InvalidOperationException("boom"));

        Assert.Equal(MutationStatus.Failed, outcome.Status);
        var row = await EventOf("op-fail");
        AssertCommon(row, "op-fail", Admin, "Ui");
        Assert.Equal("Failed", row.Outcome);
        Assert.NotNull(row.DecisionId);
        Assert.False(row.Success);
    }

    [Fact]
    public async Task Required_fields_survive_when_the_details_are_shortened()
    {
        // A long argument before the others and a long failure text — plain words, which no redaction rule shortens:
        // the details are cut to their cap, the security fields and the arguments are not.
        var principal = Admin with { CredentialId = "key-reporting" };
        var note = string.Concat(Enumerable.Repeat("please restart the web container after the deploy ", 400));
        var request = Restart("op-big", principal: principal) with
        {
            Arguments = Args(new { aNote = note, containerId = "web", serverId = "local", command = "secret-cmd --password hunter2" }),
        };

        var outcome = await Gateway().ExecuteAsync<int>(request,
            (_, _) => throw new InvalidOperationException(string.Concat(Enumerable.Repeat("the daemon did not answer ", 2000))));

        var row = await EventOf("op-big");
        Assert.True(row.Details!.Length <= 4000);
        using var details = JsonDocument.Parse(row.Details);
        Assert.DoesNotContain("containerId", row.Details[^200..]);   // the details really were shortened
        Assert.Equal("Failed", row.Outcome);
        Assert.Equal(outcome.DecisionId, row.DecisionId);
        Assert.NotNull(row.DecisionId);
        Assert.Equal(principal.Id, row.PrincipalId);
        Assert.Equal("key-reporting", row.CredentialId);
        Assert.Equal("Ui", row.Surface);
        Assert.Equal("NotRun", row.Verification);
        Assert.NotNull(row.DurationMs);
        using var arguments = JsonDocument.Parse(row.Arguments!);
        Assert.Equal(note, arguments.RootElement.GetProperty("aNote").GetString());
        Assert.Equal("web", arguments.RootElement.GetProperty("containerId").GetString());
        Assert.Equal("local", arguments.RootElement.GetProperty("serverId").GetString());
        // A payload keeps only its length, in the column as in the details.
        Assert.DoesNotContain("hunter2", row.Arguments);
        Assert.DoesNotContain("hunter2", row.Details);
    }
}
