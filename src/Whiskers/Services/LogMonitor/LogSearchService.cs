using System.Text.RegularExpressions;
using Whiskers.Services.Docker;

namespace Whiskers.Services.LogMonitor;

public class LogSearchResult
{
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
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

    /// <summary>Search logs of one or all containers for a pattern.</summary>
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

        var containers = await _docker.ListContainersAsync(all: false, serverId: serverId);
        var targets = containerId != null
            ? containers.Where(c => c.Id == containerId || c.Name == containerId).ToList()
            : containers.ToList();

        foreach (var container in targets)
        {
            if (results.Count >= maxResults) break;

            try
            {
                var logs = await _docker.GetContainerLogsAsync(container.Id, tailLines, serverId);
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
                            Line = line.Length > 500 ? line[..500] + "..." : line,
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
