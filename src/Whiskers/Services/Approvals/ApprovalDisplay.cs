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

    /// <summary>What the approval card (in the app, for those who may decide) shows of a gateway target: its canonical
    /// id and server — only when the resolver found it (<see cref="Whiskers.Services.Mutations.MutationTarget.Resolved"/>).
    /// A notification names neither: a resolved target may still carry a server id the caller passed (a DNS zone has
    /// no server), so only <see cref="NotificationText"/>'s target kind leaves the app.</summary>
    public static (string? Server, string? Resource) NotifiableTarget(Whiskers.Services.Mutations.MutationTarget? target) =>
        target is { Resolved: true } ? (target.ServerId, target.Id) : (null, null);

    /// <summary>Where the approval is decided: its card on the Approvals page (relative, path-base-safe).</summary>
    public static string LinkOf(string approvalId) => $"approvals#{Uri.EscapeDataString(approvalId)}";

    /// <summary>The notification for a raised approval (C-22): who wants to run what, the approval id, the risk and
    /// the kind of its target (<paramref name="targetKind"/>, the capability's — <c>container</c>, <c>dns-zone</c>, …)
    /// — never a server, a resource name or any other value a caller can put into the call, and never the arguments:
    /// the bell may forward this to external channels. The concrete target is on the approval's card. The in-app link to the card travels as <c>NotificationEvent.ApprovalId</c>; with a
    /// configured public address (<paramref name="publicUrl"/>) the text carries the absolute link too, so e-mail and
    /// chat channels open the card.</summary>
    public static string NotificationText(Whiskers.Models.Agent.Approval approval, string? targetKind,
        Whiskers.Configuration.WhiskersPublicUrl? publicUrl = null)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var target = Short(targetKind) ?? "—";
        return $"\"{Short(approval.Actor)}\" wants to run \"{approval.ToolName}\" — approval required. " +
               $"Approval {approval.Id} · risk {approval.RiskLevel ?? RiskOf(approval.Level)} · target {target} " +
               (publicUrl?.Link(LinkOf(approval.Id)) is { } link ? $"— {link}" : "(Whiskers → Approvals)");
    }

    /// <summary>What a notification shows of a value taken from the arguments: an identifier (letters, digits and
    /// <c>. _ - : / @</c>, at most 80 characters) that the secret redactor leaves alone; anything else is withheld.</summary>
    private static string? Short(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return Identifier.IsMatch(trimmed) && Whiskers.Utils.SecretRedactor.Redact(trimmed) == trimmed
            ? trimmed
            : "(withheld)";
    }

    private static readonly System.Text.RegularExpressions.Regex Identifier = new(
        @"^[A-Za-z0-9._:/@-]{1,80}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
