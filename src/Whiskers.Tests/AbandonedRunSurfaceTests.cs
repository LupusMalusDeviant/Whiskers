using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0023: how the surfaces report an abandoned run (point 2, 4d) and how its late end lands in the audit trail
/// (point 3): the UI runner throws <see cref="MutationFailedException"/>, the MCP filter answers with the failure, and
/// the late end is one further chain version of the same row.
/// </summary>
public sealed class AbandonedRunSurfaceTests
{
    /// <summary>A gateway that answers every call as <paramref name="code"/> without running it.</summary>
    private sealed class AnsweringGateway(string code) : IMutationGateway
    {
        public Task<MutationOutcome<T>> ExecuteAsync<T>(MutationRequest request,
            Func<MutationContext, CancellationToken, Task<T>> execute, CancellationToken ct = default)
        {
            var reason = code == MutationReasonCodes.Abandoned
                ? $"'{request.CapabilityId}' ran past its deadline of 00:00:01 and did not stop; it is still running."
                : $"'{request.CapabilityId}': {MutationGateway.NestedAbandonedReason}";
            return Task.FromResult(new MutationOutcome<T>("op-x", MutationStatus.Failed, default, reason,
                VerificationStatus.NotRun,
                new MutationAbandonedException(reason, TimeSpan.FromSeconds(1),
                    code == MutationReasonCodes.NestedAbandoned ? "op-inner" : null))
            {
                Abandoned = code == MutationReasonCodes.Abandoned,
                ReasonCode = code,
            });
        }
    }

    public static TheoryData<string, bool> Codes() => new()
    {
        { MutationReasonCodes.Abandoned, false },
        { MutationReasonCodes.Abandoned, true },
        { MutationReasonCodes.NestedAbandoned, false },
        { MutationReasonCodes.NestedAbandoned, true },
    };

    [Theory]
    [MemberData(nameof(Codes))]
    public async Task Both_ui_runner_overloads_throw_mutation_failed_also_when_the_page_token_is_cancelled(string code,
        bool pageCancelled)
    {
        var runner = new UiMutationRunner(new AnsweringGateway(code), new CapabilityCatalog(),
            new InlineApprovalRunnerTests.Person());
        using var page = new CancellationTokenSource();
        if (pageCancelled) await page.CancelAsync();
        var args = new { containerId = "web", serverId = "local" };

        var withValue = await Assert.ThrowsAsync<MutationFailedException>(() =>
            runner.RunAsync("container.restart", args, (ResolvedContainer _, CancellationToken _) => Task.FromResult(1),
                page.Token));
        var withoutValue = await Assert.ThrowsAsync<MutationFailedException>(() =>
            runner.RunAsync("container.restart", args, (ResolvedContainer _, CancellationToken _) => Task.CompletedTask,
                page.Token));

        foreach (var thrown in new[] { withValue, withoutValue })
        {
            Assert.Equal(code, thrown.ReasonCode);
            Assert.Equal(code == MutationReasonCodes.Abandoned, thrown.Abandoned);
            Assert.IsType<MutationAbandonedException>(thrown.InnerException);
        }
    }

    [Theory]
    [InlineData("restart_container", false)]   // a plain tool: an error result
    [InlineData("onboard_server", true)]       // an envelope tool (contract 0001)
    public async Task The_mcp_filter_answers_an_abandoned_run_as_a_failure_never_as_the_tools_answer(string tool, bool envelope)
    {
        foreach (var code in new[] { MutationReasonCodes.Abandoned, MutationReasonCodes.NestedAbandoned })
        {
            using var mcp = new McpFilterHarness { Gateway = new AnsweringGateway(code) };
            var result = await mcp.CallAsync(tool, McpFilterHarness.Ok,
                envelope ? new { serverId = "srv1" } : new { containerId = "web", serverId = "local" });

            Assert.True(result.IsError);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.NotEqual("done", text);   // never the tool's own answer
            Assert.Contains(code == MutationReasonCodes.Abandoned ? "ran past its deadline" : "nested run", text);
            Assert.Contains($"({code})", text);
            if (envelope)
                Assert.Equal("failed", JsonDocument.Parse(text).RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task The_late_end_is_a_further_chain_version_of_the_same_row_never_a_second_row()
    {
        using var trail = new AuditTrailFixture(AuditHashChainTests.VaultKey);
        var sink = new AuditLogMutationSink(trail.Scopes);
        var request = new MutationRequest("container.restart",
            new MutationPrincipal(MutationPrincipalKind.Human, "user:ops@example.com", "ops@example.com", McpPermissionLevels.Admin),
            MutationSurface.Ui, new MutationTarget("container", "c1", "web", "srv1"), new Dictionary<string, JsonElement>());
        var capability = new CapabilityCatalog().Find("container.restart")!;

        var id = await sink.BeginAsync("op-late", request, capability, null, CancellationToken.None);
        await sink.CompleteAsync(id, "op-late", MutationStatus.Failed, VerificationStatus.NotRun,
            "'container.restart' ran past its deadline", TimeSpan.FromSeconds(6), CancellationToken.None);
        await sink.CompleteLateEndAsync(id, "op-late", "returned", TimeSpan.FromMinutes(2),
            [new MutationDomainResult("container.restart", "container", "c1", "web", "restarted late", "srv1", true)],
            CancellationToken.None);

        using var scope = trail.Scope();
        var rows = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .Where(a => a.OperationId == "op-late").ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(AuditLogMutationSink.LateEndPhase, row.Phase);
        Assert.Equal(nameof(MutationStatus.Failed), row.Outcome);   // the outcome's status stays
        Assert.False(row.Success);
        using var details = JsonDocument.Parse(row.Details!);
        var lateEnd = details.RootElement.GetProperty("lateEnd");
        Assert.Equal("returned", lateEnd.GetProperty("how").GetString());
        Assert.Equal(120000, lateEnd.GetProperty("durationMs").GetInt64());
        Assert.Equal("restarted late", lateEnd.GetProperty("results")[0].GetProperty("details").GetString());
        Assert.Equal("Failed", details.RootElement.GetProperty("status").GetString());

        var report = await trail.Chain.VerifyAsync();
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(3, report.LinksChecked);   // intent, outcome and late end: three versions of one row
    }

    [Fact]
    public async Task A_late_end_before_its_outcome_is_refused_so_recovery_writes_it_after()
    {
        using var trail = new AuditTrailFixture(AuditHashChainTests.VaultKey);
        var sink = new AuditLogMutationSink(trail.Scopes);
        var request = new MutationRequest("container.restart",
            new MutationPrincipal(MutationPrincipalKind.Human, "user:ops@example.com", "ops@example.com", McpPermissionLevels.Admin),
            MutationSurface.Ui, new MutationTarget("container", "c1"), new Dictionary<string, JsonElement>());
        var id = await sink.BeginAsync("op-early", request, new CapabilityCatalog().Find("container.restart")!, null,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sink.CompleteLateEndAsync(id, "op-early", "returned", TimeSpan.Zero, null, CancellationToken.None));
    }
}
