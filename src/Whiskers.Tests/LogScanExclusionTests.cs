using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.LogMonitor.Hygiene;

namespace Whiskers.Tests;

/// <summary>
/// Access-path detection for the log scan (Plan-0007 WP1/WP2).
///
/// <para>Two containers caused the 2026-08-26 incident — the tunnel and the socket proxy Whiskers reaches
/// Docker through. Scanning them means scanning the record of the scan, and in two weeks that record was
/// 822 MB.</para>
///
/// <para>The dangerous direction here is the opposite of the usual one. Missing a proxy costs disk; excluding
/// a container by mistake removes it from monitoring and looks exactly like a container with nothing to
/// report. So the tests weigh false exclusions heavier than missed ones.</para>
/// </summary>
public class LogScanExclusionTests
{
    private static readonly HashSet<string> LocalHost = new(StringComparer.OrdinalIgnoreCase) { "badwolf" };

    private static LogScanExclusions Build(params string[] manualNames) =>
        new(NullLogger<LogScanExclusions>.Instance,
            LocalHost,
            new HashSet<string>(manualNames.Length == 0 ? new[] { "serverwatch" } : manualNames,
                StringComparer.OrdinalIgnoreCase));

    private static ServerConfig ReachedOverTcp(string host = "100.64.0.1", int port = 2376) => new()
    {
        Id = "badwolf",
        Name = "Badwolf",
        ConnectionType = ConnectionType.TCP,
        TcpHost = host,
        TcpPort = port
    };

    private static ContainerInfo Container(string name, params (string Ip, ushort Public, ushort Private)[] ports) => new()
    {
        Id = "id-" + name,
        Name = name,
        ServerId = "badwolf",
        ServerName = "Badwolf",
        Ports = ports.Select(p => new PortMapping { IP = p.Ip, PublicPort = p.Public, PrivatePort = p.Private }).ToList()
    };

    [Fact]
    public void The_container_we_actually_connect_to_is_excluded()
    {
        // The one the incident was about: every request Whiskers makes is a line in this container's log.
        var detector = Build();
        var server = ReachedOverTcp();

        var found = detector.Evaluate(server, new[]
        {
            Container("ghostunnel", ("0.0.0.0", 2376, 2376)),
            Container("nextcloud", ("0.0.0.0", 8080, 80))
        });

        var excluded = Assert.Single(found);
        Assert.Equal("ghostunnel", excluded.ContainerName);
        Assert.Equal("access-path", excluded.Reason);
    }

    [Fact]
    public void A_container_that_merely_shares_the_name_keeps_being_scanned()
    {
        // The acceptance criterion from Plan-0007 WP2, and the reason detection is by path and not by name:
        // somebody else's socket proxy is somebody else's evidence, and dropping it is a silent blind spot.
        var detector = Build();
        var server = ReachedOverTcp(port: 2376);

        var found = detector.Evaluate(server, new[]
        {
            Container("socket-proxy", ("0.0.0.0", 9999, 2375)),
            Container("ghostunnel", ("0.0.0.0", 2376, 2376))
        });

        Assert.Equal(new[] { "ghostunnel" }, found.Select(f => f.ContainerName));
    }

    [Fact]
    public void A_port_published_on_one_address_only_matches_that_address()
    {
        // A container bound to 127.0.0.1:2376 cannot be the thing we reach at 100.64.0.1:2376. Treating the
        // port number alone as proof would exclude a loopback-only service on every host that happens to
        // reuse the port.
        var detector = Build();

        var found = detector.Evaluate(ReachedOverTcp("100.64.0.1", 2376), new[]
        {
            Container("something-local", ("127.0.0.1", 2376, 2376))
        });

        Assert.Empty(found);
    }

    [Fact]
    public void A_socket_connection_excludes_nothing_by_detection()
    {
        // No port to match against. Guessing here would be exactly the name-based matching this avoids.
        var detector = Build();
        var server = new ServerConfig { Id = "badwolf", Name = "Badwolf", ConnectionType = ConnectionType.Local };

        var found = detector.Evaluate(server, new[] { Container("socket-proxy", ("0.0.0.0", 2375, 2375)) });

        Assert.Empty(found);
    }

    [Fact]
    public void The_manual_override_still_wins()
    {
        // The hop behind the tunnel is not visible in a container list, so the override has to keep working —
        // and it has to work on remote hosts too, which the old self-name rule did not do.
        var detector = Build("serverwatch", "socket-proxy");

        var found = detector.Evaluate(ReachedOverTcp(), new[]
        {
            Container("socket-proxy", ("0.0.0.0", 9999, 2375)),
            Container("nextcloud", ("0.0.0.0", 8080, 80))
        });

        var excluded = Assert.Single(found);
        Assert.Equal("socket-proxy", excluded.ContainerName);
        Assert.Equal("configured", excluded.Reason);
    }

    [Fact]
    public void A_same_named_container_on_a_REMOTE_host_keeps_being_scanned()
    {
        // Pre-existing behaviour, pinned here because the first draft of this class quietly widened it to
        // "any host". A container called serverwatch on somebody else's server is a different process, and
        // its logs are real evidence about a real system — dropping them is a silent blind spot on a host
        // Whiskers was specifically asked to watch.
        var detector = Build();

        var remote = Container("serverwatch");
        remote.ServerId = "hetzner-apps";

        var found = detector.Evaluate(
            new ServerConfig { Id = "hetzner-apps", Name = "AppServer", ConnectionType = ConnectionType.Local },
            new[] { remote });

        Assert.Empty(found);
    }

    [Fact]
    public void Whiskers_own_container_is_excluded_on_its_own_host()
    {
        var detector = Build();

        var found = detector.Evaluate(
            new ServerConfig { Id = "badwolf", Name = "Badwolf", ConnectionType = ConnectionType.Local },
            new[] { Container("serverwatch") });

        Assert.Equal("configured", Assert.Single(found).Reason);
    }

    [Fact]
    public void The_exclusion_says_what_it_cannot_see()
    {
        // The detail text is the whole mitigation for the undetectable second hop. If it ever stops naming the
        // override, an operator has no way to find out why the proxy behind the tunnel is still being scanned.
        var detector = Build();

        var found = detector.Evaluate(ReachedOverTcp(), new[] { Container("ghostunnel", ("0.0.0.0", 2376, 2376)) });

        Assert.Contains("WHISKERS_SELF_CONTAINERS", Assert.Single(found).Detail);
    }

    [Fact]
    public void Exclusions_stay_visible_after_the_scan()
    {
        // WP2.1: an exclusion nobody can look up is indistinguishable from a container that quietly stopped
        // reporting. Current() is what the server view, the metric and the MCP tool read.
        var detector = Build();
        detector.Evaluate(ReachedOverTcp(), new[] { Container("ghostunnel", ("0.0.0.0", 2376, 2376)) });

        Assert.Equal(new[] { "ghostunnel" }, detector.Current().Select(e => e.ContainerName));
    }

    [Fact]
    public void A_server_that_no_longer_excludes_anything_reports_nothing()
    {
        // Stale exclusions would make the count grow on its own — the exact signal WP2.2 uses to say the
        // detection has become too greedy.
        var detector = Build();
        var server = ReachedOverTcp();
        detector.Evaluate(server, new[] { Container("ghostunnel", ("0.0.0.0", 2376, 2376)) });

        detector.Evaluate(server, new[] { Container("nextcloud", ("0.0.0.0", 8080, 80)) });

        Assert.Empty(detector.Current());
    }
}
