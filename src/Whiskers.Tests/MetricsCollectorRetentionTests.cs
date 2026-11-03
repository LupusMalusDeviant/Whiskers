using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services;
using Whiskers.Services.Metrics;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-55 (R1-41 leftovers): the collector's retention runs on the settings the cycle read. An app-settings.json whose
/// Metrics section does not bind used to throw from the hourly prune every hour, so nothing was ever pruned; and a first
/// read that failed threw before the loop had started. Every cycle here fails its collection on purpose (no Docker
/// service) — retention runs regardless, in its own step.
/// </summary>
public sealed class MetricsCollectorRetentionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sw-r155-col-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestSqlite.ClearPool(_path);
        try { File.Delete(_path); } catch { /* best effort */ }
    }

    /// <summary>Reads succeed <paramref name="okReads"/> times, then throw as a binder error on "30s" does.</summary>
    private sealed class FlakyMonitor(int okReads) : IOptionsMonitor<MetricsSettings>
    {
        private int _reads;
        public MetricsSettings CurrentValue => Interlocked.Increment(ref _reads) <= okReads
            ? new MetricsSettings { Enabled = true }
            : throw new InvalidOperationException("Failed to convert configuration value '30s' to type 'System.Int32'.");
        public MetricsSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<MetricsSettings, string?> listener) => null;
    }

    private sealed class FixedRetention(MetricRetention retention) : IMetricRetentionService
    {
        public MetricRetention Current => retention;
        public IReadOnlyList<MetricRetentionSetting> Settings => [];
        public Task SaveAsync(MetricRetention values, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private (MetricsCollectorService Collector, NotificationDeliveryTests.ListLoggerProvider Logs, ServiceProvider Services) Collector(int okReads)
    {
        var services = new ServiceCollection();
        services.AddOptions<MetricAlertSettings>();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_path}"));
        services.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(new FakeServerConfig(
            new ServerConfig { Id = "local", Name = "Badwolf", ConnectionType = ConnectionType.Local, IsDefault = true }));
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope()) scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();

        var logs = new NotificationDeliveryTests.ListLoggerProvider();
        var loggers = new Microsoft.Extensions.Logging.LoggerFactory([logs]);
        var interval = new EffectiveInterval<MetricsSettings>(() => 1, "Metrics:CollectionIntervalSeconds", 1, 10,
            TimeSpan.FromSeconds(1), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var collector = new MetricsCollectorService(sp,
            sp.GetRequiredService<IOptionsMonitor<MetricAlertSettings>>(),
            new Whiskers.Services.Metrics.HostLoad.HostLoadEvaluator(), new Whiskers.Services.Metrics.HostLoad.ApiLatencyEvaluator(),
            new Whiskers.Services.Metrics.HostLoad.RollingBaseline(), new FlakyMonitor(okReads),
            new Microsoft.Extensions.Logging.Logger<MetricsCollectorService>(loggers),
            new Whiskers.Services.Observability.SelfMetrics.SelfMetrics(), interval,
            new FixedRetention(MetricRetention.Default with { RawDays = 1, AlertHistoryDays = 1 }),
            new MetricRollupService(new Microsoft.Extensions.Logging.Logger<MetricRollupService>(loggers)))
        {
            StartupDelay = TimeSpan.Zero,
            StartupOffsetForTests = TimeSpan.Zero,
        };
        return (collector, logs, sp);
    }

    private static async Task WaitFor(Func<bool> done, NotificationDeliveryTests.ListLoggerProvider logs)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(done(), "timed out; log: " + string.Join(" / ", logs.Lines));
    }

    [Fact]
    public async Task A_clock_set_back_while_running_still_rolls_up_with_its_warning_and_still_prunes()
    {
        // Review round 2: the schedules were set under the clock of the previous run; set back, real time would stay
        // below them for the whole episode — no warning, no hourly retention.
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var (collector, logs, sp) = Collector(okReads: 1);
        using (sp)
        {
            using (var scope = sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
                for (var t = t0; t <= t0.AddHours(2); t = t.AddSeconds(30))
                    db.ContainerMetrics.Add(new ContainerMetricEntity { ContainerId = "c1", ServerId = "local", Timestamp = t, CpuPercent = 5 });
                await db.SaveChangesAsync();
            }
            var retention = MetricRetention.Default with { RawDays = 1, AlertHistoryDays = 1 };
            await collector.MaintainAsync(t0.AddHours(2).AddMinutes(1), retention, default);   // rolled up, pruned

            // The clock is set back by an hour; an alert older than its retention arrives meanwhile.
            var setBack = t0.AddHours(1);
            using (var scope = sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
                db.AlertHistory.Add(new AlertHistoryEntity { AlertType = "high_cpu", Timestamp = setBack.AddDays(-3) });
                await db.SaveChangesAsync();
            }
            await collector.MaintainAsync(setBack, retention, default);

            Assert.Single(logs.Lines, l => l.Contains("ahead of the clock"));
            using (var scope = sp.CreateScope())
                Assert.False(scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AlertHistory.Any(), "the hourly retention ran");
        }
    }

    [Fact]
    public async Task Retention_runs_while_the_metrics_section_does_not_bind()
    {
        var (collector, logs, sp) = Collector(okReads: 1);   // the start reads it, every cycle after that fails
        using (sp)
        {
            await using (var db = sp.CreateScope().ServiceProvider.GetRequiredService<MetricsDbContext>())
            {
                var old = DateTime.UtcNow.AddDays(-3);
                db.ContainerMetrics.Add(new ContainerMetricEntity { ContainerId = "c1", ServerId = "local", Timestamp = old, CpuPercent = 1 });
                db.AlertHistory.Add(new AlertHistoryEntity { AlertType = "high_cpu", Timestamp = old });
                await db.SaveChangesAsync();
            }
            bool Pruned()
            {
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
                return !db.ContainerMetrics.Any() && !db.AlertHistory.Any();
            }

            await collector.StartAsync(CancellationToken.None);
            await WaitFor(Pruned, logs);

            Assert.False(collector.ExecuteTask!.IsCompleted, "the loop is still running");
            Assert.Single(logs.Lines, l => l.Contains("Metrics settings cannot be read") && l.Contains("last valid"));
            using (var scope = sp.CreateScope())   // rolled up before it was pruned
                Assert.Equal(1, scope.ServiceProvider.GetRequiredService<MetricsDbContext>().ContainerMetricRollups.Single(r => r.TierMinutes == 10).SampleCount);
            await collector.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_first_read_that_fails_starts_the_loop_on_the_defaults()
    {
        var (collector, logs, sp) = Collector(okReads: 0);
        using (sp)
        {
            int Cycles() => logs.Lines.Count(l => l.StartsWith("Metrics collection failed", StringComparison.Ordinal));

            await collector.StartAsync(CancellationToken.None);
            await WaitFor(() => Cycles() >= 2, logs);

            Assert.False(collector.ExecuteTask!.IsCompleted, "the loop is still running");
            Assert.Single(logs.Lines, l => l.Contains("Metrics settings cannot be read") && l.Contains("using the defaults"));
            await collector.StopAsync(CancellationToken.None);
        }
    }
}
