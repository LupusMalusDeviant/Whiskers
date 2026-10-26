using System.Text.Json;
using Whiskers.Models;

namespace Whiskers.Services.Approvals;

/// <summary>Display-only context for an approval card: a coarse risk band and a best-effort target. Neither value
/// changes what executes — an approved call is bound by its operation id and its arguments hash.</summary>
public static class ApprovalDisplay
{
    /// <summary>Coarse risk band from the required permission level.</summary>
    public static string RiskOf(string level) => McpPermissionLevels.Normalize(level) switch
    {
        "admin" => "high",
        "write" => "medium",
        _ => "low",
    };

    // Well-known argument keys that name the target of an operation.
    private static readonly string[] ServerKeys = { "serverId", "server", "serverName" };
    private static readonly string[] WorkloadKeys = { "containerId", "container", "name", "pod", "workload", "service" };

    public static (string? server, string? workload) ExtractTarget(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            return (FirstString(doc.RootElement, ServerKeys), FirstString(doc.RootElement, WorkloadKeys));
        }
        catch (JsonException) { return (null, null); }
    }

    private static string? FirstString(JsonElement obj, string[] keys)
    {
        foreach (var k in keys)
            if (obj.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        return null;
    }
}
