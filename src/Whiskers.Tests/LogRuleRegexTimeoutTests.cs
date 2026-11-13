using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.LogMonitor;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// Fable review (Medium): a log-alert regex that runs into its match timeout must cost only its own rule. The timeout
/// used to escape to the container's catch-all, which skipped every other rule of that container without a word.
/// </summary>
public sealed class LogRuleRegexTimeoutTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"logmon-rx-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public LogRuleRegexTimeoutTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.Database.EnsureCreated();
        // The slow rule first, so a timeout that escapes the rule would also take the plain rule after it.
        db.LogAlertRules.Add(new LogAlertRuleEntity { Name = "slow", Pattern = "^(a+)+$", IsRegex = true, Severity = "warning" });
        db.LogAlertRules.Add(new LogAlertRuleEntity { Name = "fatal", Pattern = "FATAL", Severity = "error" });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _sp.Dispose();
        TestSqlite.ClearPool(_dbPath);
        try { File.Delete(_dbPath); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task A_regex_that_times_out_skips_only_its_own_rule()
    {
        var docker = new FakeDocker(
            new ContainerInfo { Id = "c-app", Name = "app", Image = "img:1", ServerId = "local", ServerName = "Badwolf" });
        docker.Logs["local/c-app"] = new string('a', 4000) + "! FATAL disk full";
        var notifications = new FakeNotifications();
        var metrics = new Whiskers.Services.Observability.SelfMetrics.SelfMetrics();
        var monitor = new LogMonitorService(_sp.GetRequiredService<IServiceScopeFactory>(), docker,
            new FakeServerConfig(new ServerConfig { Id = "local", Name = "Badwolf", ConnectionType = ConnectionType.Local, IsDefault = true }),
            notifications, NullLogger<LogMonitorService>.Instance,
            TestBudget.Create(), metrics, new NoExclusions(), new NoOutcomes(), TimeSpan.FromSeconds(5))
        {
            RegexTimeout = TimeSpan.FromMilliseconds(5),
        };

        await monitor.RunScanCycleAsync(CancellationToken.None);

        var alert = Assert.Single(notifications.Events, e => e.EventType == Whiskers.Services.Notifications.NotificationEventTypes.LogAlert("error"));
        Assert.StartsWith("fatal ·", alert.ImageInfo);
    }
}
