using System.Text.RegularExpressions;

namespace ServerWatch.Mcp.Tools;

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
}
