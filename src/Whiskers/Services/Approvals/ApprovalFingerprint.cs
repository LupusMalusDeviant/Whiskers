using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Whiskers.Services.Approvals;

/// <summary>Hashes that bind an approval to exactly what a human saw: the arguments (keys sorted, values as raw
/// JSON) and, for approve-then-retry, the caller and the tool.</summary>
public static class ApprovalFingerprint
{
    public static string OfArguments(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return Hash(Canonical(new StringBuilder(), arguments).ToString());
    }

    /// <summary>Who called, which tool, with exactly which arguments.</summary>
    public static string Of(string actorKey, string toolName, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var sb = new StringBuilder().Append(actorKey).Append('\n').Append(toolName).Append('\n');
        return Hash(Canonical(sb, arguments).ToString());
    }

    private static StringBuilder Canonical(StringBuilder sb, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        foreach (var (key, value) in arguments.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(key).Append('=').Append(value.ValueKind == JsonValueKind.Undefined ? "" : value.GetRawText()).Append('\n');
        return sb;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
