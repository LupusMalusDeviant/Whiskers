using ServerWatch.Models;

namespace ServerWatch.Services.AuditLog;

public interface IAuditLogService
{
    Task LogAsync(string actor, string actorType, string action,
                  string targetType, string targetId, string targetName,
                  string? details = null, string? serverId = null, bool success = true);

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
        if (authHeader != null && authHeader.StartsWith("Bearer ") && permissionService != null)
        {
            var key = authHeader["Bearer ".Length..];
            var keyConfig = permissionService.ValidateKey(key);
            if (keyConfig != null)
                return (keyConfig.Name, "mcp");
        }

        // Check for MCP bearer identity
        if (httpContext.User?.HasClaim("mcp-api-key", "true") == true)
            return ("mcp-client", "mcp");

        return ("anonymous", "web");
    }
}
