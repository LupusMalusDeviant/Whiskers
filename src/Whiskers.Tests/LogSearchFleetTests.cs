using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.LogMonitor;

namespace Whiskers.Tests;

/// <summary>The log search had the same blind spot as the monitor: without an explicit server id it asked
/// only the default host, while the page's container picker lists containers from every server — so picking
/// a remote container returned "no matches" instead of its log lines.</summary>
public class LogSearchFleetTests
{
    private static ContainerInfo Container(string id, string name, string serverId, string serverName) =>
        new() { Id = id, Name = name, ServerId = serverId, ServerName = serverName };

    private static LogSearchService Search(FakeDocker docker) =>
        new(docker, NullLogger<LogSearchService>.Instance);

    private static FakeDocker TwoHosts()
    {
        var docker = new FakeDocker(
            Container("c-local", "idp-worker-1", "local", "Host-A (local)"),
            Container("c-remote", "web", "host-f", "Host-F"));
        docker.Logs["local/c-local"] = "nothing to see";
        docker.Logs["host-f/c-remote"] = "ERROR: remote boom";
        return docker;
    }

    [Fact]
    public async Task Without_a_server_id_the_whole_fleet_is_searched()
    {
        var result = await Search(TwoHosts()).SearchAsync("boom");

        var hit = Assert.Single(result);
        Assert.Equal("web", hit.ContainerName);
        Assert.Equal("Host-F", hit.ServerName);   // results must say which host they came from
    }

    [Fact]
    public async Task Each_container_is_read_from_its_own_server()
    {
        var docker = TwoHosts();
        await Search(docker).SearchAsync("nothing-matches-this");

        Assert.Equal(
            new[] { ("c-local", "local"), ("c-remote", "host-f") },
            docker.LogCalls.Select(c => (c.ContainerId, c.ServerId)).ToArray());
    }

    [Fact]
    public async Task An_explicit_server_id_still_narrows_the_search()
    {
        var docker = TwoHosts();
        var result = await Search(docker).SearchAsync("boom", serverId: "local");

        Assert.Empty(result);
        Assert.Equal(new[] { "c-local" }, docker.LogCalls.Select(c => c.ContainerId).ToArray());
    }

    [Fact]
    public async Task A_container_filter_matches_by_name_across_servers()
    {
        var docker = TwoHosts();
        var result = await Search(docker).SearchAsync("boom", containerId: "web");

        Assert.Equal("host-f", Assert.Single(result).ServerId);
    }

    // Review 1.0.0 (T3): the page offers stopped containers (their logs stay readable), the search listed running ones only
    // and answered "no matches" for a picked container it never read.
    [Fact]
    public async Task A_stopped_container_picked_by_name_is_searched()
    {
        var docker = TwoHosts();
        docker.StoppedContainerIds.Add("c-remote");

        var picked = await Search(docker).SearchAsync("boom", containerId: "web");
        var pickedOnItsServer = await Search(docker).SearchAsync("boom", containerId: "web", serverId: "host-f");

        Assert.Equal("web", Assert.Single(picked).ContainerName);
        Assert.Equal("web", Assert.Single(pickedOnItsServer).ContainerName);
    }

    // A search across all containers stays with the running ones: it does not read the log of every exited container.
    [Fact]
    public async Task A_search_across_all_containers_reads_only_the_running_ones()
    {
        var docker = TwoHosts();
        docker.StoppedContainerIds.Add("c-remote");

        var everywhere = await Search(docker).SearchAsync("boom");
        var onItsServer = await Search(docker).SearchAsync("boom", serverId: "host-f");

        Assert.Empty(everywhere);
        Assert.Empty(onItsServer);
        Assert.DoesNotContain(docker.LogCalls, c => c.ContainerId == "c-remote");
    }

    // Review 1.0.0 (T3): an invalid regular expression read as "0 hits" — a search that never ran. It is its own exception
    // type, so a caller catches exactly this, and the message says what is wrong once.
    [Fact]
    public async Task An_invalid_regex_is_an_error_not_an_empty_result()
    {
        var docker = TwoHosts();

        var ex = await Assert.ThrowsAsync<InvalidSearchPatternException>(() => Search(docker).SearchAsync("([unclosed", isRegex: true));

        Assert.StartsWith("The search pattern is not a valid regular expression: ", ex.Message);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(ex.Message, "[Ii]nvalid").Count <= 1, ex.Message);
        Assert.IsAssignableFrom<System.Text.RegularExpressions.RegexParseException>(ex.InnerException);
        Assert.Empty(docker.LogCalls);   // refused before any log is read
    }
}
