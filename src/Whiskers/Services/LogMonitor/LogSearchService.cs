using System.Text.RegularExpressions;
using Whiskers.Services.Docker;

namespace Whiskers.Services.LogMonitor;

public class LogSearchResult
{
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Line { get; set; } = "";
    public int LineNumber { get; set; }
}

public class LogSearchService : ILogSearchService
{
    private readonly IDockerService _docker;
    private readonly ILogger<LogSearchService> _logger;

    public LogSearchService(IDockerService docker, ILogger<LogSearchService> logger)
    {
        _docker = docker;
        _logger = logger;
    }

    /// <summary>Search logs of one or all containers for a pattern. Without an explicit
    /// <paramref name="serverId"/> this searches the WHOLE fleet — the container picker offers containers
    /// from every server, so a default-server-only search silently returned nothing for the others.</summary>
    public async Task<List<LogSearchResult>> SearchAsync(string pattern, bool isRegex = false,
        string? containerId = null, string? serverId = null, int tailLines = 500, int maxResults = 200,
        CancellationToken ct = default)
    {
        var results = new List<LogSearchResult>();
        Regex? regex = null;

        if (isRegex)
        {
            // Review 1.0.0 (T3): an invalid pattern is the caller's error to see, not "0 hits".
            try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2)); }
            catch (RegexParseException ex) { throw new InvalidSearchPatternException(ex); }
        }

        // A container picked by name is found stopped too (review 1.0.0, T3): its logs stay readable, and the page's
        // picker offers it — a running-only listing answered "no matches" for a container that was never searched. A
        // search across all containers stays with the running ones, so it does not read every exited container's log.
        var all = containerId != null;
        var containers = serverId != null
            ? await _docker.ListContainersAsync(all: all, serverId: serverId, ct: ct)
            : await _docker.ListAllContainersAsync(all, ct);
        var targets = containerId != null
            ? containers.Where(c => c.Id == containerId || c.Name == containerId).ToList()
            : containers.ToList();

        foreach (var container in targets)
        {
            if (results.Count >= maxResults) break;

            try
            {
                // Always fetch from the container's OWN server — with a fleet-wide list, `serverId` is null.
                // R1-41: the caller's stop is the callerToken (no circuit failure for the server), not the plain read's token.
                var logs = await _docker.GetContainerLogsAsync(container.Id, tailLines, container.ServerId, since: null,
                    deadline: CancellationToken.None, callerToken: ct);
                var lines = logs.Split('\n');

                for (int i = 0; i < lines.Length && results.Count < maxResults; i++)
                {
                    var line = lines[i];
                    bool match = regex != null
                        ? regex.IsMatch(line)
                        : line.Contains(pattern, StringComparison.OrdinalIgnoreCase);

                    if (match)
                    {
                        results.Add(new LogSearchResult
                        {
                            ContainerId = container.Id,
                            ContainerName = container.Name,
                            ServerId = container.ServerId,
                            ServerName = container.ServerName,
                            // At a complete word (R1-24): the line reaches the call history and the transcript.
                            Line = line.Length > 500 ? Whiskers.Utils.ShellUtils.CutAtBoundary(line, 500) + "..." : line,
                            LineNumber = i + 1
                        });
                    }
                }
            }
            // A stopped caller (INV-40) ends the search; only a failing container is skipped.
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                _logger.LogDebug(ex, "Failed to search logs for {Container}", container.Name);
            }
        }

        return results;
    }
}
