using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.LogMonitor;
using Whiskers.Services.Observability.SelfMetrics;
using Whiskers.Services.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Whiskers.Tests;

/// <summary>
/// What happens when a paused server comes back (Plan-0005 WP4).
///
/// <para>The worry the plan names: a pause that ends produces a burst — missed cycles caught up all at once,
/// a log window as wide as the pause was long, every server returning in the same instant. That would make
/// the emergency stop a way of <em>scheduling</em> an incident rather than preventing one.</para>
///
/// <para>These tests measure whether that burst actually happens rather than assuming it does. Two of the
/// three protections turn out to be already in place from earlier packages — which is worth proving, because
/// "we built a mechanism" and "the problem cannot occur" are different claims and only one of them survives
/// somebody refactoring the mechanism away.</para>
/// </summary>
public sealed class ResumeWithoutStormTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"resume-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public ResumeWithoutStormTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.Database.EnsureCreated();
        db.LogAlertRules.Add(new LogAlertRuleEntity { Name = "fatal", Pattern = "FATAL", Severity = "error" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _sp.Dispose();
        TestSqlite.ClearPool(_dbPath);
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }

    /// <summary>A tiny lookback cap so a gap of a few hundred milliseconds stands in for a pause of hours.
    /// Without this the cap simply never engages in a test, and the test measures nothing.</summary>
    private static readonly TimeSpan TinyLookback = TimeSpan.FromMilliseconds(50);

    private LogMonitorService Monitor(FakeDocker docker, TimeSpan? maxLookback = null) =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(),
            docker,
            new FakeServerConfig(new ServerConfig
            {
                Id = "local", Name = "Badwolf", ConnectionType = ConnectionType.Local, IsDefault = true
            }),
            new FakeNotifications(),
            NullLogger<LogMonitorService>.Instance,
            TestBudget.Create(),
            new SelfMetrics(),
            new NoExclusions(),
            new NoOutcomes(),
            logFetchTimeout: null,
            maxLookback: maxLookback);

    private static FakeDocker OneContainer() => new(new ContainerInfo
    {
        Id = "c1", Name = "burg-web", Image = "img:1", ServerId = "local", ServerName = "Badwolf"
    });

    [Fact]
    public async Task A_returning_server_is_not_asked_for_the_whole_pause_at_once()
    {
        // WP4.2. After four hours paused the watermark is four hours old, and an uncapped `since` would ask
        // the daemon to read four hours of logs from every container in the same instant — the exact shape of
        // the incident, triggered by the thing meant to prevent it.
        //
        // The cap from Plan-0002 WP1 already covers this. Proving it here rather than adding a second
        // mechanism: "the problem cannot occur" outlives "we built something".
        // The cap engages only when the watermark is older than the cap itself, so the test shrinks the cap
        // to 50 ms and lets 300 ms pass. In production that is a ten-minute cap against a pause of hours; the
        // arithmetic is the same.
        //
        // Two earlier versions of this test proved nothing and both passed against a build with the cap
        // removed: one used a 20 ms gap against the real ten-minute cap, the other assumed a fresh container
        // starts from DateTime.MinValue when it actually baselines to now. Written down because the mistake
        // is not obvious and it was made twice in a row.
        var docker = OneContainer();
        var monitor = Monitor(docker, TinyLookback);

        await monitor.RunScanCycleAsync(CancellationToken.None);   // establishes a watermark at "now"
        var afterFirst = docker.CallsInOrder.Count;

        await Task.Delay(300);                                     // stands in for the pause
        await monitor.RunScanCycleAsync(CancellationToken.None);

        var requested = docker.CallsInOrder.Skip(afterFirst).Single().Since;
        Assert.NotNull(requested);
        var window = DateTime.UtcNow - requested!.Value;

        Assert.True(window < TimeSpan.FromMilliseconds(250),
            $"the first scan after the pause asked for {window.TotalMilliseconds:F0} ms of logs; the cap was " +
            $"{TinyLookback.TotalMilliseconds:F0} ms, so the pause was not capped at all");
    }

    [Fact]
    public async Task Missed_cycles_are_not_caught_up_afterwards()
    {
        // WP4.1. A queue of skipped cycles would turn a four-hour pause into 240 scans in a row. The loops
        // have no such queue — a cycle that did not happen is simply gone — and this pins that: the first
        // cycle after a gap costs exactly one fetch per container, like any other cycle.
        var docker = OneContainer();
        var monitor = Monitor(docker);

        await monitor.RunScanCycleAsync(CancellationToken.None);
        var afterFirst = docker.CallsInOrder.Count;

        await Task.Delay(20);
        await monitor.RunScanCycleAsync(CancellationToken.None);

        var secondCycleCalls = docker.CallsInOrder.Count - afterFirst;
        Assert.Equal(1, secondCycleCalls);
    }

    [Fact]
    public async Task The_first_five_cycles_after_a_pause_cost_the_same_as_any_other_five()
    {
        // The plan's acceptance criterion, measured: "the call rate in the first five minutes after the pause
        // ends does not exceed the normal level". Steady state first, then the same count again after a gap.
        var docker = OneContainer();
        var monitor = Monitor(docker);

        for (var i = 0; i < 5; i++) await monitor.RunScanCycleAsync(CancellationToken.None);
        var steadyState = docker.CallsInOrder.Count;

        await Task.Delay(20);   // the gap
        var before = docker.CallsInOrder.Count;
        for (var i = 0; i < 5; i++) await monitor.RunScanCycleAsync(CancellationToken.None);

        Assert.Equal(steadyState, docker.CallsInOrder.Count - before);
    }
}
