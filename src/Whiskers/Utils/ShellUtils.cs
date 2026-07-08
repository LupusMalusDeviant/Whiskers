namespace Whiskers.Utils;

/// <summary>
/// Helpers for building shell commands safely and for bounding command output.
/// </summary>
public static class ShellUtils
{
    /// <summary>
    /// POSIX single-quote a value for safe interpolation into a shell command string.
    /// Wraps in single quotes and escapes embedded single quotes via the '\'' idiom.
    /// Use this whenever a caller-supplied value (package name, image, path, container id)
    /// is spliced into a command that a shell will interpret.
    /// </summary>
    public static string Quote(string s) => "'" + (s ?? "").Replace("'", "'\\''") + "'";

    /// <summary>
    /// Truncates text to <paramref name="max"/> characters, appending a marker noting how much
    /// was cut. Used to keep MCP tool responses from blowing up the model context with huge logs.
    /// </summary>
    public static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s ?? "";
        var cut = s.Length - max;
        return s[..max] + $"\n... ({cut} chars truncated)";
    }
}
