using System.Text.Json;

namespace Whiskers.Services.Guardrails;

/// <summary>
/// Contract 0018 (D1, D5, INV-64): an onboarding with a cloud bootstrap (<c>bootstrap</c> other than <c>none</c>) changes
/// access to a live server and reboots it twice, so it needs a human Admin's approval whatever the preset. Code, not
/// policy: it reads no policy field, so no preset, tool list or saved policy removes or weakens it. Part of
/// <see cref="GuardrailEngine.CreateDefault"/> only — a person in the UI decides in the confirmation dialog (PC-D1), and
/// <see cref="GuardrailEngine.CreateForHumans"/> keeps exactly its three rules. Its reason names the VM the gateway
/// resolved (<see cref="ResolvedPrefix"/> keys), so the approval card shows what a second person approves.
/// </summary>
public sealed class CloudBootstrapRule : IGuardrailRule
{
    public const string RuleId = "cloud-bootstrap";

    /// <summary>The prefix of the bound VM's facts the gateway adds for rule evaluation (id, name, created, labels, ip).</summary>
    public const string ResolvedPrefix = "__resolvedCloudVm";

    /// <summary>The capability and its MCP tool (part E).</summary>
    private static readonly HashSet<string> Onboard = new(StringComparer.Ordinal) { "server.onboard", "onboard_server" };

    public string Id => RuleId;

    public GuardrailVerdict Evaluate(GuardrailRequest request, out string reason)
    {
        reason = "";
        if (!Onboard.Contains(request.ToolName) || !request.Arguments.TryGetValue("bootstrap", out var bootstrap))
            return GuardrailVerdict.Allow;
        // Only an explicit "none" is no bootstrap; anything else (another value, a non-string) is one — fail closed.
        if (bootstrap.ValueKind is JsonValueKind.Null
            || (bootstrap.ValueKind == JsonValueKind.String && string.Equals(bootstrap.GetString(), "none", StringComparison.OrdinalIgnoreCase)))
            return GuardrailVerdict.Allow;
        string? Fact(string name) =>
            request.Arguments.TryGetValue(ResolvedPrefix + name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        reason = $"Hetzner VM '{Fact("Name") ?? "?"}' (id {Fact("Id") ?? "?"}, created {Fact("Created") ?? "?"}, labels " +
                 $"{(string.IsNullOrEmpty(Fact("Labels")) ? "none" : Fact("Labels"))}) at {Fact("Ip") ?? "?"} will be rebooted twice " +
                 "into the rescue system and back; one line is added to /root/.ssh/authorized_keys and removed at the end. " +
                 "A cloud bootstrap needs an Admin's approval.";
        return GuardrailVerdict.Confirm;
    }
}
