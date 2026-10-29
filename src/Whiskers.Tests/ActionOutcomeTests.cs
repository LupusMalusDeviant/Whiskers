using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Observability.Outcomes;
using Whiskers.Services.Observability.SelfMetrics;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// Whether an automatic action actually achieved anything (Plan-0006 WP2).
///
/// <para>Whiskers counts an action as successful when the call returned without an error — not when the
/// problem went away. The most important behaviour tested here is the third verdict: <b>not measurable</b>.
/// Folding it into "worked" would be the 2026-08-26 incident's own shape one level up — the absence of a
/// signal read as the absence of a problem — and the plan calls that its central implementation rule.</para>
/// </summary>
public sealed class ActionOutcomeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"outcomes-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;
    private readonly SelfMetrics _metrics = new();

    public ActionOutcomeTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        TestSqlite.ClearPool(_dbPath);
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }

    private ActionOutcomeService Service() =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(), _metrics, NullLogger<ActionOutcomeService>.Instance);

    private MetricsDbContext Db() => _sp.CreateScope().ServiceProvider.GetRequiredService<MetricsDbContext>();

    /// <summary>Moves a recorded action's window into the past so it can be judged without waiting.</summary>
    private async Task MakeDueAsync()
    {
        using var db = Db();
        foreach (var row in await db.ActionOutcomes.ToListAsync())
            row.DueAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    // --- the verdict that matters most -------------------------------------------------------------------

    [Fact]
    public async Task Missing_data_is_not_measurable_and_never_counts_as_success()
    {
        // WP2.4, the plan's central rule. Nothing has recorded a latency for this server, so there is nothing
        // to judge against — and "we could not tell" must never be filed as "it worked".
        var service = Service();
        await service.RecordAsync(AutomaticActionKind.SelfThrottle, "badwolf", "badwolf", "Badwolf");
        await MakeDueAsync();

        var judged = Assert.Single(await service.EvaluateDueAsync(DateTime.UtcNow));

        Assert.Equal(ActionVerdict.NotMeasurable, judged.Verdict);
        Assert.Contains("No reading", judged.Detail);
    }

    [Fact]
    public async Task An_action_that_helped_is_recorded_as_having_worked()
    {
        // The other side: the rule has to be able to say yes, or "not measurable" is just a way of never
        // committing to anything.
        var service = Service();
        await service.RecordAsync(AutomaticActionKind.SelfThrottle, "badwolf", "badwolf", "Badwolf");
        _metrics.RecordApiCall("badwolf", TimeSpan.FromMilliseconds(120));
        await MakeDueAsync();

        var judged = Assert.Single(await service.EvaluateDueAsync(DateTime.UtcNow));

        Assert.Equal(ActionVerdict.Worked, judged.Verdict);
        Assert.Contains("Met.", judged.Detail);
    }

    [Fact]
    public async Task An_action_that_changed_nothing_is_recorded_as_not_having_worked()
    {
        // The verdict the whole package exists for. Whiskers throttled itself, the daemon is still crawling —
        // so the load was never ours, and the throttle is a blind spot imposed for nothing.
        var service = Service();
        await service.RecordAsync(AutomaticActionKind.SelfThrottle, "badwolf", "badwolf", "Badwolf");
        _metrics.RecordApiCall("badwolf", TimeSpan.FromSeconds(6));
        await MakeDueAsync();

        var judged = Assert.Single(await service.EvaluateDueAsync(DateTime.UtcNow));

        Assert.Equal(ActionVerdict.DidNotWork, judged.Verdict);
        Assert.Contains("Not met.", judged.Detail);
    }

    [Fact]
    public async Task Host_cpu_criteria_are_judged_against_the_reading_after_the_action()
    {
        // A reading from before the action describes the world it was meant to change. Getting this backwards
        // would make every emergency stop look successful the moment it was taken.
        var service = Service();
        await service.RecordAsync(AutomaticActionKind.EmergencyStop, "badwolf", "badwolf", "Badwolf");

        using (var db = Db())
        {
            db.ServerMetrics.Add(new ServerMetricEntity
            {
                ServerId = "badwolf", Timestamp = DateTime.UtcNow.AddMinutes(-30), CpuPercent = 20  // before
            });
            db.ServerMetrics.Add(new ServerMetricEntity
            {
                ServerId = "badwolf", Timestamp = DateTime.UtcNow.AddSeconds(5), CpuPercent = 98    // after
            });
            await db.SaveChangesAsync();
        }

        await MakeDueAsync();
        var judged = Assert.Single(await service.EvaluateDueAsync(DateTime.UtcNow));

        Assert.Equal(ActionVerdict.DidNotWork, judged.Verdict);
        Assert.Contains("98", judged.Detail);
    }

    [Fact]
    public async Task Every_declared_criterion_can_actually_produce_a_verdict()
    {
        // A criterion whose metric is never readable would be permanently "not measurable" — which looks like
        // diligence and is a control that never controls anything. This walks all of them and demands each
        // reaches a real verdict when the data it names is present.
        var service = Service();
        var now = DateTime.UtcNow;

        _metrics.RecordApiCall("srv", TimeSpan.FromMilliseconds(100));
        _metrics.RecordCycle("logmonitor", "srv", TimeSpan.FromMilliseconds(50), success: true, TimeSpan.FromMinutes(1));

        foreach (var kind in ActionCriteria.Declared)
            await service.RecordAsync(kind, "srv", "c1", "container-1");

        using (var db = Db())
        {
            db.ServerMetrics.Add(new ServerMetricEntity { ServerId = "srv", Timestamp = now.AddSeconds(5), CpuPercent = 10 });
            db.ContainerMetrics.Add(new ContainerMetricEntity { ContainerId = "c1", ServerId = "srv", Timestamp = now.AddSeconds(5) });
            await db.SaveChangesAsync();
        }

        await MakeDueAsync();
        var judged = await service.EvaluateDueAsync(DateTime.UtcNow);

        var unevaluatable = judged.Where(j => j.Verdict == ActionVerdict.NotMeasurable)
            .Select(j => $"{j.ActionKind}: {j.Detail}").ToList();
        Assert.True(unevaluatable.Count == 0,
            "These criteria could not be evaluated even with their own data present, so they can never be " +
            "anything but 'not measurable': " + string.Join(", ", unevaluatable));
    }

    [Fact]
    public async Task An_action_whose_window_spans_a_restart_is_not_measurable()
    {
        // WP2.4, first case. After a restart the in-memory series this would be judged against begins again
        // from nothing, so any reading describes the new process rather than the action. An action recorded
        // before the process started is exactly that situation.
        var service = Service();
        await service.RecordAsync(AutomaticActionKind.SelfThrottle, "badwolf", "badwolf", "Badwolf");
        _metrics.RecordApiCall("badwolf", TimeSpan.FromMilliseconds(50));   // would otherwise pass easily

        using (var db = Db())
        {
            var row = await db.ActionOutcomes.SingleAsync();
            row.ExecutedAtUtc = DateTime.UtcNow.AddYears(-1);   // long before this process began
            row.DueAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var judged = Assert.Single(await service.EvaluateDueAsync(DateTime.UtcNow));

        Assert.Equal(ActionVerdict.NotMeasurable, judged.Verdict);
        Assert.Contains("restarted inside the check window", judged.Detail);
    }

    [Fact]
    public async Task The_restart_guard_does_not_swallow_ordinary_actions()
    {
        // The counterweight, and the reason this test exists at all. The first version of the guard captured
        // the start time in a static field, which the runtime is free to initialise as late as first access —
        // here, the evaluation itself. The "start time" then landed after every action and EVERY outcome came
        // back not-measurable: a checker that never checks, wearing the costume of diligence.
        var service = Service();
        await service.RecordAsync(AutomaticActionKind.SelfThrottle, "badwolf", "badwolf", "Badwolf");
        _metrics.RecordApiCall("badwolf", TimeSpan.FromMilliseconds(50));
        await MakeDueAsync();

        var judged = Assert.Single(await service.EvaluateDueAsync(DateTime.UtcNow));

        Assert.NotEqual(ActionVerdict.NotMeasurable, judged.Verdict);
    }

    // --- bookkeeping --------------------------------------------------------------------------------------

    [Fact]
    public async Task Recording_an_action_with_no_declared_criterion_is_refused()
    {
        // The enforcement point for WP1.3: an automatic action nobody can check must not even reach the point
        // of being recorded as done.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().RecordAsync((AutomaticActionKind)9999, "s", "t", "T"));
    }

    [Fact]
    public async Task A_window_that_has_not_elapsed_is_left_alone()
    {
        var service = Service();
        await service.RecordAsync(AutomaticActionKind.SelfThrottle, "badwolf", "badwolf", "Badwolf");

        Assert.Empty(await service.EvaluateDueAsync(DateTime.UtcNow));
        Assert.Equal(ActionVerdict.Pending, Db().ActionOutcomes.Single().Verdict);
    }

    [Fact]
    public async Task The_tally_keeps_not_measurable_apart_from_the_rest()
    {
        // WP5.1. A hit rate computed over unmeasurable attempts would flatter itself exactly when the
        // measuring has broken down.
        var service = Service();
        await service.RecordAsync(AutomaticActionKind.SelfThrottle, "a", "a", "A");
        await service.RecordAsync(AutomaticActionKind.SelfThrottle, "b", "b", "B");
        _metrics.RecordApiCall("a", TimeSpan.FromMilliseconds(100));   // a is measurable and fine; b is not
        await MakeDueAsync();
        await service.EvaluateDueAsync(DateTime.UtcNow);

        var tally = Assert.Single(await service.TalliesAsync(DateTime.UtcNow.AddHours(-1)));

        Assert.Equal(1, tally.Worked);
        Assert.Equal(1, tally.NotMeasurable);
        Assert.Equal(1, tally.Judged);          // the unmeasurable one is not in the denominator
        Assert.Equal(1.0, tally.HitRate);
    }

    [Fact]
    public async Task An_untried_action_has_no_hit_rate_rather_than_a_hit_rate_of_zero()
    {
        // Zero would read as "never works" for something that has simply not been tried.
        var service = Service();
        await service.RecordAsync(AutomaticActionKind.ContainerRestart, "a", "c", "C");

        var tally = Assert.Single(await service.TalliesAsync(DateTime.UtcNow.AddHours(-1)));

        Assert.Null(tally.HitRate);
        Assert.Equal(1, tally.Pending);
    }

    [Fact]
    public async Task Windows_that_came_due_and_were_never_judged_are_countable()
    {
        // WP5.3. A number that only grows means the sweep has stopped — and a checker that has stopped
        // checking looks exactly like a fleet with nothing to check.
        var service = Service();
        await service.RecordAsync(AutomaticActionKind.SelfThrottle, "a", "a", "A");
        await MakeDueAsync();

        Assert.Equal(1, await service.OverdueCountAsync(DateTime.UtcNow));

        await service.EvaluateDueAsync(DateTime.UtcNow);
        Assert.Equal(0, await service.OverdueCountAsync(DateTime.UtcNow));
    }

    [Fact]
    public async Task The_chain_from_trigger_to_outcome_is_one_identifier()
    {
        // WP2.3: the correlation id is the whole point of the record — without it the outcome is a fact
        // about nothing in particular.
        var service = Service();
        var correlation = await service.RecordAsync(
            AutomaticActionKind.LogScanLockout, "badwolf", "c1", "ghostunnel", reason: "3 timeouts in a row");

        var row = Db().ActionOutcomes.Single();

        Assert.Equal(correlation, row.CorrelationId);
        Assert.Equal("3 timeouts in a row", row.Reason);
        Assert.Equal("ghostunnel", row.TargetName);
    }
}
