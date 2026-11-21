using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.LogMonitor.Hygiene;
using Whiskers.Services.Observability.SelfMetrics;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// The daily log-hygiene pass (Plan-0007 WP4).
///
/// <para>The point of these tests is that the monitor <b>fires</b>. A finding that reaches nobody is what the
/// 2026-08-26 incident already had: the log driver was misconfigured for weeks, in plain sight of a tool that
/// inspected those containers every minute and said nothing.</para>
/// </summary>
public class LogHygieneMonitorTests
{
    private sealed class FakeHost : IHostCommandExecutor
    {
        public string Size { get; set; } = "157286400";     // 150 MB
        public string Free { get; set; } = "209715200";     // 200 MB

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
            => Task.FromResult(new CommandResult
            {
                ExitCode = 0,
                Output = command.Contains("df -B1") ? Free : Size
            });
    }

    private sealed class StubInventory : ILogInventory
    {
        private readonly LogInventoryEntry _entry;
        public StubInventory(LogInventoryEntry entry) => _entry = entry;

        public Task<IReadOnlyList<LogInventoryEntry>> SurveyAsync(
            Models.ServerConfig server, IReadOnlyList<ContainerInfo> containers, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LogInventoryEntry>>(new[] { _entry });

        public IReadOnlyList<LogInventoryEntry> Current() => new[] { _entry };
    }

    private static LogInventoryEntry Entry(long size, long? free) => new(
        "local", "id-ghostunnel", "ghostunnel",
        new ContainerLogConfiguration("json-file", null, null, "/var/lib/docker/x.log"),
        size, null, GrowthBytesPerDay: 52_428_800, free, DateTime.UtcNow);

    private static (LogHygieneMonitor Monitor, FakeNotifications Sent) Build(LogInventoryEntry entry)
    {
        var sent = new FakeNotifications();
        var container = new ContainerInfo
        {
            Id = "id-ghostunnel", Name = "ghostunnel", ServerId = "local", ServerName = "Host-A"
        };
        container.Labels["com.docker.compose.project"] = "serverwatch";
        container.Labels["com.docker.compose.service"] = "ghostunnel";

        var monitor = new LogHygieneMonitor(
            new StubInventory(entry),
            new FakeDocker(container),
            new FakeServerConfig(new Models.ServerConfig
            {
                Id = "local", Name = "Host-A", ConnectionType = ConnectionType.Local, IsDefault = true, Enabled = true
            }),
            sent,
            TestBudget.Create(),
            new SelfMetrics(),
            NullLogger<LogHygieneMonitor>.Instance);

        return (monitor, sent);
    }

    [Fact]
    public async Task An_unbounded_log_over_the_threshold_actually_raises_an_alert()
    {
        // The assertion that matters. 150 MB against 200 MB free is 43% of the remaining disk.
        var (monitor, sent) = Build(Entry(size: 157_286_400, free: 209_715_200));

        await monitor.RunOnceAsync(CancellationToken.None);

        var alert = Assert.Single(sent.Events);
        Assert.Equal("log_rotation_missing", alert.EventType);
        Assert.Equal("ghostunnel", alert.ContainerName);
    }

    [Fact]
    public async Task The_alert_carries_the_command_and_refuses_to_pass_as_the_cure()
    {
        var (monitor, sent) = Build(Entry(size: 157_286_400, free: 209_715_200));

        await monitor.RunOnceAsync(CancellationToken.None);

        var detail = Assert.Single(sent.Events).ImageInfo;
        // No compose working_dir label on this container, so the advice uses the project form. Both forms
        // end the same way, and that ending is the part an operator copies.
        Assert.Contains("up -d --force-recreate ghostunnel", detail);                  // WP4.3
        Assert.Contains("RECREATES", detail);
        Assert.Contains("/etc/docker/daemon.json", detail);                            // WP4.5
        Assert.Contains("not its cause", detail);                                      // WP4.4
        Assert.Contains("fills the remaining disk", detail);
    }

    [Fact]
    public async Task A_log_with_room_to_spare_stays_an_inventory_entry()
    {
        // WP4.1. The same 150 MB next to 10 GB of headroom is a note, not a message — an alert people learn
        // to ignore protects nothing.
        var (monitor, sent) = Build(Entry(size: 157_286_400, free: 10L * 1024 * 1024 * 1024));

        await monitor.RunOnceAsync(CancellationToken.None);

        Assert.Empty(sent.Events);
    }

    [Fact]
    public async Task The_same_finding_is_not_repeated_every_day()
    {
        // It is a slow-moving fact. Daily repetition is how an alert becomes a filter rule.
        var (monitor, sent) = Build(Entry(size: 157_286_400, free: 209_715_200));

        await monitor.RunOnceAsync(CancellationToken.None);
        await monitor.RunOnceAsync(CancellationToken.None);

        Assert.Single(sent.Events);
    }
}
