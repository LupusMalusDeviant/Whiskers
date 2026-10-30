using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.LogMonitor;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// The load invariants from the 2026-08-26 incident (Plan-0001 WP6.1/WP6.2).
///
/// <para>For six days Whiskers held a 2-core host at 98% CPU. The log monitor kept starting fresh log fetches
/// against two containers whose logs it could no longer read inside its 15-second window. It abandoned the
/// <em>wait</em> but not the <em>request</em>: <c>Task.WhenAny</c> leaves the losing task running, so the HTTP
/// call to dockerd stayed open until the proxy cut it after 600 seconds. At one new request per 60-second
/// cycle that settles at ten concurrent full-log scans per container — measured as 13 open file descriptors
/// and 1.15 million read() syscalls per second.</para>
///
/// <para>The incident report states the check that proves the fix, and these tests are that check:
/// <em>"the number of simultaneously open log requests against this container stays at no more than 1, and the
/// duration of a fetch does not grow over the cycles."</em> They are written to fail against the code that
/// caused the incident — a guard that was never seen red proves nothing.</para>
/// </summary>
public sealed class LogMonitorLoadInvariantTests : IDisposable
{
    // Short enough to drive ten cycles in a second; the ratio is what matters, not the absolute values.
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WedgedHostDelay = TimeSpan.FromMilliseconds(400);
    private const int Cycles = 10;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"logmon-load-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public LogMonitorLoadInvariantTests()
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

    /// <summary>Two containers on one host whose log fetch takes eight times the monitor's timeout — the
    /// wedged socket-proxy and ghostunnel of the incident.</summary>
    private static FakeDocker WedgedHost()
    {
        var docker = new FakeDocker(
            new ContainerInfo { Id = "c-proxy", Name = "socket-proxy", Image = "img:1", ServerId = "local", ServerName = "Badwolf" },
            new ContainerInfo { Id = "c-tunnel", Name = "ghostunnel", Image = "img:1", ServerId = "local", ServerName = "Badwolf" })
        {
            FetchDelay = WedgedHostDelay
        };
        return docker;
    }

    private LogMonitorService Monitor(FakeDocker docker) =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(),
            docker,
            new FakeServerConfig(new ServerConfig { Id = "local", Name = "Badwolf", ConnectionType = ConnectionType.Local, IsDefault = true }),
            new FakeNotifications(),
            NullLogger<LogMonitorService>.Instance,
            TestBudget.Create(), new Whiskers.Services.Observability.SelfMetrics.SelfMetrics(),
            new NoExclusions(), new NoOutcomes(),
            FetchTimeout);

    private async Task RunCyclesAsync(LogMonitorService monitor)
    {
        // Back to back, exactly as the incident did: the cycle timer does not wait for abandoned work.
        for (var i = 0; i < Cycles; i++)
            await monitor.RunScanCycleAsync(CancellationToken.None);
    }

    [Fact]
    public async Task At_most_one_log_request_per_container_is_ever_in_flight()
    {
        var docker = WedgedHost();

        await RunCyclesAsync(Monitor(docker));

        // Give any abandoned request time to surface as concurrency before measuring — without this the test
        // could pass simply because the run ended before the overlap became visible.
        await Task.Delay(WedgedHostDelay);

        var worst = docker.PeakConcurrentPerContainer
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}: {kv.Value} concurrent")
            .ToList();

        Assert.True(
            docker.PeakConcurrentPerContainer.Values.All(peak => peak <= 1),
            "A timed-out fetch kept running while the next cycle started another — the incident's steady " +
            "state. Peaks:\n  " + string.Join("\n  ", worst));
    }

    [Fact]
    public async Task Abandoned_requests_do_not_accumulate_across_cycles()
    {
        var docker = WedgedHost();

        await RunCyclesAsync(Monitor(docker));
        await Task.Delay(WedgedHostDelay);

        // Two containers, so two in flight is the honest ceiling for a healthy scan. Anything above that is
        // work the monitor started and no longer waits for — the part that reached the server anyway.
        Assert.True(docker.PeakTotalInFlight <= 2,
            $"peak {docker.PeakTotalInFlight} concurrent log requests across 2 containers over {Cycles} cycles " +
            "— abandoned fetches are piling up, which is what put dockerd at 184% CPU");
    }
}
