using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.HealthChecks;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0023: the caller waits at most deadline + G; an action still running then is abandoned — Failed with
/// <c>Abandoned</c>, its target guarded, what it holds kept until its real end, and its late end audited as a further
/// version of the same event.
/// </summary>
public sealed class AbandonedRunTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const string Slow = "test.slow";
    private const string SlowHost = "test.slow_host";

    private static readonly Capability SlowCapability =
        new(Slow, McpPermissionLevels.Write, "container", CapabilityRisk.Reversible, null, Deadline);

    private static readonly Capability SlowHostCapability =
        new(SlowHost, McpPermissionLevels.Write, "host", CapabilityRisk.Disruptive, null, Deadline);

    private static readonly MutationTarget Web = new("container", "c1", "web", "srv1");

    private sealed class Harness
    {
        public RecordingAudit Audit { get; } = new();
        public RecordingJournal Journal { get; } = new();
        public AbandonedRuns Abandoned { get; } = new();
        public MaintenanceStateService Maintenance { get; } = new();
        public ShutdownDrain Drain { get; } = new(NullLogger<ShutdownDrain>.Instance, TimeSpan.FromSeconds(1));
        public RecordingRecovery Recovery { get; } = new();
        public MutationGateway Gateway { get; }

        public Harness(IMutationVerifier? verifier = null, TimeSpan? grace = null)
        {
            Gateway = new MutationGateway(new Catalog(), new AllowAll(), Audit, verifier is null ? [] : [verifier],
                NullLogger<MutationGateway>.Instance, journal: Journal, recovery: Recovery, maintenance: Maintenance,
                drain: Drain, abandoned: Abandoned,
                options: Options.Create(new MutationGatewayOptions { AbandonGrace = grace ?? Grace }));
        }
    }

    private static MutationRequest Request(string capability = Slow, MutationTarget? target = null,
        string? operationId = null) =>
        new(capability, new MutationPrincipal(MutationPrincipalKind.Human, "user:ops@example.com", "ops@example.com",
                McpPermissionLevels.Admin),
            MutationSurface.Ui, target ?? Web, new Dictionary<string, JsonElement>(), operationId);

    /// <summary>An action that ignores its token and ends only when <paramref name="release"/> is set.</summary>
    private static Func<MutationContext, CancellationToken, Task<int>> Stuck(TaskCompletionSource release,
        Func<Task>? afterRelease = null) => async (_, _) =>
    {
        await release.Task;
        if (afterRelease is not null) await afterRelease();
        return 7;
    };

    [Fact]
    public async Task An_action_that_ignores_its_token_is_answered_after_deadline_plus_grace_as_abandoned()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();

        var outcome = await h.Gateway.ExecuteAsync(Request(operationId: "op-a"), Stuck(release));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"answered after {clock.Elapsed}");
        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.True(outcome.Abandoned);
        Assert.Equal(MutationReasonCodes.Abandoned, outcome.ReasonCode);
        Assert.Equal(VerificationStatus.NotRun, outcome.Verification);
        var error = Assert.IsType<MutationAbandonedException>(outcome.Error);
        Assert.Equal(Deadline, error.Deadline);
        Assert.Contains("ran past its deadline", outcome.Reason);
        Assert.Equal(default, outcome.Value);
        // The outcome row is written straight away; the late end is not there yet.
        Assert.Equal(["begin:test.slow", "complete:Failed"], h.Audit.Events);
        Assert.False(outcome.Completion.IsCompleted);
        Assert.Single(h.Abandoned.Running);

        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);
        Assert.Equal(["begin:test.slow", "complete:Failed", "late_end:returned"], h.Audit.Events);
        Assert.Empty(h.Abandoned.Running);
    }

    [Fact]
    public async Task An_action_that_stops_on_its_token_is_failed_without_abandoned()
    {
        var h = new Harness();

        var outcome = await h.Gateway.ExecuteAsync(Request(), async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return 1;
        });

        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.False(outcome.Abandoned);
        Assert.Null(outcome.ReasonCode);
        Assert.Contains("exceeded its deadline", outcome.Reason);
        Assert.True(outcome.Completion.IsCompleted);
        Assert.Equal(["begin:test.slow", "complete:Failed"], h.Audit.Events);
        Assert.Equal([(MutationStatus.Failed, VerificationStatus.NotRun)], h.Journal.Finished.Values);
    }

    [Fact]
    public async Task An_action_returning_after_its_deadline_but_within_grace_is_verified_with_a_token_of_its_own()
    {
        var verifier = new TokenVerifier(Slow);
        var h = new Harness(verifier, grace: TimeSpan.FromSeconds(2));

        var outcome = await h.Gateway.ExecuteAsync(Request(), async (_, _) =>
        {
            await Task.Delay(Deadline * 3);   // past the deadline, well inside G
            return 5;
        });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.False(outcome.Abandoned);
        Assert.Equal(VerificationStatus.Verified, outcome.Verification);
        Assert.False(verifier.SawCancelledToken);
        Assert.Equal(5, outcome.Value);
    }

    [Fact]
    public async Task At_the_grace_boundary_exactly_one_of_the_two_endings_happens()
    {
        for (var i = 0; i < 30; i++)
        {
            var h = new Harness(grace: TimeSpan.FromMilliseconds(50));
            var outcome = await h.Gateway.ExecuteAsync(Request(operationId: $"edge-{i}"), async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150));   // deadline 100 ms + G 50 ms
                return 1;
            });
            await outcome.Completion.WaitAsync(Wait);

            var lateEnds = h.Audit.Events.Count(e => e.StartsWith("late_end:", StringComparison.Ordinal));
            if (outcome.Abandoned)
            {
                Assert.Equal(1, lateEnds);
                Assert.Equal(MutationStatus.Failed, outcome.Status);
            }
            else
            {
                Assert.Equal(0, lateEnds);
                Assert.Equal(MutationStatus.Executed, outcome.Status);
            }
            Assert.Single(h.Journal.Finished);
            Assert.Empty(h.Abandoned.Running);
        }
    }

    [Fact]
    public async Task The_late_end_names_how_it_ended_and_what_the_tool_reported_after_the_abandon()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var outcome = await h.Gateway.ExecuteAsync(Request(), Stuck(release, () =>
        {
            MutationScope.Record(new MutationDomainResult("container.restart", "container", "c1", "web", "late", "srv1", true));
            throw new TimeoutException("the host never answered");
        }));
        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);

        var lateEnd = Assert.Single(h.Audit.LateEnds);
        Assert.Equal("threw TimeoutException", lateEnd.How);
        Assert.DoesNotContain("never answered", lateEnd.How);   // the type only
        Assert.Contains(lateEnd.Results!, r => r.Details == "late");
        Assert.Equal([(MutationStatus.Failed, VerificationStatus.NotRun)], h.Journal.Finished.Values);
    }

    [Fact]
    public async Task A_late_end_that_cannot_be_written_is_kept_for_recovery()
    {
        var h = new Harness();
        h.Audit.FailLateEnd = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var outcome = await h.Gateway.ExecuteAsync(Request(operationId: "op-kept"), Stuck(release));
        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);

        var kept = Assert.Single(h.Recovery.LateEnds);
        Assert.Equal("op-kept", kept.OperationId);
        Assert.Equal("returned", kept.How);
    }

    [Fact]
    public async Task The_journal_claim_stays_running_until_the_real_end_and_then_is_failed_never_executed()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var outcome = await h.Gateway.ExecuteAsync(Request(operationId: "op-j"), Stuck(release));
        Assert.Empty(h.Journal.Finished);

        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);
        Assert.Equal((MutationStatus.Failed, VerificationStatus.NotRun), h.Journal.Finished["op-j"]);
    }

    [Fact]
    public async Task The_scope_stays_open_for_a_nested_call_the_abandoned_action_makes_afterwards()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MutationOutcome<int>? nested = null;
        string? scopeAfterAbandon = null;

        var outcome = await h.Gateway.ExecuteAsync(Request(SlowHost, new MutationTarget("host", "srv1", null, "srv1")),
            Stuck(release, async () =>
            {
                scopeAfterAbandon = MutationScope.Current?.CapabilityId;
                // On the same server as the abandoned host-level run: only its own nested call passes the guard.
                nested = await h.Gateway.ExecuteAsync(Request("container.start"), (_, _) => Task.FromResult(1));
            }));
        Assert.True(outcome.Abandoned);

        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);
        Assert.Equal(SlowHost, scopeAfterAbandon);
        Assert.Equal(MutationStatus.Executed, nested!.Status);
    }

    [Fact]
    public async Task An_overlapping_target_is_refused_until_the_abandoned_run_ends()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = await h.Gateway.ExecuteAsync(Request(operationId: "op-hold"), Stuck(release));
        Assert.True(outcome.Abandoned);
        h.Audit.Clear();

        var ran = false;
        var same = await h.Gateway.ExecuteAsync(Request("container.start", operationId: "another-id"),
            (_, _) => { ran = true; return Task.FromResult(1); });
        Assert.Equal(MutationStatus.Denied, same.Status);
        Assert.Equal(MutationReasonCodes.TargetBusy, same.ReasonCode);
        Assert.False(ran);
        Assert.Equal(["denied:Denied"], h.Audit.Events);   // the attempt's single event

        // A host-level target on the same server overlaps every target there.
        var host = await h.Gateway.ExecuteAsync(Request("host.prune", new MutationTarget("host", "srv1", null, "srv1")),
            (_, _) => Task.FromResult(1));
        Assert.Equal(MutationReasonCodes.TargetBusy, host.ReasonCode);

        // Another container on another server does not.
        var elsewhere = await h.Gateway.ExecuteAsync(Request("container.start", new MutationTarget("container", "c1", "web", "srv2")),
            (_, _) => Task.FromResult(1));
        Assert.Equal(MutationStatus.Executed, elsewhere.Status);

        // A ParentOperationId the caller supplies, with no open scope, is not a way past it.
        var forged = await h.Gateway.ExecuteAsync(Request("container.start") with { ParentOperationId = "op-hold" },
            (_, _) => Task.FromResult(1));
        Assert.Equal(MutationReasonCodes.TargetBusy, forged.ReasonCode);

        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);
        var after = await h.Gateway.ExecuteAsync(Request("container.start"), (_, _) => Task.FromResult(1));
        Assert.Equal(MutationStatus.Executed, after.Status);
    }

    [Fact]
    public async Task A_related_resource_of_the_abandoned_target_is_refused_too()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var network = new MutationTarget("network", "n1", "backend", "srv1") { Related = ["c1"] };
        var outcome = await h.Gateway.ExecuteAsync(Request(target: network), Stuck(release));
        Assert.True(outcome.Abandoned);

        var container = await h.Gateway.ExecuteAsync(Request("container.start"), (_, _) => Task.FromResult(1));
        Assert.Equal(MutationReasonCodes.TargetBusy, container.ReasonCode);

        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task The_kill_switch_toggles_pass_the_guard_and_a_guardrails_save_does_not()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new MutationTarget("settings", "guardrails");
        var outcome = await h.Gateway.ExecuteAsync(Request(target: policy), Stuck(release));
        Assert.True(outcome.Abandoned);

        foreach (var toggle in MutationGateway.KillSwitchToggles)
        {
            var passed = await h.Gateway.ExecuteAsync(Request(toggle, policy), (_, _) => Task.FromResult(1));
            Assert.NotEqual(MutationReasonCodes.TargetBusy, passed.ReasonCode);
        }
        var save = await h.Gateway.ExecuteAsync(Request("guardrails.save", policy), (_, _) => Task.FromResult(1));
        Assert.Equal(MutationReasonCodes.TargetBusy, save.ReasonCode);

        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task An_outer_run_whose_nested_run_is_still_abandoned_is_failed_with_nested_abandoned_and_keeps_what_it_holds()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MutationOutcome<int>? inner = null;
        var outerCapability = "container.restart";

        var clock = Stopwatch.StartNew();
        var outer = await h.Gateway.ExecuteAsync(Request(outerCapability, operationId: "outer"), async (_, _) =>
        {
            inner = await h.Gateway.ExecuteAsync(Request(operationId: "inner"), Stuck(release));
            return 3;   // the outer action returns at once, the inner one runs on
        });

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"answered after {clock.Elapsed}");
        Assert.True(inner!.Abandoned);
        Assert.Equal(MutationStatus.Failed, outer.Status);
        Assert.Equal(MutationReasonCodes.NestedAbandoned, outer.ReasonCode);
        Assert.False(outer.Abandoned);
        Assert.Equal(default, outer.Value);
        Assert.Equal("inner", Assert.IsType<MutationAbandonedException>(outer.Error).NestedOperationId);
        Assert.Equal(VerificationStatus.NotRun, outer.Verification);

        // Held until the inner run ends: the outer completion, its claim, the maintenance admission.
        Assert.False(outer.Completion.IsCompleted);
        Assert.False(h.Journal.Finished.ContainsKey("outer"));
        Assert.False(await h.Maintenance.WaitForIdleAsync(TimeSpan.FromMilliseconds(100)));

        release.SetResult();
        await outer.Completion.WaitAsync(Wait);
        Assert.True(inner.Completion.IsCompleted);
        Assert.Equal((MutationStatus.Failed, VerificationStatus.NotRun), h.Journal.Finished["outer"]);
        Assert.True(await h.Maintenance.WaitForIdleAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task An_outer_action_passing_on_its_nested_abandon_is_failed_with_nested_abandoned_never_as_a_cancellation()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // As the compose gates do: the nested outcome's error is thrown on.
        var outer = await h.Gateway.ExecuteAsync(Request("container.restart", operationId: "outer"), async (_, _) =>
        {
            var inner = await h.Gateway.ExecuteAsync(Request(operationId: "inner"), Stuck(release));
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner.Error!).Throw();
            return 3;
        });

        Assert.Equal(MutationStatus.Failed, outer.Status);
        Assert.Equal(MutationReasonCodes.NestedAbandoned, outer.ReasonCode);
        Assert.Equal("inner", Assert.IsType<MutationAbandonedException>(outer.Error).NestedOperationId);
        Assert.Contains(MutationGateway.NestedAbandonedReason, outer.Reason);
        Assert.False(outer.Completion.IsCompleted);

        release.SetResult();
        await outer.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task The_outcome_row_names_the_abandon_by_its_reason_code()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = await h.Gateway.ExecuteAsync(Request(), Stuck(release));

        var reason = Assert.Single(h.Audit.ReasonLog);
        Assert.EndsWith($"({MutationReasonCodes.Abandoned})", reason);
        Assert.StartsWith(outcome.Reason!, reason);

        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);
    }

    [Fact]
    public async Task At_the_shutdown_cut_off_the_late_end_is_written_but_nothing_is_released_and_completion_stays_open()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = await h.Gateway.ExecuteAsync(Request(operationId: "op-cut"), Stuck(release));
        Assert.True(outcome.Abandoned);

        // The drain budget (1 s here) runs out with the run still at work: Abort.
        await h.Drain.DrainAsync(CancellationToken.None);
        var clock = Stopwatch.StartNew();
        while (!h.Audit.Events.Contains("late_end:cut off at shutdown") && clock.Elapsed < Wait)
            await Task.Delay(20);
        Assert.Contains("late_end:cut off at shutdown", h.Audit.Events);

        // The action still runs: no caller may close its records as if it had ended (INV-39 reports it at the next
        // start), and nothing it holds is released.
        await Task.Delay(200);
        Assert.False(outcome.Completion.IsCompleted);
        Assert.False(h.Journal.Finished.ContainsKey("op-cut"));
        Assert.Single(h.Abandoned.Running);
        Assert.Single(h.Drain.InFlight);
        Assert.False(await h.Maintenance.WaitForIdleAsync(TimeSpan.FromMilliseconds(100)));
        release.SetResult();
    }

    [Fact]
    public async Task A_restore_waits_for_an_abandoned_run()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = await h.Gateway.ExecuteAsync(Request(), Stuck(release));
        Assert.True(outcome.Abandoned);

        Assert.False(await h.Maintenance.WaitForIdleAsync(TimeSpan.FromMilliseconds(100)));
        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);
        Assert.True(await h.Maintenance.WaitForIdleAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task The_shutdown_drain_waits_for_an_abandoned_run()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = await h.Gateway.ExecuteAsync(Request(), Stuck(release));
        Assert.True(outcome.Abandoned);
        Assert.Single(h.Drain.InFlight);

        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);
        Assert.Empty(h.Drain.InFlight);
    }

    [Fact]
    public async Task A_top_level_call_after_stopping_has_begun_is_refused_and_recorded_and_a_nested_one_is_not()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MutationOutcome<int>? nested = null;

        // A run admitted before the stop makes a nested call during the drain.
        var running = h.Gateway.ExecuteAsync(Request("container.restart", operationId: "admitted"), async (_, _) =>
        {
            release.SetResult();
            await proceed.Task;
            nested = await h.Gateway.ExecuteAsync(Request("container.start", new MutationTarget("container", "c2", "db", "srv1")),
                (_, _) => Task.FromResult(1));
            return 1;
        });
        await release.Task.WaitAsync(Wait);
        var draining = h.Drain.DrainAsync(CancellationToken.None);
        h.Audit.Clear();

        var ran = false;
        var refused = await h.Gateway.ExecuteAsync(Request("container.start"), (_, _) => { ran = true; return Task.FromResult(1); });
        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, refused.Status);
        Assert.Equal(MutationReasonCodes.ShuttingDown, refused.ReasonCode);
        Assert.Equal(MutationGateway.ShuttingDownRefusal, refused.Reason);
        Assert.Equal(["denied:Denied"], h.Audit.Events);

        proceed.SetResult();
        var outer = await running.WaitAsync(Wait);
        Assert.Equal(MutationStatus.Executed, nested!.Status);
        Assert.Equal(MutationStatus.Executed, outer.Status);
        await draining.WaitAsync(Wait);
    }

    [Fact]
    public async Task Abandoned_runs_are_counted_and_readiness_degrades_after_the_limit()
    {
        var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = await h.Gateway.ExecuteAsync(Request(operationId: "op-l"), Stuck(release));

        var run = Assert.Single(h.Abandoned.Running);
        Assert.Equal(Slow, run.CapabilityId);
        Assert.Equal("op-l", run.OperationId);

        var soon = new AbandonedRunsCheck(h.Abandoned, Options.Create(new MutationGatewayOptions()));
        Assert.Equal(HealthStatus.Healthy, (await soon.CheckHealthAsync(new HealthCheckContext())).Status);
        var later = new AbandonedRunsCheck(h.Abandoned, Options.Create(new MutationGatewayOptions()),
            new Later(TimeSpan.FromMinutes(16)));
        var degraded = await later.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, degraded.Status);
        Assert.Contains("past its deadline", degraded.Description);

        release.SetResult();
        await outcome.Completion.WaitAsync(Wait);
        Assert.Equal(HealthStatus.Healthy, (await later.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [Fact]
    public void Targets_overlap_by_identity_by_related_resource_and_by_server_level()
    {
        var c1 = new MutationTarget("container", "c1", "web", "srv1");
        Assert.True(AbandonedRuns.Overlap(c1, new MutationTarget("container", "c1", null, "srv1")));
        Assert.False(AbandonedRuns.Overlap(c1, new MutationTarget("container", "c1", null, "srv2")));
        Assert.False(AbandonedRuns.Overlap(c1, new MutationTarget("container", "c2", null, "srv1")));
        Assert.True(AbandonedRuns.Overlap(c1, new MutationTarget("host", "srv1", null, "srv1")));
        Assert.True(AbandonedRuns.Overlap(new MutationTarget("server", "srv1"), c1));
        Assert.False(AbandonedRuns.Overlap(new MutationTarget("host", "srv2", null, "srv2"), c1));
        Assert.True(AbandonedRuns.Overlap(new MutationTarget("network", "n1", null, "srv1") { Related = ["web"] }, c1));
        Assert.True(AbandonedRuns.Overlap(c1,
            new MutationTarget("stack", "s1", null, "srv1") { RelatedResources = [["c1", "web"]] }));
        Assert.False(AbandonedRuns.Overlap(new MutationTarget("settings", "guardrails"), c1));
    }

    // ------------------------------------------------------------------------------------------------ doubles

    private sealed class Catalog : ICapabilityCatalog
    {
        private readonly CapabilityCatalog _inner = new();
        public IReadOnlyCollection<Capability> All => [.. _inner.All, SlowCapability, SlowHostCapability];
        public Capability? Find(string capabilityId) => capabilityId switch
        {
            Slow => SlowCapability,
            SlowHost => SlowHostCapability,
            _ => _inner.Find(capabilityId),
        };
        public Capability? FindByMcpTool(string toolName) => _inner.FindByMcpTool(toolName);
    }

    private sealed class AllowAll : IMutationPolicy
    {
        public MutationPolicyDecision Evaluate(MutationRequest request, Capability capability) =>
            new(MutationVerdict.Allow, "allowed", []);
    }

    private sealed class TokenVerifier(string capabilityId) : IMutationVerifier
    {
        public bool SawCancelledToken { get; private set; }
        public string CapabilityId => capabilityId;

        public Task<VerificationStatus> VerifyAsync(MutationContext context, object? result, CancellationToken ct)
        {
            SawCancelledToken = ct.IsCancellationRequested;
            return Task.FromResult(ct.IsCancellationRequested ? VerificationStatus.Failed : VerificationStatus.Verified);
        }
    }

    internal sealed class RecordingAudit : IMutationAuditSink
    {
        private long _next;
        public bool FailLateEnd { get; set; }
        public ConcurrentQueue<string> EventLog { get; } = new();
        public List<string> Events
        {
            get => [.. EventLog];
        }
        public void Clear() => EventLog.Clear();
        public ConcurrentQueue<string?> ReasonLog { get; } = new();
        public ConcurrentQueue<PendingLateEnd> LateEndLog { get; } = new();
        public IReadOnlyList<PendingLateEnd> LateEnds => [.. LateEndLog];

        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct)
        {
            EventLog.Enqueue($"begin:{capability.Id}");
            return Task.FromResult(Interlocked.Increment(ref _next));
        }

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct)
        {
            EventLog.Enqueue($"complete:{status}");
            ReasonLog.Enqueue(reason);
            return Task.CompletedTask;
        }

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct)
        {
            EventLog.Enqueue($"denied:{status}");
            return Task.CompletedTask;
        }

        public Task CompleteLateEndAsync(long auditId, string operationId, string how, TimeSpan duration,
            IReadOnlyList<MutationDomainResult>? results, CancellationToken ct)
        {
            if (FailLateEnd) throw new InvalidOperationException("database is locked");
            EventLog.Enqueue($"late_end:{how}");
            LateEndLog.Enqueue(new PendingLateEnd(auditId, operationId, how, duration, results));
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingJournal : IOperationJournal
    {
        public ConcurrentDictionary<string, (MutationStatus, VerificationStatus)> Finished { get; } = new();

        public Task<OperationClaim> ClaimAsync(OperationClaimRequest claim, CancellationToken ct = default) =>
            Task.FromResult(new OperationClaim(OperationClaimResult.Claimed, "token-" + claim.OperationId));

        public Task<OperationClaimResult?> InspectAsync(string operationId, string inputHash, CancellationToken ct = default) =>
            Task.FromResult<OperationClaimResult?>(null);

        public Task FinishAsync(string operationId, string token, MutationStatus status, VerificationStatus verification,
            CancellationToken ct = default)
        {
            Finished[operationId] = (status, verification);
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(string operationId, string token, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class RecordingRecovery : IAuditRecovery
    {
        public ConcurrentQueue<PendingLateEnd> LateEndLog { get; } = new();
        public IReadOnlyList<PendingLateEnd> LateEnds => [.. LateEndLog];
        public void OutcomeNotWritten(PendingAuditOutcome outcome) { }
        public void LateEndNotWritten(PendingLateEnd lateEnd) => LateEndLog.Enqueue(lateEnd);
        public void LegacyWriteFailed() { }
        public AuditHealth Health => new(LateEndLog.Count, 0, false);
        public Task RecoverAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Later(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + ahead;
    }
}
