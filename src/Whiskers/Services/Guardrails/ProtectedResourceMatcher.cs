using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Whiskers.Services.Guardrails;

/// <summary>
/// Finds a protected resource in a guardrail request (INV-24, R1-16). Every argument value is matched whole, as
/// before, and every path-like word of every argument against the protected paths (a volume list such as
/// <c>/var/run/docker.sock:/var/run/docker.sock</c> included). Free text — a command, SQL, a compose file — and
/// lists of references (volumes, mounts) are also split into words, and each word (and each segment of a path) is
/// matched on its own, so <c>docker stop whiskers</c> or <c>DROP DATABASE whiskers</c> is caught.
/// <para>A shell command is read the way the shell reads it: quotes and escapes are removed and the pieces of one
/// word joined (<c>whis""kers</c> is <c>whiskers</c>), and quoted text that is itself run as a command is read
/// again. A command is analysable only when every word is literal: anything the shell expands at run time
/// (<c>$VAR</c>, <c>$(…)</c>, backticks, brace expansion), an unterminated quote, a word built by <c>xargs</c>,
/// <c>eval</c> or decoding, or a glob that could reach a protected name or path, makes it unanalysable. SQL is
/// unanalysable when it builds statements at run time (dynamic SQL) or reaches the file system or a shell. Free text
/// that cannot be analysed counts as touching a protected resource, so it is refused or needs the same explicit
/// approval.</para>
/// </summary>
public static class ProtectedResourceMatcher
{
    /// <summary>Arguments whose value is free text (the same names the audit withholds as payloads, R1-12).</summary>
    public static readonly IReadOnlySet<string> FreeTextArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "command", "query", "sql", "script", "content", "body", "compose", "composeContent", "yaml",
    };

    /// <summary>Free text the shell runs.</summary>
    public static readonly IReadOnlySet<string> ShellArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "command", "script",
    };

    /// <summary>Free text a database runs.</summary>
    public static readonly IReadOnlySet<string> SqlArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "query", "sql",
    };

    /// <summary>Arguments that list resources by name or path (a deployment's volumes, a plan's mounts).</summary>
    public static readonly IReadOnlySet<string> ReferenceArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "volumes", "mounts", "binds",
    };

    /// <summary>A hit: the value (a word of free text, or a whole argument) and the pattern it matched; for free text
    /// that cannot be analysed, the argument's name and no pattern. <see cref="InFreeText"/> marks a word taken from a
    /// payload (a reason must not quote it).</summary>
    public sealed record Hit(string Value, string? Pattern, string? UnanalyzableArgument = null)
    {
        public string? Argument { get; init; }
        public bool InFreeText { get; init; }
    }

    /// <summary>The first protected resource the request touches, or <c>null</c>. <paramref name="patterns"/> are
    /// matched against names, <paramref name="pathPatterns"/> against path-like words.</summary>
    public static Hit? FirstHit(IReadOnlyDictionary<string, JsonElement> arguments, IReadOnlyList<string> patterns,
        IReadOnlyList<string> pathPatterns, bool analyseFreeText)
    {
        if (patterns.Count == 0 && pathPatterns.Count == 0) return null;
        foreach (var (key, element) in arguments)
        {
            var value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
            if (Match(value, patterns) is { } whole) return new Hit(value, whole) { Argument = key };
            if (value.Length == 0) continue;
            var freeText = analyseFreeText && FreeTextArguments.Contains(key);
            var named = freeText || (analyseFreeText && ReferenceArguments.Contains(key));
            if (freeText && ShellArguments.Contains(key))
            {
                var shell = AnalyseShell(value);
                foreach (var word in shell.Words.SelectMany(Words))
                    if (WordHit(word, patterns, pathPatterns, names: true) is { } hit)
                        return hit with { Argument = key, InFreeText = true };
                foreach (var glob in shell.Globs)
                    if (GlobHit(glob, patterns, pathPatterns) is { } hit)
                        return hit with { Argument = key, InFreeText = true };
                if (shell.Unanalyzable) return new Hit("", null, key) { Argument = key, InFreeText = true };
                continue;
            }
            foreach (var word in Words(value))
                if (WordHit(word, patterns, pathPatterns, named) is { } hit)
                    return hit with { Argument = key, InFreeText = freeText };
            if (freeText && SqlArguments.Contains(key) && IsUnanalyzableSql(value))
                return new Hit("", null, key) { Argument = key, InFreeText = true };
        }
        return null;
    }

    private static Hit? WordHit(string word, IReadOnlyList<string> patterns, IReadOnlyList<string> pathPatterns, bool names)
    {
        if (names && Match(word, patterns) is { } named) return new Hit(word, named);
        if (word.Contains('/') && Match(word.TrimEnd('/'), pathPatterns) is { } path) return new Hit(word, path);
        return null;
    }

    /// <summary>A word the shell expands as a glob hits when it could expand to a protected name or path.</summary>
    private static Hit? GlobHit(string glob, IReadOnlyList<string> patterns, IReadOnlyList<string> pathPatterns)
    {
        var asGlob = Regex.Replace(glob, @"\[[^\]]*\]", "?");
        // A glob of nothing but wildcards (the arguments of `rm *`, `count(*)` in quoted SQL) names no resource.
        if (!asGlob.Contains('/') && asGlob.Any(ch => ch is not ('*' or '?')))
            foreach (var pattern in patterns)
                if (GuardrailRuleHelpers.GlobMatch(asGlob, pattern.Replace("*", "").Replace("?", "")))
                    return new Hit(glob, pattern);
        if (!asGlob.Contains('/')) return null;
        foreach (var pattern in pathPatterns)
        {
            foreach (var sample in Samples(pattern))
                if (GuardrailRuleHelpers.GlobMatch(asGlob.TrimEnd('/'), sample))
                    return new Hit(glob, pattern);
            // A wildcard segment that could be the protected directory itself (/opt/*/ca.key, /app/da?a/…).
            var protectedSegment = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(x => x != "*");
            if (protectedSegment is not null
                && asGlob.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(seg => seg.IndexOfAny(['*', '?']) >= 0 && GuardrailRuleHelpers.GlobMatch(seg, protectedSegment)))
                return new Hit(glob, pattern);
        }
        return null;
    }

    // Concrete paths a protected path pattern covers, for testing whether a glob could reach one: the leading '*'
    // stands for any parent directory.
    private static IEnumerable<string> Samples(string pathPattern)
    {
        var literal = pathPattern.TrimEnd('*').TrimEnd('/');
        if (!literal.StartsWith('*'))
        {
            yield return literal;
            yield return literal + "/x";
            yield break;
        }
        var tail = literal.TrimStart('*');
        foreach (var parent in new[] { "", "/opt", "/etc", "/srv", "/root", "/home/x", "/app/data", "/var/lib", "/data" })
        {
            yield return parent + tail;
            yield return parent + tail + "/x";
        }
    }

    private static string? Match(string value, IReadOnlyList<string> patterns)
    {
        if (value.Length == 0) return null;
        foreach (var pattern in patterns)
            if (GuardrailRuleHelpers.GlobMatch(pattern, value)) return pattern;
        return null;
    }

    // Shell and SQL punctuation that separates words; '/' separates path segments (a path is also kept whole).
    private static readonly char[] Separators =
        [' ', '\t', '\r', '\n', ';', '|', '&', '(', ')', '<', '>', '\'', '"', '`', ',', '=', '{', '}', '[', ']', ':', '$', '\\', '!', '*', '?', '#', '+'];

    /// <summary>The words of a value, and each segment of a path-like word.</summary>
    public static IEnumerable<string> Words(string text)
    {
        foreach (var word in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return word;
            if (!word.Contains('/')) continue;
            foreach (var segment in word.Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (segment != word) yield return segment;
        }
    }

    /// <summary>The words a container id could be written as in an argument: the shell's words for a command, else
    /// the plain words.</summary>
    public static IEnumerable<string> MentionWords(string argument, string text) =>
        ShellArguments.Contains(argument) ? AnalyseShell(text).Words.SelectMany(Words) : Words(text);

    /// <summary>Whether a command invokes a container runtime directly, where a short id prefix is meaningful.</summary>
    public static bool InvokesContainerRuntime(string argument, string text) =>
        ShellArguments.Contains(argument)
        && AnalyseShell(text).Words.Any(w => w is "docker" or "podman" or "nerdctl" or "ctr" or "crictl"
                                             || w.EndsWith("/docker", StringComparison.Ordinal));

    /// <summary>What the shell makes of a command: its literal words (quotes and escapes removed, with the words of
    /// quoted text that is run again), the words it expands as globs, and whether anything is built at run time.</summary>
    public sealed record ShellText(IReadOnlyList<string> Words, IReadOnlyList<string> Globs, bool Unanalyzable);

    private const int MaxShellDepth = 4;

    public static ShellText AnalyseShell(string text)
    {
        var words = new List<string>();
        var globs = new List<string>();
        var unanalyzable = Tokenize(text, words, globs, 0);
        if (words.Any(w => ShellRunTime.Contains(w))) unanalyzable = true;
        // Inline code of another interpreter builds what it runs from strings the shell never sees.
        for (var w = 0; w + 1 < words.Count && !unanalyzable; w++)
            if (Interpreters.Contains(Path.GetFileName(words[w])) && words[w + 1] is "-c" or "-e" or "-E" or "--eval" or "-r" or "-p")
                unanalyzable = true;
        try { unanalyzable |= ShellUnanalyzable.IsMatch(text); }
        catch (RegexMatchTimeoutException) { unanalyzable = true; }
        return new ShellText(words, globs, unanalyzable);
    }

    // Commands that build what they act on from data at run time.
    private static readonly HashSet<string> ShellRunTime = new(StringComparer.Ordinal)
    {
        "eval", "xargs", "base64", "xxd", "source", "parallel", "envsubst",
    };

    // Interpreters whose inline code (-c / -e) is not shell; sh -c '…' is read again instead.
    private static readonly HashSet<string> Interpreters = new(StringComparer.Ordinal)
    {
        "python", "python2", "python3", "perl", "ruby", "node", "nodejs", "php", "lua", "tclsh", "pwsh", "powershell",
    };

    private static readonly Regex ShellUnanalyzable = new(
        @"\bopenssl\s+(enc|base64)\b|\bprintf\b[^\n]*%b|\\x[0-9a-f]{2}|\\[0-7]{3}|\bsystem\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Splits <paramref name="text"/> into shell words. Returns true when something in it is expanded at run
    /// time. Quoted text with shell syntax in it is read again as a command (<c>sh -c '…'</c>, <c>ssh host "…"</c>).</summary>
    private static bool Tokenize(string text, List<string> words, List<string> globs, int depth)
    {
        if (depth > MaxShellDepth) return true;
        var unanalyzable = false;
        var word = new StringBuilder();
        var quotedPart = new StringBuilder();
        bool inWord = false, glob = false, quoted = false;
        var i = 0;

        void End()
        {
            if (!inWord) return;
            var w = word.ToString();
            words.Add(w);
            if (glob) globs.Add(w);
            // Quoted text that looks like a command line is read again: it may be run by sh -c, ssh, docker exec …
            if (quoted && quotedPart.Length > 0 && LooksLikeCommand(quotedPart.ToString()))
                unanalyzable |= Tokenize(quotedPart.ToString(), words, globs, depth + 1);
            word.Clear();
            quotedPart.Clear();
            inWord = glob = quoted = false;
        }

        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case ' ' or '\t' or '\r' or '\n' or ';' or '|' or '&' or '(' or ')' or '<' or '>':
                    End();
                    i++;
                    continue;
                case '\'':
                {
                    var close = text.IndexOf('\'', i + 1);
                    if (close < 0) return true;
                    var part = text[(i + 1)..close];
                    word.Append(part);
                    quotedPart.Append(part);
                    inWord = quoted = true;
                    i = close + 1;
                    continue;
                }
                case '"':
                {
                    i++;
                    var part = new StringBuilder();
                    var closed = false;
                    while (i < text.Length)
                    {
                        var d = text[i];
                        if (d == '"') { closed = true; i++; break; }
                        if (d is '$' or '`') unanalyzable = true;
                        if (d == '\\' && i + 1 < text.Length && text[i + 1] is '$' or '`' or '"' or '\\' or '\n')
                        {
                            part.Append(text[i + 1]);
                            i += 2;
                            continue;
                        }
                        part.Append(d);
                        i++;
                    }
                    if (!closed) return true;
                    word.Append(part);
                    quotedPart.Append(part);
                    inWord = quoted = true;
                    continue;
                }
                case '\\':
                    if (i + 1 < text.Length)
                    {
                        if (text[i + 1] != '\n') word.Append(text[i + 1]);
                        inWord = true;
                    }
                    i += 2;
                    continue;
                case '$' or '`':
                    unanalyzable = true;
                    word.Append(c);
                    inWord = true;
                    i++;
                    continue;
                case '{' when BraceExpansion(text, i):
                    unanalyzable = true;
                    word.Append(c);
                    inWord = true;
                    i++;
                    continue;
                case '*' or '?' or '[':
                    glob = true;
                    word.Append(c);
                    inWord = true;
                    i++;
                    continue;
                case '#' when !inWord:
                {
                    // A comment runs to the end of the line.
                    var end = text.IndexOf('\n', i);
                    i = end < 0 ? text.Length : end;
                    continue;
                }
                default:
                    word.Append(c);
                    inWord = true;
                    i++;
                    continue;
            }
        }
        End();
        return unanalyzable;
    }

    private static bool LooksLikeCommand(string text) =>
        text.IndexOfAny([' ', '\t', '\n', ';', '|', '&', '$', '`', '"', '\'', '\\']) >= 0;

    // {a,b} and {1..3} are expanded by the shell; a lone brace (find -exec {} \;, awk '{…}' quoted) is not.
    private static bool BraceExpansion(string text, int open)
    {
        var close = text.IndexOf('}', open + 1);
        if (close < 0) return false;
        var inner = text[(open + 1)..close];
        return inner.Contains(',') || inner.Contains("..");
    }

    // SQL that builds a statement at run time, or reaches the file system or a shell.
    private static readonly Regex UnanalyzableSql = new(
        @"\bexecute\b|\bexec\s*\(|\bexec\s+(sp_|xp_)|\bprepare\b|\bcopy\b[^;]*\bprogram\b|\bpg_read_(binary_)?file\b"
        + @"|\blo_(import|export)\b|\bdblink|\bchr\s*\(|\bconvert_from\b|\bdecode\s*\(|\bxp_cmdshell\b|\bload_file\b"
        + @"|\binto\s+(out|dump)file\b|\bunhex\s*\(|\bfrom_base64\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Whether SQL builds what it acts on at run time. A pattern that takes too long counts as unanalysable
    /// (fail closed).</summary>
    public static bool IsUnanalyzableSql(string text)
    {
        try { return UnanalyzableSql.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    /// <summary>Whether a shell command builds what it acts on at run time.</summary>
    public static bool IsUnanalyzableShell(string text) => AnalyseShell(text).Unanalyzable;
}
