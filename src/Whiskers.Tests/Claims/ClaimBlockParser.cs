using System.Text.RegularExpressions;
using Whiskers.Tests.ReleaseGate;

namespace Whiskers.Tests.Claims;

/// <summary>One block of a governed document (D11) and its marker.</summary>
/// <param name="Line">1-based first line.</param>
/// <param name="Text">The block's lines as written (markers included), joined with '\n'.</param>
/// <param name="ClaimIds">The ids of a <c>claim:</c> marker.</param>
/// <param name="FreeReason">The reason of a <c>claim-free:</c> marker.</param>
/// <param name="Markers">How many marker comments the block holds.</param>
public sealed record ClaimBlock(int Line, string Kind, string Text, IReadOnlyList<string> ClaimIds, string? FreeReason, int Markers)
{
    public string Normalized => ClaimRules.Normalize(Text);
    public string Fingerprint => ClaimRules.Fingerprint(Text);
    public bool IsClaimShaped => ClaimRules.IsClaimShaped(Text);
    public bool IsMarked => Markers > 0;

    /// <summary>The first words, for messages and the register's "Starts with" column.</summary>
    public string StartsWith => Normalized.Length <= 40 ? Normalized : Normalized[..40].TrimEnd();
}

/// <summary>
/// Splits a governed document into blocks (contract 0011, D11): a paragraph, a list item with its continuation lines
/// (a nested item is its own block), a table body row, a fenced code block. Headings, table header rows, HTML comments
/// and blocks of lines that hold only links or images are not blocks. A marker belongs to the block it ends; a fenced
/// block takes its marker from a comment line right after the closing fence.
/// </summary>
public static class ClaimBlockParser
{
    private static readonly Regex ListItem = new(@"^\s*(?:[-*+]|\d+[.)])\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}(?:\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex CommentLine = new(@"^\s*<!--.*-->\s*$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<ClaimBlock> Parse(string markdown)
    {
        var lines = MarkdownTables.Lines(markdown);
        var fenced = MarkdownTables.Fenced(lines);
        var blocks = new List<ClaimBlock>();
        var current = new List<int>();

        void Flush(string kind)
        {
            if (current.Count == 0) return;
            if (!current.All(i => ClaimRules.IsLinkOnly(lines[i]) || CommentLine.IsMatch(lines[i])))
                blocks.Add(Make(lines, current, kind));
            current = [];
        }

        var kindOfCurrent = "paragraph";
        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];
            if (fenced[i])
            {
                Flush(kindOfCurrent);
                var start = i;
                var marker = FenceMarker(line);
                i++;
                // A fence runs to its closing line (the first fenced line, after the opening, that closes it).
                while (i < lines.Length && fenced[i] && !(FenceMarker(lines[i]) is { } close && close.Char == marker!.Value.Char
                                                             && close.Length >= marker.Value.Length && close.Tail.Length == 0))
                    i++;
                if (i < lines.Length && fenced[i]) i++; // the closing fence
                var range = Enumerable.Range(start, i - start).ToList();
                if (i < lines.Length && ClaimRules.Marker.IsMatch(lines[i]) && CommentLine.IsMatch(lines[i])) range.Add(i++);
                blocks.Add(Make(lines, range, "fence"));
                continue;
            }
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || Heading.IsMatch(line))
            {
                Flush(kindOfCurrent);
                i++;
                continue;
            }
            if (trimmed.StartsWith('|'))
            {
                Flush(kindOfCurrent);
                i++; // the header row is not a block
                if (i < lines.Length && IsSeparator(lines[i])) i++;
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|') && !fenced[i])
                {
                    blocks.Add(Make(lines, [i], "row"));
                    i++;
                }
                continue;
            }
            if (CommentLine.IsMatch(line) && current.Count == 0)
            {
                i++; // an HTML comment on its own is no block
                continue;
            }
            if (ListItem.IsMatch(line))
            {
                Flush(kindOfCurrent);
                kindOfCurrent = "item";
                current.Add(i++);
                continue;
            }
            if (current.Count == 0) kindOfCurrent = "paragraph";
            current.Add(i++);
        }
        Flush(kindOfCurrent);
        return blocks;
    }

    private static ClaimBlock Make(string[] lines, List<int> range, string kind)
    {
        var text = string.Join("\n", range.Select(i => lines[i]));
        var markers = ClaimRules.Marker.Matches(text);
        var ids = new List<string>();
        string? reason = null;
        foreach (Match m in markers)
        {
            if (m.Groups["free"].Success) reason = m.Groups["value"].Value;
            else ids.AddRange(m.Groups["value"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
        return new ClaimBlock(range[0] + 1, kind, text, ids, reason, markers.Count);
    }

    private static (char Char, int Length, string Tail)? FenceMarker(string line)
    {
        var trimmed = line.TrimStart(' ');
        if (line.Length - trimmed.Length > 3 || trimmed.Length < 3 || trimmed[0] is not ('`' or '~')) return null;
        var run = trimmed.TakeWhile(c => c == trimmed[0]).Count();
        return run < 3 ? null : (trimmed[0], run, trimmed[run..].Trim());
    }

    private static bool IsSeparator(string line)
        => MarkdownTables.Split(line) is var cells && cells.All(c => c.Length > 0 && c.Trim(':', '-').Length == 0);
}
