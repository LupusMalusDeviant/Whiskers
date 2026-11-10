using System.Security.Claims;
using Whiskers.Models;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.AuditLog;

/// <summary>
/// INV-01 (contract 0014, D6/D7): the principals of the audit rows written outside the gateway, built from typed ids —
/// a component of Whiskers, a person by their account handle, a key by its id — and the actor type a row derives
/// from its principal.
/// </summary>
public static class AuditPrincipals
{
    /// <summary>What the <c>ActorType</c> column says for <paramref name="p"/>: <c>web</c> for a person, <c>mcp</c> for a
    /// key, <c>system</c> for Whiskers itself, else the kind in lower case (<c>trigger</c> for an AI trigger).</summary>
    public static string ActorTypeOf(MutationPrincipal p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.Kind switch
        {
            MutationPrincipalKind.Human => "web",
            MutationPrincipalKind.ApiKey => "mcp",
            MutationPrincipalKind.System => "system",
            MutationPrincipalKind.Automation when PrincipalIds.TriggerIdOf(p.Id) is not null => "trigger",
            _ => p.Kind.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>A component of Whiskers (D6): <c>system:&lt;component&gt;</c>, shown as <paramref name="display"/>,
    /// optionally on behalf of whoever set the work up (a restore's requester).</summary>
    public static MutationPrincipal System(string component, string display = "system", MutationPrincipal? onBehalfOf = null) =>
        new(MutationPrincipalKind.System, PrincipalIds.System(component), display, McpPermissionLevels.None,
            OnBehalfOf: onBehalfOf, SessionId: ProcessIdentity.Current);

    /// <summary>A host CLI (S-10, S-11, S-15): <c>system:host-cli:&lt;command&gt;</c>.</summary>
    public static MutationPrincipal HostCli(string command, string display) =>
        new(MutationPrincipalKind.System, PrincipalIds.HostCli(command), display, McpPermissionLevels.None,
            SessionId: ProcessIdentity.Current);

    /// <summary>A person by their account handle (<c>user:&lt;email&gt;</c>), or <see cref="PrincipalIds.Anonymous"/>
    /// when there is none (a refused recovery code). The handle is the display unless one is given.</summary>
    public static MutationPrincipal Person(string? account, string? display = null, string? credentialId = null,
        string? sessionId = null) =>
        string.IsNullOrWhiteSpace(account) || !account.Contains('@')
            ? new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.Anonymous, display ?? "anonymous",
                McpPermissionLevels.None)
            : new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(account), display ?? account,
                McpPermissionLevels.None, CredentialId: credentialId, SessionId: sessionId);

    /// <summary>The caller of a recorded HTTP read (a self-backup download, an audit export): the signed-in person
    /// with their sign-in credential and session (contract 0014, D2), or the MCP key by its id; anonymous otherwise.</summary>
    public static MutationPrincipal OfHttpContext(Microsoft.AspNetCore.Http.HttpContext? context,
        IMcpPermissionService? keys = null)
    {
        if (context is null) return Person(null);
        var email = context.User?.FindFirst(ClaimTypes.Email)?.Value;
        if (!string.IsNullOrEmpty(email))
        {
            var (credential, session) = Auth.WebSessionClaims.Of(context.User);
            return Person(email, credentialId: credential, sessionId: session);
        }
        var (actor, _) = IAuditLogService.GetActorFromHttpContext(context, keys);
        return PrincipalIds.KeyIdOf(actor) is { } keyId
            ? new MutationPrincipal(MutationPrincipalKind.ApiKey, actor, actor, McpPermissionLevels.None, CredentialId: keyId)
            : Person(null, actor);
    }
}
