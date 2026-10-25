using System.Collections.Concurrent;
using Whiskers.Models;

namespace Whiskers.Services.LogMonitor.Hygiene;

/// <summary>Access-path detection for the log scan (Plan-0007 WP1).
///
/// <para>See <see cref="ILogScanExclusions"/> for why this exists. The detection rule is deliberately narrow:
/// a container is on the access path when Whiskers <em>connects to it</em> — the port it publishes is the port
/// in this server's configuration, on an address this server's configuration names. Everything else is a
/// guess, and a wrong guess here removes a container from monitoring without anyone noticing.</para>
///
/// <para><b>What this does not detect.</b> Only the outermost hop is knowable. On a host where Whiskers talks
/// to a tunnel which talks to a socket proxy, the tunnel is found and the proxy behind it is not — nothing in
/// the container list says who talks to whom. That second hop is what
/// <c>WHISKERS_SELF_CONTAINERS</c> is for, and <see cref="Evaluate"/> says so in the detail text of the hop
/// it did find, so the operator is told rather than left to notice.</para>
/// </summary>
public sealed class LogScanExclusions : ILogScanExclusions
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<LogScanExclusion>> _byServer = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlySet<string> _manualNames;
    private readonly IReadOnlySet<string> _selfServerIds;
    private readonly ILogger<LogScanExclusions> _logger;

    public LogScanExclusions(
        ILogger<LogScanExclusions> logger,
        IReadOnlySet<string> selfServerIds,
        IReadOnlySet<string>? manualNames = null)
    {
        _logger = logger;
        _selfServerIds = selfServerIds;
        _manualNames = manualNames ?? ManualNamesFromEnvironment();
    }

    /// <summary>The manual override: <c>WHISKERS_SELF_CONTAINERS</c>, comma-separated. The pre-rebrand name
    /// <c>SERVERWATCH_SELF_CONTAINERS</c> is still honoured when the new one is unset, so existing <c>.env</c>
    /// files keep working. The default covers both container names Whiskers ships with (installer:
    /// <c>whiskers</c>, repository compose files: <c>serverwatch</c>). It takes precedence over detection — an
    /// operator who names a container knows something the port table does not.</summary>
    public static IReadOnlySet<string> ManualNamesFromEnvironment() => new HashSet<string>(
        (Environment.GetEnvironmentVariable("WHISKERS_SELF_CONTAINERS")
         ?? Environment.GetEnvironmentVariable("SERVERWATCH_SELF_CONTAINERS")
         ?? "whiskers,serverwatch")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LogScanExclusion> Evaluate(Models.ServerConfig server, IReadOnlyList<ContainerInfo> containers)
    {
        var found = new List<LogScanExclusion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var container in containers)
        {
            var exclusion = Classify(server, container);
            if (exclusion is null || !seen.Add(container.Id)) continue;
            found.Add(exclusion);
        }

        _byServer[server.Id] = found;

        if (found.Count > 0)
            _logger.LogDebug("Log scan on {Server} skips {Count} container(s): {Names}",
                server.Name, found.Count, string.Join(", ", found.Select(f => f.ContainerName)));

        return found;
    }

    public IReadOnlyList<LogScanExclusion> Current() =>
        _byServer.Values.SelectMany(v => v).OrderBy(e => e.ServerId, StringComparer.Ordinal)
            .ThenBy(e => e.ContainerName, StringComparer.Ordinal).ToList();

    private LogScanExclusion? Classify(Models.ServerConfig server, ContainerInfo container)
    {
        // 1. Named in the configuration, and on the host Whiskers itself runs on. Both halves matter, and the
        //    host restriction is the pre-existing rule kept verbatim: a container that shares our name on a
        //    REMOTE host is a different process, and its logs are real evidence about somebody else's system.
        //    Takes precedence over detection — an operator naming a container knows about hops the port table
        //    cannot show, such as a socket proxy sitting behind the tunnel.
        if (_selfServerIds.Contains(container.ServerId) && _manualNames.Contains(container.Name))
            return new LogScanExclusion(server.Id, container.Id, container.Name, "configured",
                "Named in WHISKERS_SELF_CONTAINERS, on the host Whiskers runs on. Whiskers' own container " +
                "is here by default: scanning it feeds its own alert lines back into the rules.");

        // 2. The access path — the only detection that is not a guess.
        var endpoint = AccessEndpoint(server);
        if (endpoint is not { } target) return null;

        var match = container.Ports.FirstOrDefault(p =>
            p.PublicPort == target.Port && AddressMatches(p.IP, target.Host));
        if (match is null) return null;

        return new LogScanExclusion(server.Id, container.Id, container.Name, "access-path",
            $"Whiskers reaches Docker on this server through {target.Host}:{target.Port}, which this container " +
            $"publishes. Every request Whiskers makes is a line in its log. If it forwards to a further " +
            $"container (a socket proxy behind a tunnel), that one is not detectable from the container list — " +
            $"add it to WHISKERS_SELF_CONTAINERS.");
    }

    /// <summary>The address:port this server's configuration tells us to connect to, or null when there is
    /// none to match against (a unix socket, or a Kubernetes cluster).</summary>
    private static (string Host, int Port)? AccessEndpoint(Models.ServerConfig server) => server.ConnectionType switch
    {
        ConnectionType.TCP when !string.IsNullOrWhiteSpace(server.TcpHost) => (server.TcpHost!, server.TcpPort),

        // Over SSH the tunnel's far end is the remote docker endpoint. When that is a socket there is no port
        // to match; when it is a TCP endpoint, the same port match applies on the remote side.
        ConnectionType.SSH when !string.IsNullOrWhiteSpace(server.TcpHost) => (server.TcpHost!, server.TcpPort),

        _ => null
    };

    /// <summary>A published port on 0.0.0.0 (or ::) answers for every address, so it matches whatever host we
    /// connect to. A port published on one specific address only matches that address.</summary>
    private static bool AddressMatches(string publishedOn, string connectingTo)
    {
        if (string.IsNullOrWhiteSpace(publishedOn)) return true;
        if (publishedOn is "0.0.0.0" or "::" or "[::]") return true;
        return string.Equals(publishedOn, connectingTo, StringComparison.OrdinalIgnoreCase);
    }
}
