using System.Text.Json;
using System.Text.RegularExpressions;
using Whiskers.Configuration;
using Whiskers.Services.Server;

namespace Whiskers.Services.Guardrails;

/// <summary>
/// Contract 0022: scripts an Admin vouched for, per server, pinned to their content's SHA-256. A <em>trusted call</em>
/// is a command that is exactly a listed path followed by literal arguments; only its command word is spared the
/// "runs code this text does not show" verdict of INV-24 — every other check of the command stays. The gateway's
/// resolver reads the script's hash on the server before the decision and again right before the run.
/// </summary>
public static class TrustedScripts
{
    /// <summary>The argument the policy adapter carries the path the resolver verified in (hash and server).</summary>
    public const string ResolvedKey = "__resolvedTrustedScript";

    /// <summary>The longest <see cref="AdditionsArgument"/> one save may carry, so that the approval card (capped at
    /// 4000 characters for all arguments) shows every line (contract 0022, fifth review).</summary>
    public const int MaxAdditionsChars = 3000;

    /// <summary>The length of <paramref name="additions"/> as the approval card stores it: redacted value by value and
    /// JSON-escaped, the way the approval gate writes the card (sixth review, amendment 4 D4 — redaction can lengthen
    /// text). That length, not the raw one, is held to <see cref="MaxAdditionsChars"/>.</summary>
    public static int CardLength(string additions)
    {
        static int Stored(string value) =>
            Whiskers.Utils.SinkRedaction.Json(JsonSerializer.Serialize(new Dictionary<string, string>
                { [AdditionsArgument] = value }))?.Length ?? 0;
        return Stored(additions) - Stored("");
    }

    // An absolute path of plain characters; literal arguments of plain characters, separated by blanks. No quotes,
    // no $, no backticks, no braces, no globs, no ; | & < > # ~ or line breaks.
    private static readonly Regex Call = new(@"^[ \t]*(?<path>/[A-Za-z0-9._+/-]+)(?:[ \t]+[A-Za-z0-9._+/,:=@%-]+)*[ \t]*\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // Amendment 3 (C3): anchored at the true end (\z); '$' would also accept a final line break.
    private static readonly Regex PathShape = new(@"^/[A-Za-z0-9._+/-]+\z", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex Hex64 = new(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(200));

    /// <summary>The path of a command that has the shape of a trusted call (point 3), else <c>null</c>.</summary>
    public static string? CallPath(string? command)
    {
        if (string.IsNullOrEmpty(command)) return null;
        try
        {
            var match = Call.Match(command);
            return match.Success && IsValidPath(match.Groups["path"].Value) ? match.Groups["path"].Value : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>An absolute, normalised path of plain characters, not under the control plane, whose file name is not
    /// a shell, an interpreter, a container runtime, a build tool or a command that runs other commands.</summary>
    public static bool IsValidPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.EndsWith('/') || path.Contains("//", StringComparison.Ordinal)) return false;
        try { if (!PathShape.IsMatch(path)) return false; }
        catch (RegexMatchTimeoutException) { return false; }
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or "..")) return false;
        return !ProtectedResourceMatcher.IsUntrustableCommandName(segments[^1])
               && !ProtectedResourceMatcher.IsControlPlanePath(path);
    }

    /// <summary>Point 1: a server, a valid path, valid arguments (amendment 4) and a lowercase SHA-256.</summary>
    public static bool IsValid(TrustedScript? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.ServerId) || !IsValidPath(entry.Path)
            || !IsValidArguments(entry.Arguments))
            return false;
        try { return entry.Sha256 is { } sha && Hex64.IsMatch(sha); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Amendment 1 (A1): the fixed command that reads a script's hash, and prints it only for a real script — a
    /// regular file, no symlink anywhere in its path (its canonical path is the path itself), starting with <c>#!</c>
    /// (so no ELF program). Amendment 3 (C2): with one link (no hard link to it elsewhere), owned by root, and no write
    /// bit for group or others. Anything else prints nothing and fails, which callers treat as no hash (fail closed).
    /// The path holds no quote (<see cref="IsValidPath"/>).</summary>
    public static string HashCommand(string path)
    {
        if (!IsValidPath(path)) throw new ArgumentException("Not a path that can be trusted.", nameof(path));
        return $"p='{path}'; [ -f \"$p\" ] && [ ! -L \"$p\" ] && [ \"$(realpath -e -- \"$p\")\" = \"$p\" ] " +
               "&& [ \"$(stat -c %h:%u -- \"$p\")\" = 1:0 ] && [ $(( 0$(stat -c %a -- \"$p\") & 022 )) -eq 0 ] " +
               "&& [ \"$(head -c 2 -- \"$p\")\" = '#!' ] && sha256sum -- \"$p\"";
    }

    /// <summary>The hash a successful <see cref="HashCommand"/> printed, lowercase; <c>null</c> for anything else.</summary>
    public static string? ParseHash(CommandResult? result)
    {
        if (result is null || !result.Success || result.OutputTruncated) return null;
        var first = result.Output.Trim().Split([' ', '\t', '\n'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null) return null;
        var hash = first.TrimStart('\\').ToLowerInvariant();
        try { return Hex64.IsMatch(hash) ? hash : null; }
        catch (RegexMatchTimeoutException) { return null; }
    }

    // Amendment 4 (D1): words of the characters of point 3, separated by single blanks, no blank at either end.
    private static readonly Regex ArgumentsShape = new(@"^[A-Za-z0-9._+/,:=@%-]+(?: [A-Za-z0-9._+/,:=@%-]+)*\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Amendment 4 (D1): empty, or plain words with single blanks of which no word and no option value (after
    /// <c>=</c>, or after the first two characters of a short option) names a program that runs other code.</summary>
    public static bool IsValidArguments(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments)) return true;
        try { if (!ArgumentsShape.IsMatch(arguments)) return false; }
        catch (RegexMatchTimeoutException) { return false; }
        foreach (var word in arguments.Split(' '))
        {
            var candidates = new List<string> { word };
            if (word.IndexOf('=') is var eq and >= 0) candidates.Add(word[(eq + 1)..]);
            if (word.Length > 2 && word[0] == '-' && word[1] != '-') candidates.Add(word[2..]);
            foreach (var candidate in candidates)
            {
                var name = candidate.Contains('/') ? candidate[(candidate.LastIndexOf('/') + 1)..] : candidate;
                if (name.Length > 0 && ProtectedResourceMatcher.IsUntrustableCommandName(name)) return false;
            }
        }
        return true;
    }

    /// <summary>The arguments of a trusted call after trimming and collapsing blanks (D2); empty for none, <c>null</c>
    /// when the command has no trusted-call shape.</summary>
    public static string? CallArguments(string? command)
    {
        if (CallPath(command) is not { } path) return null;
        var words = command!.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 || words[0] != path ? null : string.Join(' ', words.Skip(1));
    }

    /// <summary>The entry the policy lists for this server, path and argument list, or <c>null</c>. Missing and empty
    /// arguments are the same call (D1).</summary>
    public static TrustedScript? Find(GuardrailPolicy policy, string? serverId, string path, string? arguments) =>
        string.IsNullOrEmpty(serverId) ? null
            : policy.TrustedScripts?.FirstOrDefault(s => s is not null
                && string.Equals(s.ServerId, serverId, StringComparison.Ordinal)
                && string.Equals(s.Path, path, StringComparison.Ordinal)
                && string.Equals(s.Arguments ?? "", arguments ?? "", StringComparison.Ordinal));

    /// <summary>How the verified entry travels to the rules: server, path, arguments and hash, separated by a unit
    /// separator (amendment 4).</summary>
    public static string Encode(TrustedScript script) =>
        string.Join('\u001f', script.ServerId, script.Path, script.Arguments ?? "", script.Sha256);

    /// <summary>The trusted call of a request the rules may honour: the resolver verified <see cref="ResolvedKey"/>
    /// (server, path, arguments and hash read on the server), the policy the rules decide with lists exactly that
    /// entry, and the <c>command</c> is exactly that call (D2). Otherwise <c>null</c>.</summary>
    public static string? TrustedCallOf(IReadOnlyDictionary<string, JsonElement> arguments, GuardrailPolicy policy)
    {
        if (!arguments.TryGetValue(ResolvedKey, out var verified) || verified.ValueKind != JsonValueKind.String) return null;
        var parts = (verified.GetString() ?? "").Split('\u001f');
        if (parts.Length != 4) return null;
        // Amendment 1 (A3): the verified server must be the server this request resolved to.
        if (!arguments.TryGetValue("__resolvedTargetId", out var server) || server.ValueKind != JsonValueKind.String
            || !string.Equals(server.GetString(), parts[0], StringComparison.Ordinal))
            return null;
        if (Find(policy, parts[0], parts[1], parts[2]) is not { } listed
            || !string.Equals(listed.Sha256, parts[3], StringComparison.Ordinal))
            return null;
        if (!arguments.TryGetValue("command", out var command) || command.ValueKind != JsonValueKind.String) return null;
        var text = command.GetString();
        return string.Equals(CallPath(text), parts[1], StringComparison.Ordinal)
               && string.Equals(CallArguments(text), parts[2], StringComparison.Ordinal)
            ? parts[1]
            : null;
    }

    /// <summary>D-1: the argument of <c>guardrails.save</c> that names the entries a save adds or whose hash it changes.</summary>
    public const string AdditionsArgument = "trustedScriptsAdded";

    /// <summary>D-1: the capability whose <see cref="AdditionsArgument"/> is read.</summary>
    public const string SaveCapability = "guardrails.save";

    /// <summary>D-1: the entries <paramref name="next"/> adds to <paramref name="stored"/> or whose hash it changes, in
    /// any preset, one line each (<c>preset | server | path | arguments | sha256</c>, amendment 4 D3), sorted; empty when
    /// there are none. A changed argument list is an addition. Removals are not listed: they narrow trust.</summary>
    public static string Additions(GuardrailConfig stored, GuardrailConfig next)
    {
        static IEnumerable<string> Lines(GuardrailConfig config) =>
            (config.Presets ?? []).Where(p => p?.Policy is not null)
                .SelectMany(p => (p.Policy.TrustedScripts ?? []).Where(s => s is not null)
                    .Select(s => $"{p.Name} | {s.ServerId} | {s.Path} | {s.Arguments ?? ""} | {s.Sha256}"));
        var before = Lines(stored).ToHashSet(StringComparer.Ordinal);
        return string.Join('\n', Lines(next).Where(l => !before.Contains(l)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
    }

    /// <summary>Every listed path, on any server: protected from changes (point 5).</summary>
    public static IReadOnlyList<string> PathsOf(GuardrailPolicy policy) =>
        policy.TrustedScripts?.Where(s => s is not null && !string.IsNullOrEmpty(s.Path)).Select(s => s.Path)
            .Distinct(StringComparer.Ordinal).ToList() ?? [];
}
