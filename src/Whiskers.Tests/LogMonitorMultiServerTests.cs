using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.LogMonitor;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests;

/// <summary>The log monitor scans the WHOLE fleet, not just the default server: an "all containers" rule
/// used to silently cover one host because the scan called ListContainersAsync() without a server id.
/// These tests pin the fleet-wide scan and everything that had to move with it — per-server log fetches,
/// server-scoped watermarks/cooldowns, and the self-container guard that must only apply to our own host.
/// </summary>
public sealed class LogMonitorMultiServerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"logmon-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public LogMonitorMultiServerTests()
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
        try { File.Delete(_dbPath); } catch { }
    }

    // --- helpers ---------------------------------------------------------------------------------------

    private static ContainerInfo Container(string id, string name, string serverId, string serverName) =>
        new() { Id = id, Name = name, Image = "img:1", ServerId = serverId, ServerName = serverName };

    private LogAlertRuleEntity SeedRule(string name = "fatal", string pattern = "FATAL",
        string? containerName = null, int cooldownMinutes = 10)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var rule = new LogAlertRuleEntity
        {
            Name = name,
            Pattern = pattern,
            ContainerName = containerName,
            Severity = "error",
            CooldownMinutes = cooldownMinutes
        };
        db.LogAlertRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    private LogAlertRuleEntity ReloadRule(string ruleId)
    {
        using var scope = _sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MetricsDbContext>()
            .LogAlertRules.AsNoTracking().Single(r => r.RuleId == ruleId);
    }

    private LogMonitorService Monitor(FakeDocker docker, FakeNotifications notifications, FakeServerConfig servers) =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(), docker, servers, notifications,
            NullLogger<LogMonitorService>.Instance, TestBudget.Create(), new Whiskers.Services.Observability.SelfMetrics.SelfMetrics(),
            // The REAL detector: this class contains the test that demands our own container is skipped on
            // the local host and scanned on a remote one, so a stand-in here would assert nothing.
            new Whiskers.Services.LogMonitor.Hygiene.LogScanExclusions(
                NullLogger<Whiskers.Services.LogMonitor.Hygiene.LogScanExclusions>.Instance,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "local" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "serverwatch" }),
            new NoOutcomes());

    private static FakeServerConfig TwoServers() => new(
        new Whiskers.Models.ServerConfig { Id = "local", Name = "Badwolf (local)", ConnectionType = ConnectionType.Local, IsDefault = true },
        new Whiskers.Models.ServerConfig { Id = "infomaniak", Name = "LupusMalus", ConnectionType = ConnectionType.TCP });

    // --- the fleet-wide scan ---------------------------------------------------------------------------

    [Fact]
    public async Task Alerts_on_a_container_of_a_REMOTE_server()
    {
        var rule = SeedRule();
        var docker = new FakeDocker(
            Container("c-local", "authentik-worker-1", "local", "Badwolf (local)"),
            Container("c-remote", "burg-web", "infomaniak", "LupusMalus"));
        docker.Logs["infomaniak/c-remote"] = "some noise\nFATAL: Kontrolltest\n";
        var notifications = new FakeNotifications();

        await Monitor(docker, notifications, TwoServers()).RunScanCycleAsync(CancellationToken.None);

        var evt = Assert.Single(notifications.Events);
        Assert.Equal("burg-web", evt.ContainerName);
        Assert.Equal("log_alert:error", evt.EventType);
        Assert.Contains("LupusMalus", evt.ImageInfo); // the host is named — container names repeat
        Assert.Equal(1, ReloadRule(rule.RuleId).TriggerCount);
    }

    [Fact]
    public async Task A_matched_line_is_redacted_before_it_is_cut_and_sent()
    {
        SeedRule();
        var docker = new FakeDocker(Container("c-remote", "burg-web", "infomaniak", "LupusMalus"));
        docker.Logs["infomaniak/c-remote"] =
            $"FATAL: login failed password=canary-value-r125 {string.Join(" ", Enumerable.Repeat("noise", 60))}" + Environment.NewLine;
        var notifications = new FakeNotifications();

        await Monitor(docker, notifications, TwoServers()).RunScanCycleAsync(CancellationToken.None);

        var evt = Assert.Single(notifications.Events);
        Assert.Contains("password=***", evt.ImageInfo);
        Assert.DoesNotContain("canary-va", evt.ImageInfo);
    }

    [Fact]
    public async Task Fetches_each_container_log_from_its_own_server()
    {
        SeedRule();
        var docker = new FakeDocker(
            Container("c-local", "authentik-worker-1", "local", "Badwolf (local)"),
            Container("c-remote", "burg-web", "infomaniak", "LupusMalus"));

        await Monitor(docker, new FakeNotifications(), TwoServers()).RunScanCycleAsync(CancellationToken.None);

        // Without the server id the call would land on the default host — the bug that hid five servers.
        Assert.Equal(
            new[] { ("c-local", "local"), ("c-remote", "infomaniak") },
            docker.LogCalls.Select(c => (c.ContainerId, c.ServerId)).OrderBy(c => c.ContainerId).ToArray());
    }

    [Fact]
    public async Task Keeps_the_local_behaviour_unchanged()
    {
        var rule = SeedRule();
        var docker = new FakeDocker(Container("c-local", "authentik-worker-1", "local", "Badwolf (local)"));
        docker.Logs["local/c-local"] = "FATAL: Kontrolltest\n";
        var notifications = new FakeNotifications();

        await Monitor(docker, notifications, TwoServers()).RunScanCycleAsync(CancellationToken.None);

        Assert.Equal("authentik-worker-1", Assert.Single(notifications.Events).ContainerName);
        Assert.Equal(1, ReloadRule(rule.RuleId).TriggerCount);
    }

    [Fact]
    public async Task A_failing_server_does_not_stop_the_others()
    {
        SeedRule();
        var docker = new FakeDocker(
            Container("c-local", "authentik-worker-1", "local", "Badwolf (local)"),
            Container("c-remote", "burg-web", "infomaniak", "LupusMalus"));
        docker.FailingServerIds.Add("local");
        docker.Logs["infomaniak/c-remote"] = "FATAL: Kontrolltest\n";
        var notifications = new FakeNotifications();

        await Monitor(docker, notifications, TwoServers()).RunScanCycleAsync(CancellationToken.None);

        Assert.Equal("burg-web", Assert.Single(notifications.Events).ContainerName);
    }

    // --- per-server keys -------------------------------------------------------------------------------

    [Fact]
    public async Task Same_container_id_on_two_servers_gets_its_own_cooldown()
    {
        // Container ids are unique per host only: a shared cooldown key would swallow the second host.
        SeedRule();
        var docker = new FakeDocker(
            Container("deadbeef", "postgres", "local", "Badwolf (local)"),
            Container("deadbeef", "postgres", "infomaniak", "LupusMalus"));
        docker.Logs["local/deadbeef"] = "FATAL: boom\n";
        docker.Logs["infomaniak/deadbeef"] = "FATAL: boom\n";
        var notifications = new FakeNotifications();

        await Monitor(docker, notifications, TwoServers()).RunScanCycleAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "Badwolf (local)", "LupusMalus" },
            notifications.Events.Select(e => e.ServerName!).OrderBy(s => s).ToArray());
    }

    [Fact]
    public void Composite_key_separates_the_same_container_id_on_two_servers()
    {
        var local = Container("deadbeef", "postgres", "local", "Badwolf (local)");
        var remote = Container("deadbeef", "postgres", "infomaniak", "LupusMalus");

        // The watermark map (_lastLogCheck) is keyed by this: on container.Id alone the two hosts would
        // share one "last checked" timestamp and one of them would skip its window.
        Assert.NotEqual(LogMonitorService.CompositeKey(local), LogMonitorService.CompositeKey(remote));
        Assert.StartsWith("local:", LogMonitorService.CompositeKey(local));
    }

    // --- the self-container guard ----------------------------------------------------------------------

    [Fact]
    public async Task Never_scans_our_own_container_on_the_local_host()
    {
        SeedRule();
        var docker = new FakeDocker(Container("c-self", "serverwatch", "local", "Badwolf (local)"));
        docker.Logs["local/c-self"] = "FATAL: our own alert log line\n";
        var notifications = new FakeNotifications();

        await Monitor(docker, notifications, TwoServers()).RunScanCycleAsync(CancellationToken.None);

        Assert.Empty(docker.LogCalls);
        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Still_scans_a_same_named_container_on_a_remote_host()
    {
        // The guard exists to break OUR OWN feedback loop; a remote namesake is a different process.
        SeedRule();
        var docker = new FakeDocker(Container("c-remote", "serverwatch", "infomaniak", "LupusMalus"));
        docker.Logs["infomaniak/c-remote"] = "FATAL: Kontrolltest\n";
        var notifications = new FakeNotifications();

        await Monitor(docker, notifications, TwoServers()).RunScanCycleAsync(CancellationToken.None);

        Assert.Single(notifications.Events);
    }

    [Fact]
    public void Self_server_ids_are_the_local_connections()
    {
        var ids = LogMonitorService.ResolveSelfServerIds(TwoServers());
        Assert.Equal(new[] { "local" }, ids.ToArray());
    }

    [Fact]
    public void Self_server_ids_fall_back_to_the_default_server_when_no_local_one_exists()
    {
        // All-mTLS / Kubernetes fleets have no ConnectionType.Local entry — keep guarding the host the
        // single-server scan used to look at rather than guarding nothing.
        var servers = new FakeServerConfig(
            new Whiskers.Models.ServerConfig { Id = "hetzner-apps", Name = "AppServer", ConnectionType = ConnectionType.TCP, IsDefault = true },
            new Whiskers.Models.ServerConfig { Id = "rabenhof", Name = "Rabenhof", ConnectionType = ConnectionType.TCP });

        Assert.Equal(new[] { "hetzner-apps" }, LogMonitorService.ResolveSelfServerIds(servers).ToArray());
    }

    [Fact]
    public async Task Keeps_a_silent_servers_watermark_so_its_outage_window_is_still_scanned()
    {
        // An unreachable host contributes no containers. Dropping its watermark would re-baseline it to
        // "now" on recovery — everything logged during the outage would never be looked at.
        SeedRule();
        var docker = new FakeDocker(
            Container("c-local", "authentik-worker-1", "local", "Badwolf (local)"),
            Container("c-remote", "burg-web", "infomaniak", "LupusMalus"));
        var notifications = new FakeNotifications();
        var monitor = Monitor(docker, notifications, TwoServers());

        await monitor.RunScanCycleAsync(CancellationToken.None);   // baseline both hosts
        await Task.Delay(40);

        // The line is written while the host is silent, and only becomes readable once it is back.
        docker.AddTimedLine("infomaniak", "c-remote", DateTime.UtcNow, "FATAL: happened during the outage");
        docker.UnreachableServerIds.Add("infomaniak");
        await Task.Delay(40);

        await monitor.RunScanCycleAsync(CancellationToken.None);   // outage cycle: no containers, no prune
        Assert.Empty(notifications.Events);
        await Task.Delay(40);

        docker.UnreachableServerIds.Remove("infomaniak");
        await monitor.RunScanCycleAsync(CancellationToken.None);   // recovery: reads from the OLD watermark

        var evt = Assert.Single(notifications.Events);
        Assert.Equal("burg-web", evt.ContainerName);
        Assert.Contains("happened during the outage", evt.ImageInfo);
    }

    // --- rule targeting --------------------------------------------------------------------------------

    [Fact]
    public void A_name_filtered_rule_matches_that_name_on_every_server()
    {
        // The UI picker lists containers of all servers but stores only the name, and MCP-created rules
        // set the name alone — so a name rule means "this workload, wherever it runs".
        var rule = new LogAlertRuleEntity { ContainerName = "postgres" };
        Assert.True(LogMonitorService.RuleApplies(rule, Container("a", "postgres", "local", "Badwolf")));
        Assert.True(LogMonitorService.RuleApplies(rule, Container("b", "postgres", "rabenhof", "Rabenhof")));
        Assert.False(LogMonitorService.RuleApplies(rule, Container("c", "redis", "rabenhof", "Rabenhof")));
    }

    [Fact]
    public async Task A_name_filtered_rule_alerts_only_on_that_container()
    {
        // ContainerId stays null for UI- and MCP-created rules, so "no filter" must test BOTH fields —
        // otherwise a rule for one container fires on every container of every server.
        SeedRule(name: "qr", pattern: "LUPUSLINK-NEU", containerName: "lupusmalus-web-app-1");
        var docker = new FakeDocker(
            Container("c-local", "authentik-worker-1", "local", "Badwolf (local)"),
            Container("c-remote", "lupusmalus-web-app-1", "infomaniak", "LupusMalus"));
        docker.Logs["local/c-local"] = "LUPUSLINK-NEU seen in the wrong container\n";
        docker.Logs["infomaniak/c-remote"] = "LUPUSLINK-NEU\n";
        var notifications = new FakeNotifications();

        await Monitor(docker, notifications, TwoServers()).RunScanCycleAsync(CancellationToken.None);

        Assert.Equal("lupusmalus-web-app-1", Assert.Single(notifications.Events).ContainerName);
        Assert.DoesNotContain(docker.LogCalls, c => c.ContainerId == "c-local"); // not even fetched
    }

    [Fact]
    public void An_unfiltered_rule_matches_everything()
    {
        var rule = new LogAlertRuleEntity();
        Assert.True(LogMonitorService.RuleApplies(rule, Container("a", "anything", "rabenhof", "Rabenhof")));
    }

}
