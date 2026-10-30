using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Whiskers.Services.Mutations;

/// <summary>
/// What a configuration or policy write changed, as part of the gateway's one event (R1-22): the section, the version
/// before and after, and each changed setting with its old and new value — redacted. Stores call
/// <see cref="Record"/> after a successful write; outside a gateway scope it does nothing.
/// </summary>
/// <remarks>
/// <para><b>Comparison and version use the complete values.</b> Each setting is compared by its full value (a secret by
/// its keyed fingerprint), so a change past the shown length or inside a URL's path is still a change. The version is
/// the SHA-256 (first 12 hex digits) of the section's canonical content with every secret replaced by whether it is
/// set: equal content is the same version across restarts, a change of a secret alone keeps it (a hash over a secret
/// would let an audit reader test guesses offline) and shows up as a changed setting.</para>
/// <para><b>What is shown.</b> A setting whose name marks a secret (password, token, secret, key, key hash/hint,
/// credential …) shows only <c>[not set]</c> or <c>[set #fingerprint]</c>, the fingerprint keyed by a per-process
/// random key: a replaced secret is a visible change within one event while nothing stored lets anyone test a guess
/// later. A secret-named object or list of values is secret as a whole; a list of records (API keys, users) is not —
/// each record's own fields are classified. A URL-named setting shows its origin only (webhook URLs carry their
/// secret in the path). Any other text is shown only when it is short, holds no URL and passes
/// <see cref="Whiskers.Utils.SecretRedactor"/> unchanged; free text (prompts, patterns, long values) shows as
/// <c>[text, N chars #fingerprint]</c>. When two values differ but show the same, both carry their fingerprint.</para>
/// <para><b>Budget.</b> One operation records at most <see cref="MaxChanges"/> changed settings over all its sections;
/// every section keeps its version entry and the count of changes left out.</para>
/// </remarks>
public static class ConfigChange
{
    /// <summary>Changed settings recorded per operation, over all its sections; the rest is counted.</summary>
    public const int MaxChanges = 8;

    /// <summary>The longest text shown as it is.</summary>
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

    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>One setting: what is shown, what it is compared by, and what goes into the version.</summary>
    public sealed record Setting(string Shown, string Compared, string Versioned);

    /// <summary>The settings of a section by path, and the version they make up.</summary>
    public sealed record Snapshot(IReadOnlyDictionary<string, Setting> Settings, string Version)
    {
        /// <summary>What each setting shows, by path.</summary>
        public IReadOnlyDictionary<string, string> Shown =>
            Settings.ToDictionary(s => s.Key, s => s.Value.Shown, StringComparer.Ordinal);
    }

    /// <summary>Records the change of <paramref name="section"/> from <paramref name="before"/> to
    /// <paramref name="after"/> in the current gateway scope. Returns false (and records nothing) outside a scope.</summary>
    public static bool Record(string section, object? before, object? after)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        if (MutationScope.Current is not { } scope) return false;
        var from = Describe(before);
        var to = Describe(after);
        var changes = Diff(from, to);

        MutationScope.Record(new MutationDomainResult(VersionAction, "config", section, section,
            $"{from.Version} → {to.Version}; {changes.Count} changed", null, true));
        var shown = scope.TakeSettingBudget(changes.Count, MaxChanges);
        foreach (var (path, old, @new) in changes.Take(shown))
            MutationScope.Record(new MutationDomainResult(scope.CapabilityId, SettingTarget, $"{section}:{path}", path,
                $"{old} → {@new}", null, true));
        if (changes.Count > shown)
            MutationScope.Record(new MutationDomainResult(scope.CapabilityId, SettingTarget, $"{section}:…", "",
                $"{changes.Count - shown} further setting(s) changed", null, true));
        return true;
    }

    /// <summary>A copy of <paramref name="value"/> as it is now, for a store that changes the object in place
    /// (an entity) and records the change afterwards.</summary>
    public static object? Capture(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType());

    /// <summary>The settings of <paramref name="value"/> and the version they make up.</summary>
    public static Snapshot Describe(object? value)
    {
        var settings = new SortedDictionary<string, Setting>(StringComparer.Ordinal);
        if (value is not null)
        {
            var element = value as JsonElement? ?? JsonSerializer.SerializeToElement(value, value.GetType());
            Flatten(element, "", secret: false, settings);
        }
        var canonical = JsonSerializer.Serialize(settings.ToDictionary(s => s.Key, s => s.Value.Versioned, StringComparer.Ordinal));
        return new Snapshot(settings, PolicyCanonicalJson.Hash(canonical)[..12]);
    }

    /// <summary>The changed paths in order, with what they show before and after (<c>(none)</c> when absent).</summary>
    public static List<(string Path, string Before, string After)> Diff(Snapshot before, Snapshot after)
    {
        var changes = new List<(string, string, string)>();
        foreach (var path in before.Settings.Keys.Union(after.Settings.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            before.Settings.TryGetValue(path, out var old);
            after.Settings.TryGetValue(path, out var @new);
            if (old?.Compared == @new?.Compared) continue;
            var oldShown = old?.Shown ?? "(none)";
            var newShown = @new?.Shown ?? "(none)";
            // Different values that show the same (a URL rotated on its origin): tell them apart by fingerprint.
            if (old is not null && @new is not null && oldShown == newShown)
            {
                oldShown += $" #{Fingerprint(old.Compared)}";
                newShown += $" #{Fingerprint(@new.Compared)}";
            }
            changes.Add((path, oldShown, newShown));
        }
        return changes;
    }

    private static void Flatten(JsonElement element, string path, bool secret, IDictionary<string, Setting> into)
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
                        JsonValueKind.Object => KeyOf(item) ?? Index(index),
                        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                            when !secret => Leaf(item, path, secret: false).Shown,
                        _ => Index(index),
                    };
                    var child = $"{path}[{key}]";
                    // A record in a secret-named list (ApiKeys) is classified by its own fields.
                    if (item.ValueKind == JsonValueKind.Object) Flatten(item, child, secret: false, into);
                    else if (item.ValueKind == JsonValueKind.Array) Flatten(item, child, secret, into);
                    else into[child] = secret ? Leaf(item, path, secret: true) : new("present", Text(item), "present");
                    index++;
                }
                break;
            default:
                into[path] = Leaf(element, path, secret);
                break;
        }
    }

    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static string? KeyOf(JsonElement item)
    {
        foreach (var name in KeyProperties)
            if (item.TryGetProperty(name, out var key) && key.ValueKind is JsonValueKind.String or JsonValueKind.Number
                && !SecretName.IsMatch(name) && key.ToString() is { Length: > 0 } text)
                return Shown(text, urlField: false);
        return null;
    }

    private static Setting Leaf(JsonElement element, string path, bool secret)
    {
        var text = Text(element);
        if (secret)
        {
            var unset = element.ValueKind is JsonValueKind.Null || (element.ValueKind == JsonValueKind.String && text.Length == 0);
            return unset
                ? new("[not set]", "[not set]", "[not set]")
                : new($"[set #{Fingerprint(text)}]", Fingerprint(text), "[set]");
        }
        if (element.ValueKind != JsonValueKind.String) return new(text, text, text);
        return new(Shown(text, UrlName.IsMatch(LastSegment(path))), text, text);
    }

    private static string Text(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Null => "null",
        _ => element.GetRawText(),
    };

    /// <summary>A text as it may appear in the audit: a URL field as its origin, short plain text as it is, anything
    /// else (free text, a URL inside text, what the redactor would change) only as its length and fingerprint.</summary>
    private static string Shown(string text, bool urlField)
    {
        if (text.Length == 0) return "";
        if (urlField && Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.PathAndQuery is "/" or "" ? $"{uri.Scheme}://{uri.Authority}" : $"{uri.Scheme}://{uri.Authority}/…";
        var plain = text.Length <= MaxValueChars
                    && !text.Contains("://", StringComparison.Ordinal)
                    && !text.Contains('\n')
                    && Whiskers.Utils.SecretRedactor.Redact(text) == text;
        return plain ? text : $"[text, {text.Length} chars #{Fingerprint(text)}]";
    }

    private static string LastSegment(string path)
    {
        var dot = path.LastIndexOf('.');
        var segment = dot < 0 ? path : path[(dot + 1)..];
        var bracket = segment.IndexOf('[');
        return bracket < 0 ? segment : segment[..bracket];
    }

    /// <summary>Six hex digits of an HMAC under the per-process key: tells two values apart within this process only.</summary>
    private static string Fingerprint(string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(ProcessKey, Encoding.UTF8.GetBytes(value)))[..6];
}
