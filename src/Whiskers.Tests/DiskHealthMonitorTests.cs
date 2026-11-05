using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Metrics;
using Whiskers.Services.Metrics.DiskHealth;
using Whiskers.Services.Notifications;
using static Whiskers.Tests.DiskHealthFixtures;

namespace Whiskers.Tests;

/// <summary>
/// R1-58 (contract 0005): the monitor queries nothing while the feature is off, isolates a failing host to itself and its
/// cycle, honours the caller's cancellation, never queries a server without a metrics endpoint (Unsupported), delivers
/// the alerts through the notification pipeline and keeps its state across a restart.
/// </summary>
public sealed class DiskHealthMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-r158-mon-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ServerConfig Server(string id, string? endpoint) => new()
    {
        Id = id, Name = id[..1].ToUpperInvariant() + id[1..], Enabled = true, ConnectionType = ConnectionType.Local,
        MetricsSource = endpoint is null ? MetricsSourceKind.Docker : MetricsSourceKind.Prometheus, MetricsEndpoint = endpoint,
    };

    private sealed class Switch(bool on) : IDiskHealthSettingsService
    {
        public bool Enabled { get; set; } = on;
        public bool ReadOnly => false;
        public string? Source => null;
        public event Action<bool>? Changed;
        public Task SaveAsync(bool enabled, CancellationToken ct = default)
        {
            var changed = enabled != Enabled;
            Enabled = enabled;
            if (changed) Changed?.Invoke(enabled);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingFirst : INotificationService
    {
        public List<NotificationEvent> Events { get; } = [];
        private int _calls;
        public Task SendAsync(NotificationEvent evt) => SendAsync(evt, CancellationToken.None);
        public Task SendTestAsync() => Task.CompletedTask;
        public Task SendAsync(NotificationEvent evt, CancellationToken ct)
        {
            if (_calls++ == 0) throw new InvalidOperationException("the channel is down");
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Switching_off_forgets_at_once_without_waiting_for_a_cycle()
    {
        var client = StubPromClient.From(_ => Queries(smart: SmartFailed));
        var rig = Make(client, [Server("badwolf", "http://tsdb:8428")]);
        for (var i = 0; i < 3; i++) { await rig.Monitor.RunCycleAsync(CancellationToken.None); rig.Clock.Utc += DiskHealthMonitor.Interval; }
        Assert.Single(rig.Notifications.Events);

        await rig.Setting.SaveAsync(false);
        await rig.Monitor.SwitchedOff.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(rig.Readings.All());
        Assert.Equal("disk_failing_recovered", rig.Notifications.Events[^1].EventType);
        Assert.True(rig.Notifications.Events[^1].RecoveryUnconfirmed);
        // Switched on again before the next cycle: nothing of the old state is left.
        await rig.Setting.SaveAsync(true);
        Assert.Empty(rig.Monitor.Alerts.OpenFindings());
    }

    [Fact]
    public async Task One_failed_send_does_not_lose_the_next_finding_of_the_same_reading()
    {
        var client = StubPromClient.From(_ => Queries(smart: SmartFailed, md: MdFailedDisk));
        var notify = new FailingFirst();
        var clock = new DiskHealthClock(Now);
        var monitor = new DiskHealthMonitor(new FakeServerConfig(Server("badwolf", "http://tsdb:8428")), client, new Switch(true),
            new DiskHealthService(), notify, NullLogger<DiskHealthMonitor>.Instance, null, clock);

        for (var i = 0; i < 3; i++) { await monitor.RunCycleAsync(CancellationToken.None); clock.Utc += DiskHealthMonitor.Interval; }

        Assert.Single(notify.Events);          // disk_failing's send failed; array_degraded still went out
        Assert.Equal(NotificationEventTypes.ArrayDegraded, notify.Events[0].EventType);
    }

    [Fact]
    public async Task A_first_read_whose_queries_all_fail_is_not_read_yet_not_unsupported()
    {
        var client = new StubPromClient((_, _, _) => Task.FromResult(PromQueryResult.Failed("the metrics endpoint could not be reached (ConnectionError)")));
        var rig = Make(client, [Server("badwolf", "http://tsdb:8428")]);

        await rig.Monitor.RunCycleAsync(CancellationToken.None);

        var reading = rig.Readings.Latest("badwolf")!;
        Assert.Equal(DiskHealthState.Unsupported, reading.Overall);
        Assert.StartsWith("not read yet: query failed", Assert.Single(reading.Items).Reason);
    }

    private sealed record Rig(DiskHealthMonitor Monitor, DiskHealthService Readings, FakeNotifications Notifications,
        Switch Setting, DiskHealthClock Clock);

    private static Rig Make(IPrometheusQueryClient client, ServerConfig[] servers, bool enabled = true, IDiskHealthStateStore? state = null)
    {
        var readings = new DiskHealthService();
        var notifications = new FakeNotifications();
        var setting = new Switch(enabled);
        var clock = new DiskHealthClock(Now);
        var monitor = new DiskHealthMonitor(new FakeServerConfig(servers), client, setting, readings, notifications,
            NullLogger<DiskHealthMonitor>.Instance, state, clock);
        return new Rig(monitor, readings, notifications, setting, clock);
    }

    private static StubPromClient Good() => StubPromClient.From(_ => Queries());

    [Fact]
    public async Task Off_queries_nothing_and_shows_nothing()
    {
        var client = Good();
        var rig = Make(client, [Server("badwolf", "http://tsdb:8428")], enabled: false);

        await rig.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.Empty(client.Calls);
        Assert.Empty(rig.Readings.All());
    }

    [Fact]
    public async Task On_reads_each_host_with_its_six_queries_on_its_own_endpoint()
    {
        var client = Good();
        var rig = Make(client, [Server("badwolf", "http://tsdb:8428"), Server("zirkus", "http://tsdb:8428/")]);

        await rig.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.Equal(12, client.Calls.Count);
        Assert.Equal(6, client.Calls.Count(c => c.PromQl.Contains("server=\"zirkus\"")));
        Assert.Equal(DiskHealthState.Healthy, rig.Readings.Latest("badwolf")!.Overall);
        Assert.Equal(DiskHealthState.Healthy, rig.Readings.Latest("zirkus")!.Overall);
    }

    [Fact]
    public async Task A_server_without_a_metrics_endpoint_is_Unsupported_and_never_queried()
    {
        var client = Good();
        var rig = Make(client, [Server("docker", null)]);

        await rig.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.Empty(client.Calls);
        Assert.Equal(DiskHealthState.Unsupported, rig.Readings.Latest("docker")!.Overall);
    }

    [Fact]
    public async Task A_failing_host_is_isolated_to_itself_and_its_cycle()
    {
        var failing = true;
        var client = new StubPromClient((endpoint, promql, ct) =>
        {
            if (endpoint.Contains("broken") && failing) throw new InvalidOperationException("the handler blew up");
            return Good().QueryAsync(endpoint, promql, ct);
        });
        var rig = Make(client, [Server("broken", "http://broken:8428"), Server("badwolf", "http://tsdb:8428")]);

        await rig.Monitor.RunCycleAsync(CancellationToken.None);

        // Review round 1 (M1): a read that throws is a failed read — never read before, so "not read yet", never healthy.
        var broken = rig.Readings.Latest("broken")!;
        Assert.Equal(DiskHealthState.Unsupported, broken.Overall);
        Assert.StartsWith("not read yet: query failed", Assert.Single(broken.Items).Reason);
        Assert.Equal(DiskHealthState.Healthy, rig.Readings.Latest("badwolf")!.Overall);  // the other host is read

        failing = false;                                                              // the next cycle runs normally
        rig.Clock.Utc += DiskHealthMonitor.Interval;
        await rig.Monitor.RunCycleAsync(CancellationToken.None);
        Assert.Equal(DiskHealthState.Healthy, rig.Readings.Latest("broken")!.Overall);
    }

    [Fact]
    public async Task An_unreachable_endpoint_reads_Unknown_after_a_reading()
    {
        var reachable = true;
        var client = new StubPromClient((endpoint, promql, ct) => reachable
            ? Good().QueryAsync(endpoint, promql, ct)
            : Task.FromResult(PromQueryResult.Failed("the metrics endpoint could not be reached (ConnectionError)")));
        var rig = Make(client, [Server("badwolf", "http://tsdb:8428")]);

        await rig.Monitor.RunCycleAsync(CancellationToken.None);
        reachable = false;
        rig.Clock.Utc += DiskHealthMonitor.Interval;
        await rig.Monitor.RunCycleAsync(CancellationToken.None);

        var reading = rig.Readings.Latest("badwolf")!;
        Assert.Equal(DiskHealthState.Unknown, reading.Overall);
        Assert.All(reading.Items, i => Assert.StartsWith("query failed: ", i.Reason));
    }

    [Fact]
    public async Task The_callers_cancellation_reaches_the_queries_and_ends_the_cycle()
    {
        var sawStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubPromClient(async (_, _, ct) =>
        {
            ct.Register(() => sawStop.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return PromQueryResult.Of([]);
        });
        var rig = Make(client, [Server("badwolf", "http://tsdb:8428"), Server("zirkus", "http://tsdb:8428")]);
        using var stop = new CancellationTokenSource();

        var cycle = rig.Monitor.RunCycleAsync(stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();

        await sawStop.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(rig.Readings.All());                    // nothing published after the stop
        Assert.DoesNotContain(client.Calls, c => c.PromQl.Contains("zirkus"));
        Assert.Empty(rig.Notifications.Events);
    }

    [Fact]
    public async Task The_hosted_loop_stops_on_the_hosts_stop()
    {
        var client = Good();
        var rig = Make(client, [Server("badwolf", "http://tsdb:8428")]);
        var monitor = new DiskHealthMonitor(new FakeServerConfig(Server("badwolf", "http://tsdb:8428")), client, rig.Setting,
            rig.Readings, rig.Notifications, NullLogger<DiskHealthMonitor>.Instance) { StartupOffsetForTests = TimeSpan.Zero };

        await monitor.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100 && client.Calls.Count < 6; i++) await Task.Delay(20);
        await monitor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(6, client.Calls.Count);
        Assert.NotNull(rig.Readings.Latest("badwolf"));
    }

    [Fact]
    public async Task A_failing_disk_is_delivered_after_ten_minutes_and_closed_when_the_feature_is_switched_off()
    {
        var client = StubPromClient.From(_ => Queries(smart: SmartFailed));
        var rig = Make(client, [Server("badwolf", "http://tsdb:8428")]);

        for (var i = 0; i < 3; i++)
        {
            await rig.Monitor.RunCycleAsync(CancellationToken.None);
            rig.Clock.Utc += DiskHealthMonitor.Interval;
        }

        var raised = Assert.Single(rig.Notifications.Events);
        Assert.Equal(NotificationEventTypes.DiskFailing, raised.EventType);
        Assert.Equal("badwolf", raised.ServerId);
        Assert.Equal("Badwolf", raised.ServerName);

        rig.Setting.Enabled = false;
        await rig.Monitor.RunCycleAsync(CancellationToken.None);

        Assert.Equal(2, rig.Notifications.Events.Count);
        var closed = rig.Notifications.Events[1];
        Assert.Equal("disk_failing_recovered", closed.EventType);
        Assert.True(closed.RecoveryUnconfirmed);
        Assert.Empty(rig.Readings.All());
    }

    [Fact]
    public async Task A_removed_server_has_its_alerts_closed_and_its_reading_dropped()
    {
        var client = StubPromClient.From(_ => Queries(md: MdFailedDisk));
        var servers = new List<ServerConfig> { Server("badwolf", "http://tsdb:8428") };
        var config = new MutableServers(servers);
        var readings = new DiskHealthService();
        var notifications = new FakeNotifications();
        var clock = new DiskHealthClock(Now);
        var monitor = new DiskHealthMonitor(config, client, new Switch(true), readings, notifications,
            NullLogger<DiskHealthMonitor>.Instance, null, clock);
        for (var i = 0; i < 3; i++) { await monitor.RunCycleAsync(CancellationToken.None); clock.Utc += DiskHealthMonitor.Interval; }
        Assert.Single(notifications.Events);

        servers.Clear();
        await monitor.RunCycleAsync(CancellationToken.None);

        Assert.Equal("array_degraded_recovered", notifications.Events[^1].EventType);
        Assert.True(notifications.Events[^1].RecoveryUnconfirmed);
        Assert.Null(readings.Latest("badwolf"));
    }

    [Fact]
    public async Task The_last_readings_and_open_alerts_survive_a_restart()
    {
        var path = Path.Combine(_dir, "disk-health.json");
        var failing = StubPromClient.From(_ => Queries(smart: SmartFailed));
        var first = Make(failing, [Server("badwolf", "http://tsdb:8428")], state: new DiskHealthStateStore(path));
        for (var i = 0; i < 3; i++) { await first.Monitor.RunCycleAsync(CancellationToken.None); first.Clock.Utc += DiskHealthMonitor.Interval; }
        Assert.Single(first.Notifications.Events);

        // Restart: the endpoint does not answer yet. The disk was read before, so it reads Unknown — not Unsupported —
        // and the open alert is neither raised again nor closed.
        var unreachable = new StubPromClient((_, _, _) => Task.FromResult(PromQueryResult.Failed("the metrics endpoint could not be reached (ConnectionError)")));
        var second = Make(unreachable, [Server("badwolf", "http://tsdb:8428")], state: new DiskHealthStateStore(path));
        second.Clock.Utc = first.Clock.Utc;
        for (var i = 0; i < 3; i++) { await second.Monitor.RunCycleAsync(CancellationToken.None); second.Clock.Utc += DiskHealthMonitor.Interval; }

        Assert.Equal(DiskHealthState.Unknown, second.Readings.Latest("badwolf")!.Overall);
        Assert.DoesNotContain(second.Notifications.Events, e => e.EventType == NotificationEventTypes.DiskFailing);
        Assert.DoesNotContain(second.Notifications.Events, e => e.EventType == "disk_failing_recovered");
        Assert.Contains(second.Monitor.Alerts.OpenFindings(), f => f.Kind == NotificationEventTypes.DiskFailing);
        // The transition out of DegradedOrFailing (read before the restart) still raises disk_health_unknown.
        Assert.Contains(second.Notifications.Events, e => e.EventType == NotificationEventTypes.DiskHealthUnknown);
    }

    [Fact]
    public async Task A_host_that_read_Healthy_and_then_throws_reads_Unknown_and_raises_disk_health_unknown()
    {
        var throwing = false;
        var client = new StubPromClient((endpoint, promql, ct) => throwing
            ? throw new HttpIOException(HttpRequestError.ResponseEnded, "the response ended prematurely")
            : Good().QueryAsync(endpoint, promql, ct));
        var rig = Make(client, [Server("badwolf", "http://tsdb:8428")]);

        await rig.Monitor.RunCycleAsync(CancellationToken.None);
        Assert.Equal(DiskHealthState.Healthy, rig.Readings.Latest("badwolf")!.Overall);

        throwing = true;
        for (var i = 0; i < 3; i++)
        {
            rig.Clock.Utc += DiskHealthMonitor.Interval;
            await rig.Monitor.RunCycleAsync(CancellationToken.None);
            Assert.Equal(DiskHealthState.Unknown, rig.Readings.Latest("badwolf")!.Overall);
        }

        Assert.Contains(rig.Notifications.Events, e => e.EventType == NotificationEventTypes.DiskHealthUnknown);
    }

    private sealed class MutableServers(List<ServerConfig> servers) : Whiskers.Services.ServerConfig.IServerConfigService
    {
        private readonly FakeServerConfig _shape = new();
        public bool IsInitialized => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<ServerConfig> GetServers() => servers.ToList();
        public List<ServerConfig> GetEnabledServers() => servers.Where(s => s.Enabled).ToList();
        public ServerConfig? GetServer(string serverId) => servers.FirstOrDefault(s => s.Id == serverId);
        public ServerConfig? GetDefaultServer() => servers.FirstOrDefault();
        public bool SupportsTerminal(string? serverId) => false;
        public Task AddServerAsync(ServerConfig server) => _shape.AddServerAsync(server);
        public Task UpdateServerAsync(ServerConfig server) => _shape.UpdateServerAsync(server);
        public Task RemoveServerAsync(string serverId) => _shape.RemoveServerAsync(serverId);
        public Task SaveSshKeyAsync(string serverId, string fileName, byte[] keyData) => _shape.SaveSshKeyAsync(serverId, fileName, keyData);
        public string? GetSshKeyPath(ServerConfig server) => null;
        public Task DeleteSshKeyAsync(string serverId) => _shape.DeleteSshKeyAsync(serverId);
        public Task<bool> AdoptCloudKeyRefAsync(string serverId, string plaintext, string vaultRef) => _shape.AdoptCloudKeyRefAsync(serverId, plaintext, vaultRef);
    }
}
