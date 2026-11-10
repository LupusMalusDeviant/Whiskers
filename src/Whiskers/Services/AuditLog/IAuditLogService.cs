using Whiskers.Models;

namespace Whiskers.Services.AuditLog;

public interface IAuditLogService
{
    Task LogAsync(string actor, string actorType, string action,
                  string targetType, string targetId, string targetName,
                  string? details = null, string? serverId = null, bool success = true);

    /// <summary>Like <see cref="LogAsync"/>, and tells whether the row was stored — for a caller that must not go on
    /// without its record (fail-closed, e.g. handing out a backup archive, R1-22).</summary>
    Task<bool> TryLogAsync(string actor, string actorType, string action,
                           string targetType, string targetId, string targetName,
                           string? details = null, string? serverId = null, bool success = true);

    /// <summary>Like <see cref="TryLogAsync"/>, stamped with the time the event happened rather than the time the row
    /// is written — for a row that waited in a queue (sign-ins, R1-22).</summary>
    Task<bool> TryLogAtAsync(DateTime occurredUtc, string actor, string actorType, string action,
                             string targetType, string targetId, string targetName,
                             string? details = null, string? serverId = null, bool success = true) =>
        TryLogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);

    /// <summary>
    /// INV-01 (contract 0014, D7): writes a row with every principal column. The display actor and the actor type are
    /// derived from the principal (<see cref="AuditPrincipals"/>), never the other way round. Every row Whiskers writes
    /// outside the gateway goes through here; the string overloads above stay for the MCP tools' enrichment
    /// (<see cref="ToolAuditLog"/>) only — a source test pins that. Tells whether the row was stored.
    /// </summary>
    Task<bool> TryLogAsync(Mutations.MutationPrincipal principal, string action, string targetType, string targetId,
        string targetName, string? details = null, string? serverId = null, bool success = true,
        DateTime? occurredUtc = null) =>
        TryLogAtAsync(occurredUtc ?? DateTime.UtcNow, principal.DisplayName, AuditPrincipals.ActorTypeOf(principal),
            action, targetType, targetId, targetName, details, serverId, success);

    Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0,
                                               string? actionFilter = null,
                                               string? targetTypeFilter = null);

    Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null);

    /// <summary>
    /// Extracts the actor name and type from an HTTP context.
    /// Returns (actorName, actorType) — e.g. ("user@example.com", "web") or ("Claude Code", "mcp").
    /// </summary>
    static (string Actor, string ActorType) GetActorFromHttpContext(
        Microsoft.AspNetCore.Http.HttpContext? httpContext,
        Mcp.IMcpPermissionService? permissionService = null)
    {
        if (httpContext == null)
            return ("system", "system");

        // Check for authenticated web user
        var email = httpContext.User?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        if (!string.IsNullOrEmpty(email))
            return (email, "web");

        // Check for MCP API key
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && permissionService != null) // NIED-1
        {
            var key = authHeader["Bearer ".Length..];
            var keyConfig = permissionService.ValidateKey(key);
            // The key's id, not its renameable name (INV-01).
            if (keyConfig != null)
                return (Mutations.PrincipalIds.Key(keyConfig.Id), "mcp");
        }

        // Check for MCP bearer identity
        if (httpContext.User?.HasClaim("mcp-api-key", "true") == true)
            return ("mcp-client", "mcp");

        return ("anonymous", "web");
    }
}
