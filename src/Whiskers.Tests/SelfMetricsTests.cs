using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.LogMonitor;
using Whiskers.Services.Observability.SelfMetrics;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// What Whiskers knows about itself (Plan-0003).
///
/// <para>A metric that never moves and a metric that is not wired up look identical from the outside, and the
/// second is the normal outcome of adding one. So these tests do not check that a series exists — they check
/// that it changes by the right amount when the thing it counts happens.</para>
/// </summary>
public sealed class SelfMetricsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"selfmetrics-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public SelfMetricsTests()
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

    // --- the type itself ---------------------------------------------------------------------------------

    [Fact]
    public void A_counter_moves_by_exactly_one()
    {
        var metrics = new SelfMetrics();

        metrics.Count("log_fetch_timeouts", "badwolf");
        metrics.Count("log_fetch_timeouts", "badwolf");
        metrics.Count("log_fetch_timeouts", "burgcloud");

        var counter = metrics.Counters()["log_fetch_timeouts"];
        Assert.Equal(2, counter["badwolf"]);
        Assert.Equal(1, counter["burgcloud"]);
    }

    [Fact]
    public void A_failed_cycle_does_not_move_the_last_success()
    {
        var metrics = new SelfMetrics();

        metrics.RecordCycle("logmonitor", "badwolf", TimeSpan.FromSeconds(1), success: true);
        var afterSuccess = metrics.Loops().Single().LastSuccess;

        Thread.Sleep(20);
        metrics.RecordCycle("logmonitor", "badwolf", TimeSpan.FromSeconds(1), success: false);

        var loop = metrics.Loops().Single();
        Assert.Equal(afterSuccess, loop.LastSuccess);   // the point: failures must not refresh it
        Assert.Equal(1, loop.Failures);
        Assert.True(loop.LastAttempt > loop.LastSuccess);
    }

    [Fact]
    public void A_skipped_server_still_appears_with_its_reason()
    {
        // The failure this prevents: four loops skip Kubernetes servers entirely, so those servers produce no
        // metrics at all — and "this loop never runs here" reads exactly like "this loop found nothing".
        var metrics = new SelfMetrics();

        metrics.RecordSkip("cve", "k3s-cluster", "Kubernetes server, Docker-only loop");

        var loop = Assert.Single(metrics.Loops());
        Assert.Equal("k3s-cluster", loop.ServerId);
        Assert.Null(loop.LastSuccess);
        Assert.Equal("Kubernetes server, Docker-only loop", loop.SkipReason);
    }

    [Fact]
    public void Every_docker_only_loop_marks_the_kubernetes_servers_it_steps_over()
    {
        // The concrete blind spot from PRD-0003: four loops filter Kubernetes servers out, and until now they
        // did it silently. A K8s host therefore produced no health, metric, CVE or update data at all — which
        // on a dashboard is indistinguishable from a host with nothing wrong.
        var metrics = new SelfMetrics();
        var fleet = new[]
        {
            new ServerConfig { Id = "badwolf", Name = "Badwolf", ConnectionType = ConnectionType.Local },
            new ServerConfig { Id = "k3s", Name = "k3s cluster", ConnectionType = ConnectionType.Kubernetes }
        };

        foreach (var loop in new[]
                 {
                     SelfMetricsFleetExtensions.Loops.Health,
                     SelfMetricsFleetExtensions.Loops.Metrics,
                     SelfMetricsFleetExtensions.Loops.Cve,
                     SelfMetricsFleetExtensions.Loops.ImageUpdate
                 })
        {
            metrics.RecordKubernetesSkips(loop, fleet);
        }

        var skipped = metrics.Loops().Where(l => l.ServerId == "k3s").ToList();
        Assert.Equal(4, skipped.Count);
        Assert.All(skipped, l => Assert.Contains("Kubernetes", l.SkipReason));

        // And the Docker host must NOT be marked as skipped — a false skip would hide a real gap.
        Assert.DoesNotContain(metrics.Loops(), l => l.ServerId == "badwolf");
    }

    // --- wired into the real loop ------------------------------------------------------------------------

    private LogMonitorService Monitor(FakeDocker docker, ISelfMetrics metrics, TimeSpan? timeout = null) =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(),
            docker,
            new FakeServerConfig(new ServerConfig { Id = "local", Name = "Badwolf", ConnectionType = ConnectionType.Local, IsDefault = true }),
            new FakeNotifications(),
            NullLogger<LogMonitorService>.Instance,
            TestBudget.Create(),
            metrics,
            new NoExclusions(), new NoOutcomes(),
            timeout);

    [Fact]
    public async Task A_healthy_scan_records_a_recent_success()
    {
        var metrics = new SelfMetrics();
        var docker = new FakeDocker(
            new ContainerInfo { Id = "c-ok", Name = "burg-web", Image = "img:1", ServerId = "local", ServerName = "Badwolf" });

        await Monitor(docker, metrics).RunScanCycleAsync(CancellationToken.None);

        var loop = Assert.Single(metrics.Loops());
        Assert.Equal("logmonitor", loop.Loop);
        Assert.Equal("local", loop.ServerId);
        Assert.NotNull(loop.LastSuccess);
        Assert.True(DateTime.UtcNow - loop.LastSuccess!.Value < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_timeout_that_went_uncounted_for_six_days_is_counted()
    {
        var metrics = new SelfMetrics();
        var docker = new FakeDocker(
            new ContainerInfo { Id = "c-tunnel", Name = "ghostunnel", Image = "img:1", ServerId = "local", ServerName = "Badwolf" })
        {
            // Hangs rather than answering late: a finite delay raced the 40 ms timeout, and on a starved CI
            // runner both timers came due together and the fetch could "succeed" (CI run 38304968400).
            FetchHangs = true
        };

        await Monitor(docker, metrics, TimeSpan.FromMilliseconds(40)).RunScanCycleAsync(CancellationToken.None);

        Assert.Equal(1, metrics.Counters()["log_fetch_timeouts"]["local"]);
    }

    [Fact]
    public async Task Collecting_the_numbers_costs_no_docker_calls()
    {
        // A self-measurement that talks to the daemon would be the incident one level up.
        var metrics = new SelfMetrics();
        var docker = new FakeDocker(
            new ContainerInfo { Id = "c-ok", Name = "burg-web", Image = "img:1", ServerId = "local", ServerName = "Badwolf" });

        await Monitor(docker, metrics).RunScanCycleAsync(CancellationToken.None);
        var callsAfterScan = docker.Calls.Count;

        for (var i = 0; i < 50; i++)
        {
            _ = metrics.Loops();
            _ = metrics.Counters();
        }

        Assert.Equal(callsAfterScan, docker.Calls.Count);
    }
}
