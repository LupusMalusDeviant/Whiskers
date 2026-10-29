using System.Text.Json;
using System.Text.RegularExpressions;

namespace Whiskers.Services.Mutations;

/// <summary>
/// What a configuration or policy write changed, as part of the gateway's one event (R1-22): the section, the version
/// before and after, and each changed setting with its old and new value — redacted. Stores call
/// <see cref="Record"/> after a successful write; outside a gateway scope it does nothing.
/// </summary>
/// <remarks>
/// <para><b>Redaction.</b> A setting whose name marks a secret (password, token, secret, key, key hint, credential …) never shows
/// a value: only whether it is set, with a fingerprint keyed by a per-process random key, so a replaced secret is a
/// visible change within one event while nothing stored lets anyone test a guess later. A URL shows its origin only (scheme, host, port) — webhook URLs
/// carry their secret in the path. Every other value is cut to <see cref="MaxValueChars"/> and passed through
/// <see cref="Whiskers.Utils.SecretRedactor"/>.</para>
/// <para><b>Versions.</b> A version is the SHA-256 (first 12 hex digits) of the section's canonical, redacted content:
/// equal content is the same version, so a reader can chain the events of one section. Secret values are not part of
/// it — a hash over a secret would let an audit reader test guesses offline — so a change of a secret alone keeps the
/// version and shows up as a changed setting (<c>[set #a1b2c3] → [set #d4e5f6]</c>).</para>
/// </remarks>
public static class ConfigChange
{
    /// <summary>Changed settings recorded per write; the rest is counted. Kept small so the event's capped details
    /// (4000 characters, shrunk field by field) never cut the version entry.</summary>
    public const int MaxChanges = 8;

    /// <summary>The longest value shown before or after a change.</summary>
    public const int MaxValueChars = 80;

    /// <summary>The action of the version entry of an event (one per written section).</summary>
    public const string VersionAction = "config.version";

    /// <summary>The target type of each changed setting in the event.</summary>
    public const string SettingTarget = "setting";

    private static readonly Regex SecretName = new(
        @"(?i)(pass(word|wd|phrase)?|pwd|secret|token|api_?key|access_?key|private_?key|credential|cookie|^key$|keyhash|keyhint|hash$|salt|vault)",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex UrlName = new(
        @"(?i)(url|uri|endpoint|homeserver|server)$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly string[] KeyProperties = ["Id", "id", "Email", "email", "Name", "name", "Key"];

    /// <summary>Records the change of <paramref name="section"/> from <paramref name="before"/> to
    /// <paramref name="after"/> in the current gateway scope. Returns false (and records nothing) outside a scope.</summary>
    public static bool Record(string section, object? before, object? after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        if (MutationScope.Current is not { } scope) return false;
        var (from, fromVersion) = Describe(before);
        var (to, toVersion) = Describe(after);
        var changes = Diff(from, to);

        MutationScope.Record(new MutationDomainResult(VersionAction, "config", section, section,
            $"{fromVersion} → {toVersion}; {changes.Count} changed", null, true));
        foreach (var (path, old, @new) in changes.Take(MaxChanges))
            MutationScope.Record(new MutationDomainResult(scope.CapabilityId, SettingTarget, $"{section}:{path}", path,
                $"{old} → {@new}", null, true));
        if (changes.Count > MaxChanges)
            MutationScope.Record(new MutationDomainResult(scope.CapabilityId, SettingTarget, $"{section}:…", "",
                $"{changes.Count - MaxChanges} further setting(s) changed", null, true));
        return true;
    }

    /// <summary>The redacted settings of <paramref name="value"/> by path, and the version they make up.</summary>
    public static (IReadOnlyDictionary<string, string> Settings, string Version) Describe(object? value)
    {
        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (value is not null)
        {
            var element = value as JsonElement? ?? JsonSerializer.SerializeToElement(value, value.GetType());
            Flatten(element, "", secret: false, settings);
        }
        // The version leaves the per-process secret fingerprints out: equal content has one version across restarts.
        var canonical = JsonSerializer.Serialize(settings.ToDictionary(s => s.Key,
            s => s.Value.StartsWith("[set #", StringComparison.Ordinal) ? "[set]" : s.Value, StringComparer.Ordinal));
        return (settings, PolicyCanonicalJson.Hash(canonical)[..12]);
    }

    /// <summary>The changed paths in order, with their redacted old and new value (<c>(none)</c> when absent).</summary>
    public static List<(string Path, string Before, string After)> Diff(IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        var changes = new List<(string, string, string)>();
        foreach (var path in before.Keys.Union(after.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            before.TryGetValue(path, out var old);
            after.TryGetValue(path, out var @new);
            if (old == @new) continue;
            changes.Add((path, old ?? "(none)", @new ?? "(none)"));
        }
        return changes;
    }

    private static void Flatten(JsonElement element, string path, bool secret, IDictionary<string, string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var child = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                    Flatten(property.Value, child, secret || SecretName.IsMatch(property.Name), into);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    // Lists of records are keyed by their identity, lists of values by the value itself: an entry
                    // added in the middle is one change, not a shift of every entry after it.
                    var key = item.ValueKind switch
                    {
                        JsonValueKind.Object => KeyOf(item) ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                            when !secret => Value(item, path, secret: false),
                        _ => index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    };
                    var child = $"{path}[{key}]";
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Flatten(item, child, secret, into);
                    else into[child] = secret ? Secret(item) : "present";
                    index++;
                }
                break;
            default:
                into[path] = secret ? Secret(element) : Value(element, path, secret: false);
                break;
        }
    }

    private static string? KeyOf(JsonElement item)
    {
        foreach (var name in KeyProperties)
            if (item.TryGetProperty(name, out var key) && key.ValueKind is JsonValueKind.String or JsonValueKind.Number
                && !SecretName.IsMatch(name) && key.ToString() is { Length: > 0 } text)
                return Shorten(Whiskers.Utils.SecretRedactor.Redact(text));
        return null;
    }

    /// <summary>A secret shows whether it is set, never what it is (<see cref="SecretMarker"/>).</summary>
    private static string Secret(JsonElement element) =>
        element.ValueKind is JsonValueKind.Null || (element.ValueKind == JsonValueKind.String && element.GetString() is "" or null)
            ? "[not set]"
            : SecretMarker(element.ToString());

    /// <summary>"[set]" plus a fingerprint that only tells two values apart within this process (a per-process random
    /// key): a replaced secret is a visible change, while nothing stored lets anyone test a guess later.</summary>
    private static string SecretMarker(string value) =>
        $"[set #{Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(ProcessKey, System.Text.Encoding.UTF8.GetBytes(value)))[..6]}]";

    private static readonly byte[] ProcessKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private static string Value(JsonElement element, string path, bool secret)
    {
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Null => "null",
            _ => element.GetRawText(),
        };
        if (element.ValueKind == JsonValueKind.String && UrlName.IsMatch(LastSegment(path)) && text.Length > 0)
            text = OriginOf(text);
        return Shorten(Whiskers.Utils.SecretRedactor.Redact(PolicyDecisions.WithoutUrlCredentials(text)!));
    }

    private static string LastSegment(string path)
    {
        var dot = path.LastIndexOf('.');
        var segment = dot < 0 ? path : path[(dot + 1)..];
        var bracket = segment.IndexOf('[');
        return bracket < 0 ? segment : segment[..bracket];
    }

    /// <summary>Scheme, host and port of a URL — the path and query of a webhook URL hold its secret.</summary>
    private static string OriginOf(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? (uri.PathAndQuery is "/" or "" ? $"{uri.Scheme}://{uri.Authority}" : $"{uri.Scheme}://{uri.Authority}/…")
            : text.Contains('/') ? "[url]" : text;

    private static string Shorten(string text) =>
        text.Length > MaxValueChars ? text[..MaxValueChars] + "…" : text;
}
