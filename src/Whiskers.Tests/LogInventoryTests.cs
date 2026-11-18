using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.LogMonitor.Hygiene;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// The log-file inventory (Plan-0007 WP3/WP4).
///
/// <para>Two containers reached 822 MB in a fortnight with nothing watching. The inventory has to say so
/// before the disk does — and it has to be honest about the readings it could not take, because a made-up
/// number here gets acted on as if it were measured.</para>
/// </summary>
public class LogInventoryTests
{
    private sealed class FakeHost : IHostCommandExecutor
    {
        public Dictionary<string, CommandResult> Responses { get; } = new(StringComparer.Ordinal);
        public List<string> Commands { get; } = new();
        public CommandResult Fallback { get; set; } = new() { ExitCode = 1, Error = "no host access" };

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Commands.Add(command);
            foreach (var (needle, response) in Responses)
                if (command.Contains(needle, StringComparison.Ordinal))
                    return Task.FromResult(response);
            return Task.FromResult(Fallback);
        }
    }

    private static CommandResult Says(string output) => new() { ExitCode = 0, Output = output };

    private static readonly Models.ServerConfig HostA = new() { Id = "host-a", Name = "Host-A" };

    private static ContainerInfo Container(string name, params (string Key, string Value)[] labels)
    {
        var c = new ContainerInfo { Id = "id-" + name, Name = name, ServerId = "host-a", ServerName = "Host-A" };
        foreach (var (k, v) in labels) c.Labels[k] = v;
        return c;
    }

    private static LogInventory Build(FakeHost host, ContainerLogConfiguration? config) =>
        new(new FakeDocker { LogConfiguration = config }, host, NullLogger<LogInventory>.Instance);

    // --- readings ----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unbounded_log_is_measured_and_reported()
    {
        var host = new FakeHost();
        host.Responses["stat -c %s"] = Says("157286400");           // 150 MB
        host.Responses["df -B1"] = Says("209715200");               // 200 MB free

        var inventory = Build(host, new ContainerLogConfiguration("json-file", null, null, "/var/lib/docker/x-json.log"));

        var entry = Assert.Single(await inventory.SurveyAsync(HostA, new[] { Container("ghostunnel") }));

        Assert.Equal(157286400, entry.SizeBytes);
        Assert.True(entry.IsUnbounded);
        Assert.Equal(LogHygieneSeverity.Alert, LogHygieneAdvice.Severity(entry));
    }

    [Fact]
    public async Task An_unreadable_size_is_reported_as_unknown_and_never_guessed()
    {
        // WP3.2, and the rule that makes the whole inventory trustworthy. A zero here would read as "this log
        // is empty" — the opposite of the truth on a host we simply cannot see into.
        var inventory = Build(new FakeHost(), new ContainerLogConfiguration("json-file", null, null, "/var/lib/docker/x.log"));

        var entry = Assert.Single(await inventory.SurveyAsync(HostA, new[] { Container("ghostunnel") }));

        Assert.Null(entry.SizeBytes);
        Assert.NotNull(entry.UnknownReason);
        Assert.False(entry.IsUnbounded);   // unknown size is not evidence of a large one
        Assert.Equal(LogHygieneSeverity.None, LogHygieneAdvice.Severity(entry));
    }

    [Fact]
    public async Task A_driver_that_writes_elsewhere_is_not_our_problem()
    {
        var host = new FakeHost();
        host.Responses["stat -c %s"] = Says("999999999");

        var inventory = Build(host, new ContainerLogConfiguration("syslog", null, null, null));
        var entry = Assert.Single(await inventory.SurveyAsync(HostA, new[] { Container("app") }));

        Assert.Null(entry.SizeBytes);
        Assert.Contains("syslog", entry.UnknownReason);
        Assert.DoesNotContain(host.Commands, c => c.Contains("stat -c %s"));
    }

    [Fact]
    public async Task A_configured_rotation_limit_is_not_a_finding()
    {
        var host = new FakeHost();
        host.Responses["stat -c %s"] = Says("157286400");
        host.Responses["df -B1"] = Says("209715200");

        var inventory = Build(host, new ContainerLogConfiguration("json-file", "50m", "3", "/var/lib/docker/x.log"));
        var entry = Assert.Single(await inventory.SurveyAsync(HostA, new[] { Container("well-behaved") }));

        Assert.False(entry.IsUnbounded);
        Assert.Equal(LogHygieneSeverity.None, LogHygieneAdvice.Severity(entry));
    }

    [Fact]
    public async Task The_log_path_reaches_the_shell_quoted()
    {
        // The path comes from Docker rather than from a user, but it is still data on a command line. An
        // unquoted odd filename is an injection waiting for somebody to name a container badly.
        var host = new FakeHost();
        host.Responses["stat -c %s"] = Says("1024");

        var inventory = Build(host, new ContainerLogConfiguration("json-file", null, null, "/var/lib/docker/a b;rm -rf /.log"));
        await inventory.SurveyAsync(HostA, new[] { Container("odd") });

        var stat = Assert.Single(host.Commands, c => c.Contains("stat -c %s"));
        Assert.Contains("'/var/lib/docker/a b;rm -rf /.log'", stat);
    }

    // --- judgement ---------------------------------------------------------------------------------------

    [Fact]
    public void The_threshold_is_relative_to_the_free_disk()
    {
        // 150 MB is a note next to 10 GB of headroom and an alert next to 200 MB. An absolute threshold would
        // be wrong on every host but the one it was picked for.
        var roomy = Entry(size: 157_286_400, free: 10L * 1024 * 1024 * 1024);
        var tight = Entry(size: 157_286_400, free: 209_715_200);

        Assert.Equal(LogHygieneSeverity.Note, LogHygieneAdvice.Severity(roomy));
        Assert.Equal(LogHygieneSeverity.Alert, LogHygieneAdvice.Severity(tight));
    }

    [Fact]
    public void Without_disk_information_a_large_log_still_raises_an_alert()
    {
        // Staying quiet about a 2 GB log because df failed would be the worse of the two errors.
        var blind = Entry(size: 2L * 1024 * 1024 * 1024, free: null);

        Assert.Null(blind.ShareOfFreeDisk);
        Assert.Equal(LogHygieneSeverity.Alert, LogHygieneAdvice.Severity(blind));
    }

    [Fact]
    public void A_single_reading_admits_it_has_no_trend()
    {
        var text = LogHygieneAdvice.Describe(Entry(size: 157_286_400, free: 209_715_200), "Host-A");

        Assert.Contains("No growth rate yet", text);
    }

    [Fact]
    public void A_growth_rate_becomes_a_deadline()
    {
        // The number that makes people act is not the size, it is "this fills the disk on Thursday".
        var entry = Entry(size: 157_286_400, free: 209_715_200) with { GrowthBytesPerDay = 52_428_800 };  // 50 MB/day

        var text = LogHygieneAdvice.Describe(entry, "Host-A");

        Assert.Contains("50 MB per day", text);
        Assert.Contains("fills the remaining disk in roughly 4 days", text);
    }

    [Fact]
    public void The_remediation_is_copyable_and_says_it_recreates_the_container()
    {
        // An operator who finds out about the restart afterwards will not trust the next suggestion.
        var entry = Entry(size: 157_286_400, free: 209_715_200);
        var labels = new Dictionary<string, string>
        {
            ["com.docker.compose.project"] = "serverwatch",
            ["com.docker.compose.service"] = "ghostunnel",
            ["com.docker.compose.project.working_dir"] = "/opt/ServerWatch"
        };

        var text = LogHygieneAdvice.Remediation(entry, labels);

        Assert.Contains("docker compose up -d --force-recreate ghostunnel", text);
        Assert.Contains("/opt/ServerWatch", text);
        Assert.Contains("RECREATES", text);
        Assert.Contains("max-size", text);
        Assert.Contains("/etc/docker/daemon.json", text);   // WP4.5 — stop it happening to the next container
    }

    [Fact]
    public void A_container_without_compose_gets_advice_it_can_actually_follow()
    {
        var text = LogHygieneAdvice.Remediation(Entry(size: 1024, free: 1024), new Dictionary<string, string>());

        Assert.DoesNotContain("docker compose up", text);
        Assert.Contains("docker inspect", text);
        Assert.Contains("loses any option not repeated", text);
    }

    [Fact]
    public void The_wording_refuses_to_be_mistaken_for_the_cure()
    {
        // WP4.4. Without this sentence, closing the ticket for a rotation limit reads like closing the
        // incident — and the load-budget work that actually fixes it gets postponed.
        Assert.Contains("not its cause", LogHygieneAdvice.TriggerNotCause);
        Assert.Contains("abandoned instead of cancelled", LogHygieneAdvice.TriggerNotCause);
    }

    private static LogInventoryEntry Entry(long? size, long? free) => new(
        "host-a", "id-x", "ghostunnel",
        new ContainerLogConfiguration("json-file", null, null, "/var/lib/docker/x.log"),
        size, size is null ? "unreadable" : null, null, free, DateTime.UtcNow);
}
