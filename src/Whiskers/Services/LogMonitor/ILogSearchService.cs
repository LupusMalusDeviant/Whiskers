namespace Whiskers.Services.LogMonitor;

/// <summary>Full-text/regex search across the logs of running containers, and of a container picked by name also when
/// it is stopped (its logs stay readable, and the page offers it). An invalid regular expression throws
/// <see cref="InvalidSearchPatternException"/>: "no hits" would report a search that never ran.</summary>
public interface ILogSearchService
{
    Task<List<LogSearchResult>> SearchAsync(string pattern, bool isRegex = false,
        string? containerId = null, string? serverId = null, int tailLines = 500, int maxResults = 200,
        CancellationToken ct = default);
}
