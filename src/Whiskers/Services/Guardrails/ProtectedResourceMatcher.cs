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
    /// <param name="nameKeys">The arguments <paramref name="patterns"/> apply to; all when <c>null</c>. Paths and
    /// unanalysable free text are checked in every argument.</param>
    public static Hit? FirstHit(IReadOnlyDictionary<string, JsonElement> arguments, IReadOnlyList<string> patterns,
        IReadOnlyList<string> pathPatterns, bool analyseFreeText, Func<string, bool>? nameKeys = null)
    {
        if (patterns.Count == 0 && pathPatterns.Count == 0) return null;
        foreach (var (key, element) in arguments)
        {
            var value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
            // The resolved target's kind (container, whiskers, host …) is a category, not a name.
            if (key == ResolvedKindKey) continue;
            var keyPatterns = nameKeys is null || nameKeys(key) ? patterns : [];
            // A whole payload (SQL, a command) that matches is never quoted either: it can carry a credential.
            if (Match(value, keyPatterns) is { } whole)
                return new Hit(value, whole)
                {
                    Argument = key,
                    InFreeText = PayloadArguments.Contains(key) || key.StartsWith(ResolvedWithheldPrefix, StringComparison.Ordinal),
                };
            if (value.Length == 0) continue;
            var freeText = analyseFreeText && FreeTextArguments.Contains(key);
            var reference = analyseFreeText && (ReferenceArguments.Contains(key) || key.StartsWith(ResolvedRelatedPrefix, StringComparison.Ordinal));
            var named = freeText || reference;
            if (reference)
            {
                // A mount names what it reads on its source side only ('/srv/app:/app/data' mounts /srv/app); a
                // directory above a protected path (/, /var/run, /var/lib/docker) reaches into it.
                foreach (var source in Sources(value))
                {
                    if (AncestorHit(source, pathPatterns) is { } above) return above with { Argument = key, InFreeText = true };
                    foreach (var word in Words(source))
                        if (WordHit(word, keyPatterns, pathPatterns, names: true) is { } hit)
                            return hit with { Argument = key, InFreeText = true };
                }
                continue;
            }
            if (freeText && ShellArguments.Contains(key))
            {
                var shell = AnalyseShell(value);
                foreach (var word in shell.Words.SelectMany(Words))
                    if (WordHit(word, keyPatterns, pathPatterns, names: true) is { } hit)
                        return hit with { Argument = key, InFreeText = true };
                foreach (var glob in shell.Globs)
                    if (GlobHit(glob, keyPatterns, pathPatterns) is { } hit)
                        return hit with { Argument = key, InFreeText = true };
                // A command that removes, moves or changes permissions of a directory above a protected path
                // (rm -rf /var/lib/docker) destroys it; merely reading one (df -h /var/lib/docker) does not.
                if (shell.Words.Any(w => ModifyingCommands.Contains(Path.GetFileName(w))))
                    foreach (var word in shell.Words.Where(w => !w.StartsWith('-')))
                    {
                        if (word.StartsWith('/') && AncestorHit(word, pathPatterns) is { } above)
                            return above with { Argument = key, InFreeText = true };
                        // A relative operand (cd /app/data && rm -rf mtls) is judged by the directory it names.
                        if (!word.StartsWith('/') && ProtectedSegment(word, pathPatterns) is { } segment)
                            return new Hit(word, segment) { Argument = key, InFreeText = true };
                    }
                if (shell.Unanalyzable) return new Hit("", null, key) { Argument = key, InFreeText = true };
                continue;
            }
            // A compose file is read as compose reads it: ${VAR:-default} becomes its default, an unset variable empty.
            if (freeText && ComposeArguments.Contains(key))
            {
                // The raw file of the UI deploy is not planned: a bare variable may be set by the target directory's
                // .env, which is not seen here, so its value is unknown.
                if (ExpandComposeVariables(value) is not { } expanded
                    || (key.Equals("yaml", StringComparison.OrdinalIgnoreCase) && BareComposeVariable.IsMatch(value)))
                    return new Hit("", null, key) { Argument = key, InFreeText = true };
                value = expanded;
                // A bind mount's host side above a protected path, read from the YAML itself (flow style, quoting and
                // escapes decoded); a file that is no YAML cannot be read for its mounts.
                if (ReadCompose(value) is not { } compose)
                    return new Hit("", null, key) { Argument = key, InFreeText = true };
                foreach (var source in compose.Sources)
                    if (AncestorHit(source, pathPatterns) is { } above)
                        return above with { Argument = key, InFreeText = true };
                // Names and paths are looked for in the decoded values too (an escaped "docker.sock").
                value = value + "\n" + string.Join('\n', compose.Scalars);
            }
            foreach (var word in Words(value))
                if (WordHit(word, keyPatterns, pathPatterns, named) is { } hit)
                    return hit with { Argument = key, InFreeText = true };
            if (freeText && SqlArguments.Contains(key) && IsUnanalyzableSql(value))
                return new Hit("", null, key) { Argument = key, InFreeText = true };
        }
        return null;
    }

    /// <summary>Arguments whose value is a payload that can carry a credential: free text and secrets. A hit in one
    /// names the argument, never its value.</summary>
    public static readonly IReadOnlySet<string> PayloadArguments = new HashSet<string>(
        FreeTextArguments.Concat(["env", "envVars", "password", "secret", "token", "mounts"]), StringComparer.OrdinalIgnoreCase);

    /// <summary>Free text that is a compose file.</summary>
    public static readonly IReadOnlySet<string> ComposeArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "yaml", "compose", "composeContent",
    };

    // Commands that remove, move or take away a directory tree.
    private static readonly HashSet<string> ModifyingCommands = new(StringComparer.Ordinal)
    {
        "rm", "rmdir", "mv", "chmod", "chown", "chgrp", "chattr", "shred", "truncate", "rsync", "find", "mount",
        "umount", "ln", "unlink", "setfacl",
    };

    private static readonly Regex BareComposeVariable = new(@"(?<!\$)\$\{[A-Za-z_][A-Za-z0-9_]*\}|(?<!\$)\$[A-Za-z_]",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>A compose file read as YAML: every absolute host path it can mount, wherever it stands (services,
    /// anchors, merges, extensions) — the source side of a <c>volumes</c> entry, a long entry's <c>source</c>, a
    /// volume's <c>driver_opts</c> <c>device</c> — and every scalar, decoded. <c>null</c> when the text is no YAML.</summary>
    public static (IReadOnlyList<string> Sources, IReadOnlyList<string> Scalars)? ReadCompose(string yaml)
    {
        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (YamlDotNet.Core.YamlException) { return null; }
        var sources = new List<string>();
        var scalars = new List<string>();
        foreach (var document in stream.Documents)
            CollectSources(document.RootNode, null, sources, scalars, 0);
        return (sources, scalars);
    }

    private static void CollectSources(YamlDotNet.RepresentationModel.YamlNode node, string? key, List<string> sources,
        List<string> scalars, int depth)
    {
        if (depth > 64) return;
        switch (node)
        {
            case YamlDotNet.RepresentationModel.YamlScalarNode scalar:
                if (scalar.Value is { Length: > 0 } text) scalars.Add(text);
                break;
            case YamlDotNet.RepresentationModel.YamlMappingNode map:
                foreach (var (k, v) in map.Children)
                {
                    var name = (k as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value;
                    if (name is "source" or "device" && v is YamlDotNet.RepresentationModel.YamlScalarNode s)
                        Add(s.Value);
                    if (name is not null) scalars.Add(name);
                    CollectSources(v, name, sources, scalars, depth + 1);
                }
                break;
            case YamlDotNet.RepresentationModel.YamlSequenceNode seq:
                foreach (var item in seq.Children)
                {
                    // "- /src:/dst[:ro]": the host side is before the first colon.
                    if (key == "volumes" && item is YamlDotNet.RepresentationModel.YamlScalarNode s && s.Value is { } v)
                        Add(v.Contains(':') ? v[..v.IndexOf(':')] : v);
                    CollectSources(item, key, sources, scalars, depth + 1);
                }
                break;
        }

        void Add(string? path)
        {
            if (path?.Trim() is { Length: > 0 } p && p.StartsWith('/')) sources.Add(p);
        }
    }

    private static readonly Regex ComposeVariable = new(
        @"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:(?<op>:?[-?+])(?<word>[^{}$]*))?\}|\$(?<bare>[A-Za-z_][A-Za-z0-9_]*)|\$\$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>A compose file with its variables replaced the way compose would without an <c>.env</c>: a default
    /// (<c>:-</c>, <c>-</c>) or alternative (<c>:+</c>, <c>+</c>) by its word, anything else by nothing — both, since
    /// whether a variable is set is not known here. <c>null</c> when a variable cannot be read (nested, cut off).</summary>
    public static string? ExpandComposeVariables(string yaml)
    {
        try
        {
            var expanded = ComposeVariable.Replace(yaml, m =>
            {
                if (m.Value == "$$") return "$";
                var word = m.Groups["word"].Success ? m.Groups["word"].Value : "";
                return m.Groups["op"].Value.EndsWith('?') ? "" : word;
            });
            return expanded.Contains("${", StringComparison.Ordinal) ? null : expanded;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>The argument the policy adapter carries the resolved target's kind in (R1-16).</summary>
    public const string ResolvedKindKey = "__resolvedKind";

    // The protected directory a relative path ends in or passes through (mtls, dockerproxy/certs, step-ca).
    private static string? ProtectedSegment(string relative, IReadOnlyList<string> pathPatterns)
    {
        var segments = NormalizePath(relative).Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pattern in pathPatterns)
        {
            var name = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(x => x != "*");
            if (name is null || name.Contains('*')) continue;
            if (segments.Any(s => string.Equals(s, name, StringComparison.Ordinal))) return pattern;
        }
        return null;
    }

    /// <summary>The prefix of the resolved target's related resources the policy adapter adds (R1-06, R1-16).</summary>
    public const string ResolvedRelatedPrefix = "__resolvedRelated";

    /// <summary>The prefix of a related resource that is decided but never quoted (a mount from an <c>.env</c> value,
    /// <see cref="Whiskers.Services.Mutations.MutationTarget.Withheld"/>); it starts with
    /// <see cref="ResolvedRelatedPrefix"/>, so it is matched like the others.</summary>
    public const string ResolvedWithheldPrefix = "__resolvedRelatedWithheld";

    /// <summary>The first free-text argument that cannot be analysed (a command or SQL completed at run time, a
    /// compose file that is no YAML), found whatever else the arguments name; <c>null</c> when there is none.</summary>
    public static Hit? FirstUnanalyzable(IReadOnlyDictionary<string, JsonElement> arguments) =>
        FirstHit(arguments, [NoName], [], analyseFreeText: true) is { UnanalyzableArgument: not null } hit ? hit : null;

    // A pattern no word matches: FirstHit then finds only what cannot be analysed.
    private const string NoName = "\u0000";

    // The source side of each entry of a mount list ("src:dst[:mode]", comma- or line-separated).
    private static IEnumerable<string> Sources(string value)
    {
        foreach (var entry in value.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = entry.IndexOf(':');
            yield return colon > 0 ? entry[..colon] : entry;
        }
    }

    // A mount of a directory that contains a protected path: the root for any of them, and every parent of an
    // absolute one (/var/run for the Docker socket, /var/lib/docker for Whiskers' volume).
    private static Hit? AncestorHit(string source, IReadOnlyList<string> pathPatterns)
    {
        if (!source.StartsWith('/')) return null;
        var path = NormalizePath(source);
        foreach (var pattern in pathPatterns)
        {
            if (path == "/") return new Hit(source, pattern);
            if (pattern.StartsWith('*')) continue;
            var literal = pattern.TrimEnd('*').TrimEnd('/');
            if (literal.StartsWith(path + "/", StringComparison.Ordinal)) return new Hit(source, pattern);
        }
        return null;
    }

    /// <summary>A path as the kernel resolves it: repeated slashes and <c>.</c> collapsed, <c>..</c> applied.</summary>
    public static string NormalizePath(string path)
    {
        var absolute = path.StartsWith('/');
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        var joined = string.Join('/', parts);
        return absolute ? "/" + joined : joined;
    }

    private static Hit? WordHit(string word, IReadOnlyList<string> patterns, IReadOnlyList<string> pathPatterns, bool names)
    {
        if (names && Match(word, patterns) is { } named) return new Hit(word, named);
        if (word.Contains('/') && Match(NormalizePath(word), pathPatterns) is { } path) return new Hit(word, path);
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

    /// <summary>The words of a command that can be a container id: every word except an option or a number given to
    /// one (<c>--tail 100</c>), an image reference, a path, an assignment or a file name, and a single digit before a
    /// subcommand that takes containers; plus the container of a Docker API path. The command <c>exec</c> runs inside a
    /// container is read too (its option values cannot be told from the container reliably), so a word there that
    /// happens to start a protected container's id refuses the call — never the other way round.</summary>
    public static IEnumerable<string> RuntimeIdWords(string text)
    {
        var words = AnalyseShell(text).Words;
        var takesContainer = false;   // a subcommand that takes containers was seen since the runtime word
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var previous = i > 0 ? words[i - 1] : "";
            // The Docker API reached directly names its container in the path (/containers/c0ffee/stop).
            if (RuntimeApiTarget.Match(w) is { Success: true } api)
            {
                yield return api.Groups[1].Value;
                continue;
            }
            if (IsRuntimeWord(w))
            {
                takesContainer = false;
                continue;
            }
            if (!takesContainer && ContainerSubcommands.Contains(w))
            {
                takesContainer = true;
                continue;
            }
            // Options, a number given to an option (--tail 100), image references (redis:7), paths, assignments
            // and file names name no container; `container:/path` (docker cp) does by its first part.
            if (w.StartsWith('-') || (NumericOptions.Contains(previous) && w.All(ch => char.IsAsciiDigit(ch) || ch is ':' or '.' or '/' or '-')))
                continue;
            var candidate = w.Contains(":/", StringComparison.Ordinal) ? w[..w.IndexOf(':')]
                : w.IndexOfAny([':', '/', '=', '.', '@']) >= 0 ? null
                : w;
            if (candidate is null) continue;
            // A single digit is an id prefix only where a command takes a container (docker stop -t 10 7).
            if (candidate.Length < 2 && candidate.All(char.IsAsciiDigit) && !takesContainer)
                continue;
            yield return candidate;
        }
    }

    /// <summary>Whether a command invokes a container runtime directly, where a short id prefix is meaningful.</summary>
    public static bool InvokesContainerRuntime(string argument, string text) =>
        ShellArguments.Contains(argument)
        && (AnalyseShell(text).Words.Any(IsRuntimeWord)
            // the Docker API reached directly (curl to a socket proxy or a TCP endpoint)
            || RuntimeApi.IsMatch(text));

    private static readonly Regex RuntimeApi = new(@"/(containers|volumes|networks|exec)/|:237[56]\b|docker\.sock",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly Regex RuntimeApiTarget = new(@"/(?:containers|exec)/([^/?#&\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static bool IsRuntimeWord(string word) =>
        word is "docker" or "podman" or "nerdctl" or "ctr" or "crictl" || word.EndsWith("/docker", StringComparison.Ordinal);

    // Runtime subcommands whose operands are containers.
    private static readonly HashSet<string> ContainerSubcommands = new(StringComparer.Ordinal)
    {
        "stop", "start", "restart", "kill", "rm", "pause", "unpause", "exec", "logs", "inspect", "top", "wait",
        "port", "attach", "update", "rename", "commit", "export", "diff", "stats",
    };

    // Container-runtime options that take a number (a count, a duration, a port, a size) — not a container.
    private static readonly HashSet<string> NumericOptions = new(StringComparer.Ordinal)
    {
        "--tail", "-n", "--lines", "--since", "--until", "-t", "--time", "--timeout", "--stop-timeout", "-p",
        "--publish", "-m", "--memory", "--memory-swap", "--cpus", "--cpu-shares", "-c", "--shm-size", "--limit",
        "--last", "--pids-limit", "--restart-condition",
    };

    /// <summary>What the shell makes of a command: its literal words (quotes and escapes removed, with the words of
    /// quoted text that is run again), the words it expands as globs, and whether anything is built at run time.</summary>
    public sealed record ShellText(IReadOnlyList<string> Words, IReadOnlyList<string> Globs, bool Unanalyzable);

    private const int MaxShellDepth = 4;

    /// <summary>
    /// Reads a command as the shell does and decides whether it is analysable. Every command word — the first word
    /// of each command of a list or pipeline, after <c>sudo</c>, <c>env</c>, <c>nohup</c>, … — is judged by its file
    /// name: a shell or <c>su</c> is analysable only as <c>-c '…'</c> (whose text is read again), any other
    /// interpreter, a script or program run by a relative path or a script by its path, a build tool, <c>.</c> and
    /// the commands that build their arguments from data (<c>xargs</c>, <c>eval</c>, <c>parallel</c>, …) are not.
    /// </summary>
    public static ShellText AnalyseShell(string text)
    {
        var words = new List<string>();
        var globs = new List<string>();
        var commands = new List<int>();
        var unanalyzable = Tokenize(text, words, globs, commands, 0);
        foreach (var i in commands)
        {
            if (unanalyzable) break;
            var word = words[i];
            var name = Path.GetFileName(word);
            var next = i + 1 < words.Count ? words[i + 1] : null;
            if (ShellRunTime.Contains(name) || BuildTools.Contains(name) || name == "."
                || word.StartsWith("./", StringComparison.Ordinal) || word.StartsWith("../", StringComparison.Ordinal)
                || word.StartsWith("~/", StringComparison.Ordinal)
                || (word.StartsWith('/') && ScriptFile.IsMatch(word))
                || IsInterpreter(name))
                unanalyzable = true;
            else if ((Shells.Contains(name) || name == "su") && !IsCommandFlag(next))
                unanalyzable = true;
            else if (ComposeWrite(words, i, name))
                unanalyzable = true;
            else if (name == "find" && words.Skip(i + 1).Any(w => w is "-exec" or "-execdir" or "-ok" or "-okdir" or "-delete"))
            {
                // What it acts on comes from the file system: analysable only when every starting point is a plain
                // absolute directory that neither is nor contains a protected path.
                var roots = words.Skip(i + 1).TakeWhile(w => !w.StartsWith('-') && w is not "(" and not "!").ToList();
                unanalyzable = roots.Count == 0 || roots.Any(r => !r.StartsWith('/')
                    || AncestorHit(r, ControlPlaneResources.PathPatterns) is not null
                    || Match(NormalizePath(r), ControlPlaneResources.PathPatterns) is not null);
            }
        }
        // A shell or an interpreter as an argument (docker exec -i db sh, ssh host python3 x.py) runs what it is fed.
        for (var w = 0; w < words.Count && !unanalyzable; w++)
        {
            var name = Path.GetFileName(words[w]);
            var next = w + 1 < words.Count ? words[w + 1] : null;
            if (ShellRunTime.Contains(name)) unanalyzable = true;
            else if (Shells.Contains(name) && !IsCommandFlag(next)) unanalyzable = true;
            else if (IsInterpreter(name) && next is not null && (next.StartsWith('-') || next.Contains('.') || next.Contains('/')))
                unanalyzable = true;
        }
        try { unanalyzable |= ShellUnanalyzable.IsMatch(text); }
        catch (RegexMatchTimeoutException) { unanalyzable = true; }
        return new ShellText(words, globs, unanalyzable);
    }

    /// <summary>A compose or stack command that changes containers (<c>docker compose down</c>, <c>docker-compose up</c>,
    /// <c>docker stack deploy</c>) acts on what a compose file this text does not show defines.</summary>
    private static bool ComposeWrite(List<string> words, int command, string name)
    {
        var rest = words.Skip(command + 1).Where(w => !w.StartsWith('-')).ToList();
        // A cleanup (docker system prune, docker container prune, … volume/network) removes what the daemon finds,
        // named nowhere; images and the build cache hold no state of the control plane.
        if (IsRuntimeWord(name) && rest.Contains("prune") && !rest.Any(w => w is "image" or "builder" or "buildx"))
            return true;
        if (name is "docker" or "podman" && rest.Count > 0 && rest[0] is "compose" or "stack")
            rest = rest.Skip(1).ToList();
        else if (name is not ("docker-compose" or "podman-compose"))
            return false;
        // Options with a value (-f file, -p name, --project-directory dir) leave their value among the words; the
        // subcommand is the first word that is one.
        return rest.Any(w => ComposeWriteCommands.Contains(w));
    }

    private static readonly HashSet<string> ComposeWriteCommands = new(StringComparer.Ordinal)
    {
        "up", "down", "rm", "restart", "stop", "kill", "start", "create", "run", "deploy", "pause", "scale", "exec",
        "cp", "watch",
    };

    // -c, or single-dash flags that include it (-ec, -lc); never a long option (--norc).
    private static bool IsCommandFlag(string? word) =>
        word is { Length: >= 2 } && word[0] == '-' && word[1] != '-' && word.Contains('c') && word[1..].All(char.IsAsciiLetter);

    private static bool IsInterpreter(string name) =>
        Interpreters.Contains(name) || InterpreterVersion.IsMatch(name);

    private static readonly Regex InterpreterVersion = new(@"^(python|pypy)\d*(\.\d+)?$|^(perl|ruby|php|lua|node)\d+(\.\d+)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    private static readonly Regex ScriptFile = new(@"\.(sh|bash|zsh|ksh|py|pl|rb|js|mjs|cjs|ts|php|lua|ps1)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // Words before the command word that run it (sudo docker …, env X=1 docker …, nohup …, timeout 10 …).
    private static readonly HashSet<string> CommandPrefixes = new(StringComparer.Ordinal)
    {
        "sudo", "doas", "env", "exec", "nohup", "time", "nice", "ionice", "command", "builtin", "stdbuf", "timeout",
        "chroot", "setsid", "unbuffer", "watch", "flock",
    };

    // Programs that run a recipe from a file this text does not show.
    private static readonly HashSet<string> BuildTools = new(StringComparer.Ordinal)
    {
        "make", "gmake", "just", "task", "npm", "npx", "yarn", "pnpm", "ansible", "ansible-playbook", "rake", "invoke",
        "cron", "crontab", "at", "batch", "systemd-run", "run-parts",
    };

    // Commands that build what they act on from data at run time.
    private static readonly HashSet<string> ShellRunTime = new(StringComparer.Ordinal)
    {
        "eval", "xargs", "base64", "xxd", "source", "parallel", "envsubst", "uudecode", "rev",
    };

    private static readonly HashSet<string> Shells = new(StringComparer.Ordinal)
    {
        "sh", "bash", "dash", "ash", "zsh", "ksh", "mksh", "fish", "busybox",
    };

    // Interpreters whose inline code (-c / -e) is not shell; sh -c '…' is read again instead.
    private static readonly HashSet<string> Interpreters = new(StringComparer.Ordinal)
    {
        "python", "python2", "python3", "perl", "ruby", "node", "nodejs", "php", "lua", "tclsh", "pwsh", "powershell",
        "deno", "bun", "Rscript", "julia", "osascript", "expect", "gawk", "mawk", "nawk",
    };

    private static readonly Regex ShellUnanalyzable = new(
        @"\bopenssl\s+(enc|base64)\b|\bprintf\b[^\n]*%b|\\x[0-9a-f]{2}|\\[0-7]{3}|\bsystem\s*\("
        // sed's e flag or command runs the pattern space as a command
        + @"|\bsed\b[^|;&\n]*(/[gIimMp0-9]*e[gIimMp0-9]*['""\s]|['""]\s*e\s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Splits <paramref name="text"/> into shell words. Returns true when something in it is expanded at run
    /// time. Quoted text with shell syntax in it is read again as a command (<c>sh -c '…'</c>, <c>ssh host "…"</c>).</summary>
    private static bool Tokenize(string text, List<string> words, List<string> globs, List<int> commands, int depth)
    {
        if (depth > MaxShellDepth) return true;
        var unanalyzable = false;
        var word = new StringBuilder();
        var quotedPart = new StringBuilder();
        bool inWord = false, glob = false, quoted = false;
        // Whether the next word is a command word, and whether a prefix (sudo, env …) is still taking its options.
        bool atCommand = true, inPrefix = false;
        var i = 0;

        void End()
        {
            if (!inWord) return;
            var w = word.ToString();
            if (atCommand)
            {
                var prefixArgument = inPrefix && (w.StartsWith('-') || w.All(char.IsAsciiDigit)
                                                  || (w.Contains('=') && !w.StartsWith('=')));
                var assignment = !inPrefix && w.Contains('=') && !w.StartsWith('=') && !w.StartsWith('-')
                                 && w.IndexOf('=') < (w.IndexOf('/') is var slash and >= 0 ? slash : int.MaxValue);
                if (prefixArgument || assignment) { }
                else if (CommandPrefixes.Contains(Path.GetFileName(w))) inPrefix = true;
                else
                {
                    commands.Add(words.Count);
                    atCommand = inPrefix = false;
                }
            }
            words.Add(w);
            if (glob) globs.Add(w);
            // Quoted text that looks like a command line is read again: it may be run by sh -c, ssh, docker exec …
            if (quoted && quotedPart.Length > 0 && LooksLikeCommand(quotedPart.ToString()))
                unanalyzable |= Tokenize(quotedPart.ToString(), words, globs, commands, depth + 1);
            word.Clear();
            quotedPart.Clear();
            inWord = glob = quoted = false;
        }

        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case ';' or '|' or '&' or '(' or ')' or '\n':
                    End();
                    atCommand = true;
                    inPrefix = false;
                    i++;
                    continue;
                case '<' or '>':
                {
                    // A redirection: a file descriptor before it (2>) and one after it (>&1) are not words.
                    if (inWord && !quoted && word.ToString().All(char.IsAsciiDigit))
                    {
                        word.Clear();
                        inWord = glob = false;
                    }
                    End();
                    i++;
                    while (i < text.Length && text[i] is '>' or '<') i++;
                    if (i < text.Length && text[i] == '&')
                    {
                        i++;
                        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '-')) i++;
                    }
                    continue;
                }
                case ' ' or '\t' or '\r':
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
        + @"|\binto\s+(out|dump)file\b|\bunhex\s*\(|\bfrom_base64\s*\("
        // PostgreSQL unicode-escaped identifiers and strings, untrusted procedural languages, routines defined inline
        + @"|\bu&[""']|\\[0-9a-f]{4,6}|\blanguage\s+'?pl\w*u\b|\bcreate\s+(or\s+replace\s+)?(function|procedure)\b"
        // a document database's query language: strings joined or decoded at run time, server-side JavaScript
        + @"|\bfromCharCode\b|\$where\b|\$function\b|\bdb\.eval\s*\("
        // string escapes a shell or a driver decodes into a name: b, \u{62}, \x62, octal \142
        + @"|\\u\{?[0-9a-f]|\\x[0-9a-f]{2}|\\[0-7]{3}"
        // client commands the text is fed to: psql \i \ir \gexec \c \! …, mysql source / system / \. , sqlite .read
        // .shell …, mongosh load() — they run a file, a shell or another database the text does not name
        + @"|(?:^|[\s;])\\[a-z!.]|^\s*(source|system)\s|^\s*\.(read|shell|system|import|open|load|cd)\b|\bload\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline, TimeSpan.FromMilliseconds(200));

    // A document database's shell query (db.…): strings joined at run time name what it acts on only then.
    private static readonly Regex MongoShell = new(@"\bdb\.\w", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex MongoJoined = new(@"[""'`]\s*\+|\+\s*[""'`]|\bconcat\s*\(|\.join\s*\(|\$\{",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Whether SQL builds what it acts on at run time. A pattern that takes too long counts as unanalysable
    /// (fail closed).</summary>
    public static bool IsUnanalyzableSql(string text)
    {
        try { return UnanalyzableSql.IsMatch(text) || (MongoShell.IsMatch(text) && MongoJoined.IsMatch(text)); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    /// <summary>Whether a shell command builds what it acts on at run time.</summary>
    public static bool IsUnanalyzableShell(string text) => AnalyseShell(text).Unanalyzable;
}
