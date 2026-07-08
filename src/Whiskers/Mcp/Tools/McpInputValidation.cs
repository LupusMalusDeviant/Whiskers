using System.Text.RegularExpressions;
using Whiskers.Models;

namespace Whiskers.Mcp.Tools;

/// <summary>
/// Input-validation and safe target resolution for the MCP tool boundary. Pure static helpers, so a
/// hostile tool argument is rejected with a clear error instead of reaching a shell or the wrong target.
/// </summary>
public static class McpInputValidation
{
    // A deployment project name becomes a path segment under /opt/deployments and is spliced into shell
    // commands. Require a leading alphanumeric, allow only a safe charset, and forbid ".." (path escape).
    private static readonly Regex SafeProjectNameRegex = new(@"^[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.Compiled);

    /// <summary>True when <paramref name="name"/> is a safe docker-compose project/directory name.</summary>
    public static bool IsSafeProjectName(string? name)
        => !string.IsNullOrWhiteSpace(name) && !name.Contains("..") && SafeProjectNameRegex.IsMatch(name);

    /// <summary>
    /// Resolve a container by exact id/name, else by a UNIQUE id-prefix. Returns a clear error (never the
    /// raw id as a silent fallback) when the prefix is ambiguous or nothing matches — so a truncated id
    /// can never act on the wrong container.
    /// </summary>
    public static (ContainerInfo? Container, string? Error) Resolve(IList<ContainerInfo> containers, string containerId)
    {
        var exact = containers.FirstOrDefault(c => c.Id == containerId || c.Name == containerId);
        if (exact != null) return (exact, null);

        var prefixMatches = containers.Where(c => c.Id.StartsWith(containerId, StringComparison.Ordinal)).ToList();
        if (prefixMatches.Count == 1) return (prefixMatches[0], null);
        if (prefixMatches.Count > 1)
            return (null, $"Ambiguous container id '{containerId}' — matches {prefixMatches.Count} containers; use the full id or name.");
        return (null, $"Container not found: {containerId}");
    }
}
