using System.Text.Json;

namespace Whiskers.Utils;

/// <summary>
/// What every persistent sink stores instead of the raw text (R1-24, INV-51): the audit log, the MCP/agent call
/// history, approvals, policy decisions, scheduled-task results and the chat transcripts. Free-text payloads (a shell
/// command, SQL, a compose file, an .env block, a prompt) keep only their length — no pattern can recognise every
/// credential syntax inside them; every other string is redacted on its own (<see cref="SecretRedactor"/>, with URL
/// user info removed first). Never a prefix of a secret: a withheld value is described by its length alone.
/// </summary>
public static class SinkRedaction
{
    /// <summary>Name parts that make an argument a free-text payload, compared case- and separator-insensitively
    /// (<c>composeContent</c>, <c>compose_content</c>, <c>docker-compose</c>, <c>existingContent</c>,
    /// <c>envVars</c> …). An identifier that happens to contain one is withheld too — losing a name in the audit is
    /// the safe direction.</summary>
    private static readonly string[] PayloadParts =
    [
        "command", "query", "sql", "script", "content", "body", "compose", "yaml", "prompt", "mounts",
        "password", "passwd", "secret", "token", "credential", "privatekey", "pem",
    ];

    /// <summary>Whole names (after normalising) that are payloads although no part above matches them.</summary>
    private static readonly HashSet<string> PayloadNames = new(StringComparer.Ordinal)
    {
        "env", "envvars", "environment", "volumes",
    };

    /// <summary>Words that contain a part above without being a payload (<c>de·script·ion</c>). Only whole names: a
    /// <c>descriptionScript</c> is still withheld.</summary>
    private static readonly HashSet<string> NotPayloadNames = new(StringComparer.Ordinal)
    {
        "description", "subscription",
    };

    /// <summary>Whether the argument <paramref name="name"/> carries a free-text payload whose value is never stored.</summary>
    public static bool IsPayload(string name)
    {
        var normalized = Normalize(name);
        if (NotPayloadNames.Contains(normalized)) return false;
        return PayloadNames.Contains(normalized) || PayloadParts.Any(normalized.Contains);
    }

    private static string Normalize(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>The length-only stand-in for a payload value. A value that already is one stays as it is, so applying
    /// the redaction twice changes nothing.</summary>
    public static string Withheld(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text)
            return IsWithheld(text) ? text : $"<withheld: {text.Length} chars>";
        return $"<withheld: {value.GetRawText().Length} chars>";
    }

    // Exactly the marker this class writes: a caller's "<withheld: …>" carrying anything else is a payload like any other.
    private static readonly System.Text.RegularExpressions.Regex WithheldMarker = new(
        @"^<withheld: [0-9]{1,10} chars>$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsWithheld(string text) => WithheldMarker.IsMatch(text);

    /// <summary>The length-only stand-in for a payload text (a command, SQL) a writer names directly.</summary>
    public static string Withheld(string text) => IsWithheld(text) ? text : $"<withheld: {text.Length} chars>";

    /// <summary>Free text: URL user info removed, then the secret patterns. A pattern that times out withholds the
    /// whole text (fail closed).</summary>
    public static string? Text(string? text) =>
        string.IsNullOrEmpty(text) ? text
            : SecretRedactor.FailClosed(text, t => SecretRedactor.Redact(WithoutUrlCredentials(t)));

    /// <summary>A document that may be JSON: redacted value by value (so a pattern never runs across quotes and
    /// commas), plain text otherwise.</summary>
    public static string? Json(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return Write(writer => WriteRedacted(writer, null, document.RootElement, payloads: false));
        }
        catch (JsonException)
        {
            // not JSON: redacted as text below
        }
        return Text(text);
    }

    /// <summary>Tool or mutation arguments as JSON: payload arguments keep only their length, a secret-named key (at
    /// any depth) only <c>***</c>, every other string is redacted on its own. Text that is no JSON object is redacted
    /// as a document.</summary>
    public static string? Arguments(string? json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return Write(writer => WriteRedacted(writer, null, document.RootElement, payloads: true));
        }
        catch (JsonException)
        {
            // not JSON: redacted as text below
        }
        return Json(json);
    }

    /// <summary>The arguments as one JSON object, redacted as <see cref="Arguments(string?)"/> does.</summary>
    public static string Arguments(IReadOnlyDictionary<string, JsonElement> arguments) =>
        // Written element by element (not through a node tree): a document with a repeated property name is valid
        // input and is kept as it came — redacting it must never fail, or the attempt would have no event.
        Write(writer =>
        {
            writer.WriteStartObject();
            foreach (var (key, value) in arguments)
            {
                writer.WritePropertyName(key);
                WriteArgument(writer, key, value);
            }
            writer.WriteEndObject();
        });

    /// <summary>Shortens a stored text to at most <paramref name="max"/> characters. A JSON document is shortened by
    /// its string values (then, if still too long, replaced by a valid stub) so it stays valid JSON — redacting it again
    /// on save then runs value by value, never a pattern across a cut document's quotes.</summary>
    public static string? Cap(string? text, int max)
    {
        if (text is null || text.Length <= max) return text;
        if (max < 2) return "…";
        if (text is ['{' or '[', ..])
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                for (var limit = max; limit >= 16; limit /= 2)
                {
                    var shortened = Write(writer => WriteShortened(writer, document.RootElement, limit));
                    if (shortened.Length <= max) return shortened;
                }
                var stub = $"{{\"cut\":\"{text.Length} chars\"}}";
                if (stub.Length <= max) return stub;
            }
            catch (JsonException)
            {
                // not JSON: cut as text below
            }
        }
        var end = max - 1;
        if (char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }

    private static void WriteShortened(Utf8JsonWriter writer, JsonElement value, int limit)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteShortened(writer, property.Value, limit);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteShortened(writer, item, limit);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String when value.GetString() is { Length: > 0 } text && text.Length > limit:
                writer.WriteStringValue(Cap(text, limit));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    /// <summary>The arguments with every payload replaced by its length (for display and approval cards).</summary>
    public static IReadOnlyDictionary<string, object?> WithoutPayloads(IReadOnlyDictionary<string, JsonElement> arguments) =>
        arguments.ToDictionary(a => a.Key, a => IsPayload(a.Key) ? (object?)Withheld(a.Value) : a.Value);

    /// <summary>Removes the values of payload arguments from free text such as a refusal reason.</summary>
    public static string? WithoutPayloadValues(string? text, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (key, value) in arguments)
        {
            if (!IsPayload(key) || value.ValueKind != JsonValueKind.String) continue;
            var payload = value.GetString();
            if (!string.IsNullOrEmpty(payload) && payload.Length >= 3)
                text = text.Replace(payload, "<withheld>", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>A URL's user info (<c>https://user:token@host</c>, also a token alone in front of the host) as
    /// <c>***</c>: no pattern recognises a bare token there.</summary>
    public static string? WithoutUrlCredentials(string? value) =>
        Whiskers.Services.Mutations.PolicyDecisions.WithoutUrlCredentials(value);

    private static void WriteArgument(Utf8JsonWriter writer, string key, JsonElement value)
    {
        // A secret-named key is "***" (not even its length); other payloads keep their length.
        if (IsPayload(key) && !SecretKeyName.IsMatch(key)) writer.WriteStringValue(Withheld(value));
        else WriteRedacted(writer, key, value, payloads: true);
    }

    private static void WriteRedacted(Utf8JsonWriter writer, string? key, JsonElement value, bool payloads)
    {
        // A secret-named key hides everything below it — a value, a list, an object alike.
        if (key is not null && SecretKeyName.IsMatch(key))
        {
            writer.WriteStringValue("***");
            return;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (payloads) WriteArgument(writer, property.Name, property.Value);
                    else WriteRedacted(writer, property.Name, property.Value, payloads);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteRedacted(writer, null, item, payloads);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                // A string that is itself a JSON document (a gateway event's arguments) is redacted value by value, as
                // its writer did: a pattern run over its text would cross its quotes and commas.
                var text = value.GetString();
                writer.WriteStringValue(text is ['{' or '[', ..] ? Json(text) : Text(text));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        // Readable text (a quote stays a quote, not '): the stored JSON is shown HTML-encoded, never raw.
        using (var writer = new Utf8JsonWriter(buffer,
                   new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(writer);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static readonly System.Text.RegularExpressions.Regex SecretKeyName = new(
        "(?i)password|passwd|pwd|passwort|kennwort|token|secret|api[_-]?key|access[_-]?key|authorization|bearer|credential|private[_-]?key",
        System.Text.RegularExpressions.RegexOptions.Compiled);
}
