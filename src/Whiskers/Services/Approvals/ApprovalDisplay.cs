using System.Text.Json;
using Whiskers.Models;

namespace Whiskers.Services.Approvals;

/// <summary>Display-only context for an approval card: a coarse risk band and a best-effort target. Neither value
/// changes what executes — an approved call is bound by its <see cref="Whiskers.Models.Agent.Approval.Binding"/>.</summary>
public static class ApprovalDisplay
{
    /// <summary>Coarse risk band from the required permission level.</summary>
    public static string RiskOf(string level) => McpPermissionLevels.Normalize(level) switch
    {
        "admin" => "high",
        "write" => "medium",
        _ => "low",
    };

    /// <summary>Where the approval is decided: its card on the Approvals page.</summary>
    public static string LinkOf(string approvalId) => $"/approvals#{approvalId}";

    /// <summary>The notification for a raised approval (C-22): who wants to run what, the approval id, the risk, the
    /// target and a link to decide it. Never the arguments — the bell may forward this to external channels.</summary>
    public static string NotificationText(Whiskers.Models.Agent.Approval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var target = approval.TargetServer is null && approval.TargetWorkload is null
            ? "—"
            : $"{Short(approval.TargetServer) ?? "—"} / {Short(approval.TargetWorkload) ?? "—"}";
        return $"\"{Short(approval.Actor)}\" wants to run \"{approval.ToolName}\" — approval required. " +
               $"Approval {approval.Id} · risk {approval.RiskLevel ?? RiskOf(approval.Level)} · target {target} · " +
               LinkOf(approval.Id);
    }

    // A target is a name, not a payload: one line, bounded.
    private static string? Short(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var line = string.Join(' ', value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 80 ? line[..80] + "…" : line;
    }

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
