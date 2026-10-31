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
        var kept = CutAtBoundary(s, max);
        return kept + $"\n... ({s.Length - kept.Length} chars truncated)";
    }

    /// <summary>At most <paramref name="max"/> characters of <paramref name="s"/>, ending at a complete line or word.
    /// A raw cut can fall inside a credential (https://user:secr…), where no redaction pattern recognises what is
    /// left (R1-24); text without any boundary before the limit is dropped.</summary>
    public static string CutAtBoundary(string s, int max)
    {
        if (s.Length <= max) return s;
        // The limit falls just before whitespace: the last token is complete.
        return char.IsWhiteSpace(s[max]) ? s[..max] : DropIncompleteTail(s[..max]);
    }

    /// <summary>Text cut at an unknown place, without its incomplete last line (or word, for one long line); empty
    /// when it has no boundary at all.</summary>
    public static string DropIncompleteTail(string head)
    {
        var end = head.LastIndexOf('\n');
        if (end < 0) end = head.AsSpan().LastIndexOfAny(" \t\r\f\v");
        return end < 0 ? "" : head[..end];
    }
}
