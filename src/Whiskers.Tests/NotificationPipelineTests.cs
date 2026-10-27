using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>The two things every notification now passes through centrally: the per-container mute
/// preferences (which only the health monitor used to honour, so muting a container still let its log
/// alerts, CVE findings and image updates through) and the persisted alert history (a table that existed
/// from the first migration but that nothing ever wrote to).</summary>
public sealed class NotificationPipelineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"notif-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;
    private readonly RecordingChannel _channel = new();
    private readonly StubPrefs _prefs = new();

    public NotificationPipelineTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<IAiTriggerDispatcher, NoopDispatcher>();
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    private CompositeNotificationService Service() => new(
        new[] { _channel }, new NullInAppStore(), _prefs, _sp,
        NullLogger<CompositeNotificationService>.Instance);

    private List<AlertHistoryEntity> History()
    {
        using var scope = _sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MetricsDbContext>()
            .AlertHistory.AsNoTracking().OrderBy(a => a.Id).ToList();
    }

    // --- mute preferences -------------------------------------------------------------------------------

    [Fact]
    public async Task A_muted_container_is_silenced_for_EVERY_producer()
    {
        _prefs.Muted.Add("noisy");

        await Service().SendAsync(new NotificationEvent
        {
            ContainerName = "noisy",
            EventType = "log_alert:error"   // not a health-monitor event — used to bypass the mute
        });

        Assert.Empty(_channel.Sent);
        Assert.Empty(History());
    }

    [Theory]
    [InlineData("automation_suspended")]
    [InlineData("automation_refused")]
    public async Task A_muted_target_never_hides_that_its_automation_was_suspended_or_refused(string eventType)
    {
        // R1-04: the admin must hear that a task stopped, even when its container's own alerts are muted.
        _prefs.MuteEverything = true;

        await Service().SendAsync(new NotificationEvent { ContainerName = "noisy", EventType = eventType });

        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task An_unmuted_container_still_gets_through()
    {
        await Service().SendAsync(new NotificationEvent { ContainerName = "web", EventType = "log_alert:error" });
        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task Server_level_events_are_not_affected_by_container_mutes()
    {
        // They carry no container name, so there is nothing to mute them by — a fleet outage must never be
        // swallowed because someone once muted a container.
        _prefs.MuteEverything = true;

        await Service().SendAsync(new NotificationEvent
        {
            EventType = "server_unreachable",
            ServerId = "rabenhof",
            ServerName = "Rabenhof",
            ImageInfo = "Rabenhof — Connection failed"
        });

        Assert.Single(_channel.Sent);
    }

    // --- alert history ----------------------------------------------------------------------------------

    [Fact]
    public async Task Every_delivered_event_lands_in_the_alert_history()
    {
        await Service().SendAsync(new NotificationEvent
        {
            ContainerName = "web",
            ContainerId = "abc123",
            ServerId = "rabenhof",
            ServerName = "Rabenhof",
            EventType = "unhealthy"
        });

        var row = Assert.Single(History());
        Assert.Equal("rabenhof", row.ServerId);      // the column that makes the history fleet-aware
        Assert.Equal("web", row.ContainerName);
        Assert.Equal("unhealthy", row.AlertType);
        Assert.Contains("Container unhealthy", row.Message);
        Assert.False(row.Resolved);
    }

    [Fact]
    public async Task A_recovery_closes_the_outage_it_ends()
    {
        var svc = Service();
        await svc.SendAsync(new NotificationEvent { EventType = "server_unreachable", ServerId = "rabenhof", ServerName = "Rabenhof" });
        await svc.SendAsync(new NotificationEvent { EventType = "server_recovered", ServerId = "rabenhof", ServerName = "Rabenhof" });

        var rows = History();
        Assert.True(rows.Single(r => r.AlertType == "server_unreachable").Resolved);
    }

    [Fact]
    public async Task A_recovery_does_not_close_another_servers_outage()
    {
        var svc = Service();
        await svc.SendAsync(new NotificationEvent { EventType = "server_unreachable", ServerId = "rabenhof" });
        await svc.SendAsync(new NotificationEvent { EventType = "server_recovered", ServerId = "burgcloud" });

        Assert.False(History().Single(r => r.AlertType == "server_unreachable").Resolved);
    }

    [Fact]
    public async Task A_broken_history_write_never_swallows_the_notification()
    {
        // Delivery matters more than bookkeeping: with no database reachable the channels must still fire.
        var brokenSp = new ServiceCollection()
            .AddSingleton<IAiTriggerDispatcher, NoopDispatcher>()
            .BuildServiceProvider();   // no MetricsDbContext registered

        var svc = new CompositeNotificationService(new[] { _channel }, new NullInAppStore(), _prefs, brokenSp,
            NullLogger<CompositeNotificationService>.Instance);

        await svc.SendAsync(new NotificationEvent { ContainerName = "web", EventType = "stopped" });
        Assert.Single(_channel.Sent);
    }

    // --- doubles ----------------------------------------------------------------------------------------

    private sealed class RecordingChannel : INotificationChannel
    {
        public List<NotificationEvent> Sent { get; } = new();
        public string Name => "Recording";
        public Task SendAsync(NotificationEvent evt) { Sent.Add(evt); return Task.CompletedTask; }
        public Task SendTestAsync() => Task.CompletedTask;
    }

    private sealed class StubPrefs : IContainerNotificationPrefsService
    {
        public HashSet<string> Muted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool MuteEverything { get; set; }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ContainerNotifEntry GetPrefs(string containerName) => new();
        public bool ShouldNotify(string containerName, string eventType) => !MuteEverything && !Muted.Contains(containerName);
        public Task SavePrefsAsync(string containerName, ContainerNotifEntry entry) => Task.CompletedTask;
    }

    private sealed class NoopDispatcher : IAiTriggerDispatcher
    {
        public Task OnEventAsync(NotificationEvent evt) => Task.CompletedTask;
    }

    private sealed class NullInAppStore : IInAppNotificationStore
    {
        public IReadOnlyList<InAppNotification> Recent => Array.Empty<InAppNotification>();
        public int UnreadCount => 0;
        public void Add(NotificationEvent evt) { }
        public void MarkAllRead() { }
        public void Clear() { }
        public event Action? Changed { add { } remove { } }
        public event Action<InAppNotification>? Added { add { } remove { } }
        public Task<List<InAppNotification>> QueryAsync(string? severity, string? eventType, int skip, int take, CancellationToken ct = default)
            => Task.FromResult(new List<InAppNotification>());
        public Task<int> CountAsync(string? severity, string? eventType, CancellationToken ct = default) => Task.FromResult(0);
    }
}
