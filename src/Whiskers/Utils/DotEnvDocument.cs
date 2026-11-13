namespace Whiskers.Utils;

/// <summary>One line of a compose <c>.env</c> file: a variable (<see cref="Key"/> set) or anything else — a comment, a
/// blank line, a line without <c>=</c> — kept exactly as it was written.</summary>
public sealed record DotEnvLine(string Raw, string? Key, string? Value);

/// <summary>A variable as the editor hands it back: <see cref="Line"/> is its line in the file it was read from, null
/// for a variable added in the editor.</summary>
public sealed record DotEnvEdit(int? Line, string Key, string Value);

/// <summary>
/// Reads and writes a compose <c>.env</c> file for the container page (review of main): the editor shows the
/// variables only, so writing the file back must keep every other line — comments, blank lines, anything it does not
/// understand — where it was. A variable removed in the editor drops its line; an added one goes at the end.
/// </summary>
public static class DotEnvDocument
{
    /// <summary>The host command that reads the file: its first line says whether the file exists, so a missing file
    /// and a failed read are told apart.</summary>
    public static string ReadCommand(string quotedPath) =>
        $"if [ -e {quotedPath} ]; then echo present && cat {quotedPath}; else echo absent; fi";

    /// <summary>What <see cref="ReadCommand"/> returned: the file's lines; an empty list with <c>Missing</c> when there
    /// is no file; null when the read failed — then nothing may be written, or saving would replace a file nobody saw.
    /// </summary>
    public static IReadOnlyList<DotEnvLine>? FromRead(bool success, string? output, out bool missing)
    {
        missing = false;
        if (!success || output is null) return null;
        var firstBreak = output.IndexOf('\n');
        var head = (firstBreak < 0 ? output : output[..firstBreak]).Trim();
        if (head == "absent")
        {
            missing = true;
            return [];
        }
        return head == "present" ? Parse(firstBreak < 0 ? "" : output[(firstBreak + 1)..]) : null;
    }

    public static IReadOnlyList<DotEnvLine> Parse(string content)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);      // the file's final line break, not a blank line

        return lines.Select(raw =>
        {
            var trimmed = raw.Trim();
            var idx = trimmed.IndexOf('=');
            return trimmed.StartsWith('#') || idx <= 0
                ? new DotEnvLine(raw, null, null)
                : new DotEnvLine(raw, trimmed[..idx], trimmed[(idx + 1)..]);
        }).ToList();
    }

    public static string Render(IReadOnlyList<DotEnvLine> original, IEnumerable<DotEnvEdit> edits)
    {
        var list = edits.Where(e => !string.IsNullOrWhiteSpace(e.Key)).ToList();
        var byLine = list.Where(e => e.Line is not null).ToDictionary(e => e.Line!.Value);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < original.Count; i++)
        {
            if (original[i].Key is null)
                sb.Append(original[i].Raw).Append('\n');
            else if (byLine.TryGetValue(i, out var edit))
                sb.Append(edit.Key).Append('=').Append(edit.Value).Append('\n');
            // else: removed in the editor
        }
        foreach (var added in list.Where(e => e.Line is null))
            sb.Append(added.Key).Append('=').Append(added.Value).Append('\n');
        return sb.ToString();
    }
}
