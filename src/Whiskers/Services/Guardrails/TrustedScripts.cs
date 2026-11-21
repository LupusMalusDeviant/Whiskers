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

    /// <summary>The length of <paramref name="additions"/> as the approval card stores it: JSON-escaped (sixth review).
    /// That length, not the raw one, is held to <see cref="MaxAdditionsChars"/>.</summary>
    public static int CardLength(string additions) =>
        System.Text.Json.JsonSerializer.Serialize(additions, Whiskers.Utils.SinkRedaction.JsonOptions).Length;

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

    /// <summary>Point 1: a server, a valid path and a lowercase SHA-256.</summary>
    public static bool IsValid(TrustedScript? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.ServerId) || !IsValidPath(entry.Path)) return false;
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

    /// <summary>The entry the policy lists for this server and path, or <c>null</c>.</summary>
    public static TrustedScript? Find(GuardrailPolicy policy, string? serverId, string path) =>
        string.IsNullOrEmpty(serverId) ? null
            : policy.TrustedScripts?.FirstOrDefault(s => s is not null
                && string.Equals(s.ServerId, serverId, StringComparison.Ordinal)
                && string.Equals(s.Path, path, StringComparison.Ordinal));

    /// <summary>How the verified entry travels to the rules: server, path and hash, separated by a unit separator.</summary>
    public static string Encode(TrustedScript script) =>
        string.Join('\u001f', script.ServerId, script.Path, script.Sha256);

    /// <summary>The trusted call of a request the rules may honour: the resolver verified <see cref="ResolvedKey"/>
    /// (server, path and hash read on the server), the policy the rules decide with lists exactly that entry, and the
    /// <c>command</c> is exactly a call of that path. Otherwise <c>null</c>.</summary>
    public static string? TrustedCallOf(IReadOnlyDictionary<string, JsonElement> arguments, GuardrailPolicy policy)
    {
        if (!arguments.TryGetValue(ResolvedKey, out var verified) || verified.ValueKind != JsonValueKind.String) return null;
        var parts = (verified.GetString() ?? "").Split('\u001f');
        if (parts.Length != 3) return null;
        // Amendment 1 (A3): the verified server must be the server this request resolved to.
        if (!arguments.TryGetValue("__resolvedTargetId", out var server) || server.ValueKind != JsonValueKind.String
            || !string.Equals(server.GetString(), parts[0], StringComparison.Ordinal))
            return null;
        if (Find(policy, parts[0], parts[1]) is not { } listed
            || !string.Equals(listed.Sha256, parts[2], StringComparison.Ordinal))
            return null;
        if (!arguments.TryGetValue("command", out var command) || command.ValueKind != JsonValueKind.String) return null;
        return string.Equals(CallPath(command.GetString()), parts[1], StringComparison.Ordinal) ? parts[1] : null;
    }

    /// <summary>D-1: the argument of <c>guardrails.save</c> that names the entries a save adds or whose hash it changes.</summary>
    public const string AdditionsArgument = "trustedScriptsAdded";

    /// <summary>D-1: the capability whose <see cref="AdditionsArgument"/> is read.</summary>
    public const string SaveCapability = "guardrails.save";

    /// <summary>D-1: the entries <paramref name="next"/> adds to <paramref name="stored"/> or whose hash it changes, in
    /// any preset, one line each (<c>preset | server | path | sha256</c>), sorted; empty when there are none. Removals
    /// are not listed: they narrow trust.</summary>
    public static string Additions(GuardrailConfig stored, GuardrailConfig next)
    {
        static IEnumerable<string> Lines(GuardrailConfig config) =>
            (config.Presets ?? []).Where(p => p?.Policy is not null)
                .SelectMany(p => (p.Policy.TrustedScripts ?? []).Where(s => s is not null)
                    .Select(s => $"{p.Name} | {s.ServerId} | {s.Path} | {s.Sha256}"));
        var before = Lines(stored).ToHashSet(StringComparer.Ordinal);
        return string.Join('\n', Lines(next).Where(l => !before.Contains(l)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
    }

    /// <summary>Every listed path, on any server: protected from changes (point 5).</summary>
    public static IReadOnlyList<string> PathsOf(GuardrailPolicy policy) =>
        policy.TrustedScripts?.Where(s => s is not null && !string.IsNullOrEmpty(s.Path)).Select(s => s.Path)
            .Distinct(StringComparer.Ordinal).ToList() ?? [];
}
