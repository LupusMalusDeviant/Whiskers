using System.Text;
using System.Text.Json;
using Whiskers.Models.Agent;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Approvals;

/// <summary>
/// What an approval is bound to (R1-14, INV-30/31): who asks (principal, the credential it authenticated with and whom
/// it acts for), what (capability or tool and the level it needs), with exactly which normalized arguments, on which
/// resolved target, under which policy version. A grant is usable only by a call with the same binding, so a changed
/// target or a changed policy never inherits an earlier approval.
/// </summary>
public static class ApprovalBinding
{
    /// <summary>The binding of a gateway call, from the request as the gateway resolved it.</summary>
    public static string Of(MutationRequest request, Capability capability, string? policyVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capability);
        var p = request.Principal;
        var target = request.Target;
        return Hash(w =>
        {
            w.WriteString("kind", "mutation");
            w.WriteString("principalKind", p.Kind.ToString());
            w.WriteString("principal", p.Id);
            w.WriteString("credential", p.CredentialId);
            w.WriteString("onBehalfOf", p.OnBehalfOf?.Id);
            w.WriteString("onBehalfOfCredential", p.OnBehalfOf?.CredentialId);
            w.WriteString("capability", capability.Id);
            w.WriteString("level", capability.RequiredLevel);
            w.WriteString("arguments", PolicyCanonicalJson.InputHash(request.Arguments));
            w.WriteString("targetKind", target.Kind);
            w.WriteString("targetId", target.Id);
            w.WriteString("targetServer", target.ServerId);
            w.WriteStartArray("targetRelated");
            foreach (var related in target.Related.Order(StringComparer.Ordinal)) w.WriteStringValue(related);
            w.WriteEndArray();
            w.WriteString("policy", policyVersion);
        });
    }

    /// <summary>The binding of a direct MCP read that needs a confirmation.</summary>
    public static string OfDirectCall(AgentPrincipal principal, string toolName, string level,
        IReadOnlyDictionary<string, JsonElement> arguments, string? policyVersion)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(arguments);
        return Hash(w =>
        {
            w.WriteString("kind", "direct");
            w.WriteString("principalKind", principal.Kind.ToString());
            w.WriteString("principal", PrincipalIds.Of(principal));
            w.WriteString("credential", principal.McpKeyId);
            w.WriteString("tool", toolName);
            w.WriteString("level", level);
            w.WriteString("arguments", PolicyCanonicalJson.InputHash(arguments));
            w.WriteString("policy", policyVersion);
        });
    }

    private static string Hash(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return PolicyCanonicalJson.Hash(Encoding.UTF8.GetString(stream.ToArray()));
    }
}
