namespace ServerWatch.Services.LogMonitor;

/// <summary>Full-text/regex search across container logs.</summary>
public interface ILogSearchService
{
    Task<List<LogSearchResult>> SearchAsync(string pattern, bool isRegex = false,
        string? containerId = null, string? serverId = null, int tailLines = 500, int maxResults = 200);
}
