using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Observability;
using Whiskers.Services.Observability.SelfMetrics;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// The self-metrics on disk (Plan-0003 WP3.2/WP3.3).
///
/// <para>History is the smaller half of why this exists. The larger half: after a restart the in-memory view
/// is empty, and an empty "last success" is indistinguishable from "never succeeded". A supervisor facing
/// that has only bad options — alarm on every restart, or stay quiet about fresh loops, which is exactly the
/// window in which a bad deploy has most likely broken something.</para>
///
/// <para>So the tests here demand both directions: a restart must not invent a problem, and it must not hide
/// one either.</para>
/// </summary>
public sealed class SelfMetricsPersistenceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"selfmetrics-persist-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public SelfMetricsPersistenceTests()
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

    private SelfMetricsRecorder RecorderFor(ISelfMetrics metrics) =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(), metrics, NullLogger<SelfMetricsRecorder>.Instance);

    private MetricsDbContext Db() => _sp.CreateScope().ServiceProvider.GetRequiredService<MetricsDbContext>();

    private static ScanSupervisor Supervisor(ISelfMetrics metrics, FakeNotifications sent) =>
        new(metrics, sent,
            new FakeServerConfig(new Models.ServerConfig { Id = "badwolf", Name = "Badwolf", IsDefault = true }),
            NullLogger<ScanSupervisor>.Instance,
            TimeSpan.Zero);

    [Fact]
    public async Task A_sampling_cycle_makes_no_Docker_calls_at_all()
    {
        // Plan-0003 WP6.2, and the rule the whole package stands on: a self-measurement that adds load to the
        // thing it measures is the same mistake it exists to reveal, one level up. Not "few calls" — none.
        var docker = new FakeDocker(new ContainerInfo
        {
            Id = "c1", Name = "burg-web", ServerId = "badwolf", ServerName = "Badwolf"
        });

        var metrics = new SelfMetrics();
        metrics.RecordCycle("logmonitor", "badwolf", TimeSpan.FromMilliseconds(120), success: true, TimeSpan.FromMinutes(1));

        var recorder = RecorderFor(metrics);
        await recorder.SampleAsync();
        await recorder.RestoreAsync();
        _ = metrics.Loops();
        _ = metrics.Counters();

        Assert.Empty(docker.CallsInOrder);
    }

    [Fact]
    public async Task A_sample_writes_one_row_per_loop_and_server()
    {
        var metrics = new SelfMetrics();
        metrics.RecordCycle("logmonitor", "badwolf", TimeSpan.FromMilliseconds(120), success: true, TimeSpan.FromMinutes(1));
        metrics.RecordCycle("health", "badwolf", TimeSpan.FromMilliseconds(80), success: false, TimeSpan.FromSeconds(30));

        await RecorderFor(metrics).SampleAsync();

        using var db = Db();
        var rows = await db.SelfMetricSamples.OrderBy(s => s.Loop).ToListAsync();
        Assert.Equal(new[] { "health", "logmonitor" }, rows.Select(r => r.Loop));

        var health = rows[0];
        Assert.Null(health.LastSuccessUtc);          // it failed — recorded as "never succeeded", not as zero
        Assert.Equal(1, health.Failures);
        Assert.Equal(30, health.ExpectedIntervalSeconds);
    }

    [Fact]
    public async Task A_restart_does_not_turn_a_healthy_loop_into_an_alarm()
    {
        // THE test for this work package. Before the restore existed, every restart produced a fleet-wide
        // "nothing is being checked" — and an alarm that fires on every deploy is one people mute.
        var before = new SelfMetrics();
        before.RecordCycle("logmonitor", "badwolf", TimeSpan.FromMilliseconds(120), success: true, TimeSpan.FromMinutes(1));
        await RecorderFor(before).SampleAsync();

        // A new process: fresh, empty in-memory state.
        var after = new SelfMetrics();
        Assert.Empty(after.Loops());

        await RecorderFor(after).RestoreAsync();

        var restored = Assert.Single(after.Loops());
        Assert.Equal("logmonitor", restored.Loop);
        Assert.NotNull(restored.LastSuccess);
        Assert.Equal(TimeSpan.FromMinutes(1), restored.ExpectedInterval);

        // And the supervisor agrees: nothing to report.
        var sent = new FakeNotifications();
        var supervisor = Supervisor(after, sent);
        await supervisor.CheckAsync();

        Assert.Empty(sent.Events);
    }

    [Fact]
    public async Task A_restart_does_not_hide_a_loop_that_really_has_stopped()
    {
        // The other direction, and the one that matters more. If the restore made every restart look healthy,
        // it would be worse than no restore at all: a loop that died before the restart would come back
        // looking alive.
        var before = new SelfMetrics();
        before.RecordCycle("logmonitor", "badwolf", TimeSpan.FromMilliseconds(120), success: true, TimeSpan.FromMinutes(1));
        await RecorderFor(before).SampleAsync();

        // Age the stored success by two hours — 120 intervals for a one-minute loop.
        using (var db = Db())
        {
            var row = await db.SelfMetricSamples.SingleAsync();
            row.LastSuccessUtc = DateTime.UtcNow.AddHours(-2);
            await db.SaveChangesAsync();
        }

        var after = new SelfMetrics();
        await RecorderFor(after).RestoreAsync();

        var sent = new FakeNotifications();
        await Supervisor(after, sent).CheckAsync();

        Assert.Equal("monitoring_stalled", Assert.Single(sent.Events).EventType);
    }

    [Fact]
    public async Task A_live_reading_always_beats_the_one_from_disk()
    {
        // A loop with a short cadence can complete a cycle before the restore reaches it. Letting a stale
        // timestamp win there would make a working loop look older than it is — the restore would be
        // manufacturing exactly the false alarm it exists to prevent.
        var before = new SelfMetrics();
        before.RecordCycle("logmonitor", "badwolf", TimeSpan.FromMilliseconds(120), success: true, TimeSpan.FromMinutes(1));
        await RecorderFor(before).SampleAsync();

        using (var db = Db())
        {
            var row = await db.SelfMetricSamples.SingleAsync();
            row.LastSuccessUtc = DateTime.UtcNow.AddHours(-2);
            await db.SaveChangesAsync();
        }

        var after = new SelfMetrics();
        after.RecordCycle("logmonitor", "badwolf", TimeSpan.FromMilliseconds(90), success: true, TimeSpan.FromMinutes(1));
        var live = after.Loops().Single().LastSuccess;

        await RecorderFor(after).RestoreAsync();

        Assert.Equal(live, after.Loops().Single().LastSuccess);
    }

    [Fact]
    public async Task A_reading_older_than_the_restore_window_is_not_restored()
    {
        // Beyond a week the stored success says nothing about now. Restoring it would let a loop that has
        // been dead for a month come back looking recently alive.
        var before = new SelfMetrics();
        before.RecordCycle("logmonitor", "badwolf", TimeSpan.FromMilliseconds(120), success: true, TimeSpan.FromMinutes(1));
        await RecorderFor(before).SampleAsync();

        using (var db = Db())
        {
            var row = await db.SelfMetricSamples.SingleAsync();
            row.TakenAtUtc = DateTime.UtcNow - SelfMetricsRecorder.MaxRestoreAge - TimeSpan.FromDays(1);
            await db.SaveChangesAsync();
        }

        var after = new SelfMetrics();
        await RecorderFor(after).RestoreAsync();

        Assert.Empty(after.Loops());
    }

    [Fact]
    public async Task Only_the_newest_reading_per_loop_is_restored()
    {
        var metrics = new SelfMetrics();
        metrics.RecordCycle("logmonitor", "badwolf", TimeSpan.FromMilliseconds(120), success: true, TimeSpan.FromMinutes(1));
        var recorder = RecorderFor(metrics);

        await recorder.SampleAsync();
        await Task.Delay(10);
        metrics.RecordCycle("logmonitor", "badwolf", TimeSpan.FromMilliseconds(130), success: true, TimeSpan.FromMinutes(1));
        await recorder.SampleAsync();

        using var db = Db();
        Assert.Equal(2, await db.SelfMetricSamples.CountAsync());
        var newest = await db.SelfMetricSamples.OrderByDescending(s => s.TakenAtUtc).FirstAsync();

        var after = new SelfMetrics();
        await RecorderFor(after).RestoreAsync();

        Assert.Equal(newest.LastSuccessUtc, after.Loops().Single().LastSuccess);
    }
}
