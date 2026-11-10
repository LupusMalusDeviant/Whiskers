using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// INV-45: marks a test that drives a path of the <c>PRODUCT_CONTRACT.md</c> §7 matrix and proves that each attempt
/// on it, with the outcomes named here, is exactly one complete <c>AuditLog</c> event. <see cref="Row"/> is a row id
/// (<c>S-01</c>), or a class with <c>-*</c> (<c>U-*</c>) for a test whose cases are every capability of every row of
/// that class. <see cref="AuditPathMatrixTests"/> fails a §7 row that no marked test proves.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AuditPathAttribute(string row, params string[] outcomes) : Attribute
{
    public string Row { get; } = row;
    public IReadOnlyList<string> Outcomes { get; } = outcomes;
}

/// <summary>The real audit trail of a test: the gateway writes into a database, and the rows an attempt added are read
/// back and checked for every field INV-45 names.</summary>
internal static class AuditTrail
{
    public const string Executed = "Executed";
    public const string Denied = "Denied";
    public const string Pending = "PendingApproval";
    public const string Failed = "Failed";
    /// <summary>Approve-then-execute: the run after the approval, one event of its own naming the approval and approver.</summary>
    public const string Approved = "Approved";

    public static readonly IReadOnlySet<string> Outcomes =
        new HashSet<string>([Executed, Denied, Pending, Failed, Approved], StringComparer.Ordinal);

    /// <summary>The production gateway writing to the database behind <paramref name="scopes"/>: audit sink, policy
    /// decision record and operation journal.</summary>
    public static MutationGateway Gateway(IServiceScopeFactory scopes, IGuardrailStore store, IApprovalStore? approvals = null,
        IMutationTargetResolver? targets = null, IEnumerable<IMutationVerifier>? verifiers = null,
        ITriggerAuthority? triggers = null) =>
        new(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), new AuditLogMutationSink(scopes), verifiers ?? [],
            NullLogger<MutationGateway>.Instance, targets: targets ?? TestTargets.Resolver(),
            approvals: new ApprovalGate(approvals ?? new ApprovalStore(), store),
            decisions: new DbPolicyDecisionStore(scopes), triggers: triggers, journal: new DbOperationJournal(scopes));

    public static async Task<List<AuditLogEntity>> RowsAsync(IServiceScopeFactory scopes)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .OrderBy(a => a.Id).ToListAsync();
    }

    /// <summary>The rows <paramref name="attempt"/> added — whatever it throws, it is one attempt. For an attempt
    /// that finishes in the call (every path but the webhook worker's): the gateway writes the outcome before the
    /// attempt returns, so nothing of it can arrive later and be counted in the next one. An attempt a background
    /// worker finishes is read by its operation id (<see cref="ForOperationAsync"/>).</summary>
    public static async Task<List<AuditLogEntity>> AttemptAsync(IServiceScopeFactory scopes, Func<Task> attempt)
    {
        var before = (await RowsAsync(scopes)).Select(r => r.Id).ToHashSet();
        try
        {
            await attempt();
        }
        catch (MutationRefusedException) { /* a refusal is an outcome */ }
        catch (MutationFailedException) { /* so is a failure */ }
        return (await RowsAsync(scopes)).Where(r => !before.Contains(r.Id)).ToList();
    }

    /// <summary>The events of one operation and of the operations nested in it, once its own outcome is written —
    /// read by its id, not by what else was written meanwhile, for an attempt a background worker finishes (a webhook
    /// delivery). Waits at most <paramref name="timeout"/> (10 s) for the outcome.</summary>
    public static async Task<List<AuditLogEntity>> ForOperationAsync(IServiceScopeFactory scopes, string operationId,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var all = await RowsAsync(scopes);
            var ids = new HashSet<string>(StringComparer.Ordinal) { operationId };
            // The operation and everything nested in it, at any depth.
            while (all.Where(r => r.ParentOperationId is { } parent && ids.Contains(parent) && r.OperationId is not null)
                       .Select(r => r.OperationId!).Where(ids.Add).ToList() is { Count: > 0 }) { }
            var rows = all.Where(r => r.OperationId is { } id && ids.Contains(id)).ToList();
            if (rows.Any(r => r.OperationId == operationId && r.Phase == "outcome") || DateTime.UtcNow > deadline)
                return rows;
            await Task.Delay(20);
        }
    }

    /// <summary>A marker no audit field may ever hold: the tests put it into payload arguments (a password, a token,
    /// a command line, a container environment) and every event is checked for it (the "redacted arguments" of
    /// INV-45, INV-51).</summary>
    public const string Probe = "inv45-redaction-probe";

    /// <summary>Exactly one event for the attempt, complete for <paramref name="outcome"/>.</summary>
    /// <param name="decided">Whether the attempt reached the policy step: then the event names its decision. A refusal
    /// before it — the role below the capability, no owner, a page's own check, a run refused before it asked the
    /// gateway — has none, by design, and must have none.</param>
    /// <param name="targetId">The resolved target the event must name; by default any non-empty one.</param>
    public static AuditLogEntity One(IReadOnlyList<AuditLogEntity> rows, string outcome, string capability, string surface,
        bool owned = true, bool decided = true, string? targetId = null)
    {
        var row = Assert.Single(rows);
        AssertComplete(row, outcome, capability, surface, owned, decided, targetId);
        return row;
    }

    /// <summary>
    /// Every field INV-45 names, as columns of the event: principal (kind, id; for an agent or an owned automation the
    /// principal it acts for), surface, capability, resolved target (kind and id), the redacted arguments (no
    /// <see cref="Probe"/> anywhere in the event), the decision id (set exactly when the policy step was reached:
    /// <paramref name="decided"/>), the approval and approver, outcome, verification, duration and operation id.
    /// </summary>
    public static void AssertComplete(AuditLogEntity row, string outcome, string capability, string surface, bool owned = true,
        bool decided = true, string? targetId = null)
    {
        var context = $"{capability} ({outcome}): {row.Details}";
        Assert.True(row.Phase == "outcome", $"{context}: phase {row.Phase}");
        Assert.Equal(outcome == Approved ? Executed : outcome, row.Outcome);
        Assert.Equal(capability, row.Action);
        Assert.Equal(surface, row.Surface);
        Assert.False(string.IsNullOrEmpty(row.EventId), context);
        Assert.False(string.IsNullOrEmpty(row.OperationId), context);
        Assert.False(string.IsNullOrEmpty(row.PrincipalKind), context);
        Assert.False(string.IsNullOrEmpty(row.PrincipalId), context);
        if (owned && row.PrincipalKind is nameof(MutationPrincipalKind.Agent) or nameof(MutationPrincipalKind.Automation)
            or nameof(MutationPrincipalKind.Webhook))
        {
            Assert.False(string.IsNullOrEmpty(row.OnBehalfOfId), context);
            Assert.False(string.IsNullOrEmpty(row.OnBehalfOfKind), context);
        }
        Assert.False(string.IsNullOrEmpty(row.TargetType), context);
        // The Whiskers instance and the fleet are one target each: their kind names them, they have no id.
        if (targetId is not null) Assert.Equal(targetId, row.TargetId);
        else if (row.TargetType is not ("whiskers" or "fleet"))
            Assert.False(string.IsNullOrEmpty(row.TargetId), $"{context}: no target id");
        foreach (var field in new[] { row.Arguments, row.Details, row.TargetId, row.TargetName })
            Assert.DoesNotContain(Probe, field ?? "", StringComparison.Ordinal);
        Assert.NotNull(row.Arguments);
        using (JsonDocument.Parse(row.Arguments)) { }
        Assert.NotNull(row.Details);
        using (JsonDocument.Parse(row.Details)) { }
        Assert.False(string.IsNullOrEmpty(row.Verification), context);
        Assert.NotNull(row.DurationMs);
        Assert.Equal(outcome is Executed or Approved, row.Success);
        // Decided: every run, wait and failure, and every refusal at or after the policy step (kill switch, protected
        // resource, forbidden argument). Not decided: only a refusal before it.
        Assert.True(decided || outcome == Denied, $"{context}: only a refusal comes before the decision");
        Assert.True(decided != string.IsNullOrEmpty(row.DecisionId),
            $"{context}: decision id '{row.DecisionId}', expected {(decided ? "one" : "none (refused before the policy step)")}");
        if (outcome is Denied or Pending) Assert.Equal("NotRun", row.Verification);
        if (outcome is Pending)
        {
            Assert.False(string.IsNullOrEmpty(row.ApprovalId), context);
            Assert.Null(row.ApprovedBy);
        }
        if (outcome is Approved)
        {
            Assert.False(string.IsNullOrEmpty(row.ApprovalId), context);
            Assert.False(string.IsNullOrEmpty(row.ApprovedBy), context);
        }
        if (outcome is Executed) Assert.Null(row.ApprovalId);
    }
}
