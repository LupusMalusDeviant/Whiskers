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
        string? containerId = null, string? serverId = null, int tailLines = 500, int maxResults = 200)
    {
        var results = new List<LogSearchResult>();
        Regex? regex = null;

        if (isRegex)
        {
            try { regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2)); }
            catch { return results; }
        }

        var containers = serverId != null
            ? await _docker.ListContainersAsync(all: false, serverId: serverId)
            : await _docker.ListAllContainersAsync(all: false);
        var targets = containerId != null
            ? containers.Where(c => c.Id == containerId || c.Name == containerId).ToList()
            : containers.ToList();

        foreach (var container in targets)
        {
            if (results.Count >= maxResults) break;

            try
            {
                // Always fetch from the container's OWN server — with a fleet-wide list, `serverId` is null.
                var logs = await _docker.GetContainerLogsAsync(container.Id, tailLines, container.ServerId);
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
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to search logs for {Container}", container.Name);
            }
        }

        return results;
    }
}
