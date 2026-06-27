using System.Globalization;
using System.Text.Json;

namespace ServerWatch.Services.Agent;

/// <summary>Converts a JSON argument value from the LLM into the .NET parameter type of the tool method.
/// Tolerant of LLM quirks (number as string, etc.) but without silent data loss.</summary>
public static class AgentArgumentBinder
{
    /// <summary>Parses the ArgumentsJson string of a tool call into a case-insensitive dictionary.
    /// Broken JSON → empty dict (the tool then reports missing required fields).</summary>
    public static Dictionary<string, JsonElement> ParseArguments(string? json)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return dict;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var prop in doc.RootElement.EnumerateObject())
                    dict[prop.Name] = prop.Value.Clone();
        }
        catch (JsonException) { /* empty dict */ }
        return dict;
    }

    public static object? ConvertJson(JsonElement el, Type targetType)
    {
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (t == typeof(string))
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();

        if (t == typeof(bool))
            return el.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? el.GetBoolean()
                : bool.Parse(el.GetString() ?? "false");

        if (t == typeof(int))
            return el.ValueKind == JsonValueKind.Number ? el.GetInt32()
                : int.Parse(el.GetString() ?? "0", CultureInfo.InvariantCulture);

        if (t == typeof(long))
            return el.ValueKind == JsonValueKind.Number ? el.GetInt64()
                : long.Parse(el.GetString() ?? "0", CultureInfo.InvariantCulture);

        if (t == typeof(short))
            return el.ValueKind == JsonValueKind.Number ? el.GetInt16()
                : short.Parse(el.GetString() ?? "0", CultureInfo.InvariantCulture);

        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
            return el.ValueKind == JsonValueKind.Number ? el.GetDouble()
                : double.Parse(el.GetString() ?? "0", CultureInfo.InvariantCulture);

        // Fallback: raw JSON as string (the tool receives the unmodified representation).
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
    }
}
