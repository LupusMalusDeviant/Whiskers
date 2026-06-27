using System.Security.Claims;
using ServerWatch.Models;
using ServerWatch.Services.Auth;
using ServerWatch.Services.Mcp;

namespace ServerWatch.Mcp;

/// <summary>
/// Helper to check MCP tool permissions from within tool methods.
/// Extracts the API key (or the authenticated web user's role) from the current HTTP context.
/// </summary>
public static class McpPermissionCheck
{
    /// <summary>
    /// Checks if the current caller has permission to execute the given tool.
    /// Returns null if allowed, or an error message string if denied.
    /// </summary>
    public static string? CheckAccess(IHttpContextAccessor httpContextAccessor, McpPermissionService permissionService, string toolName)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext == null)
            return "Kein HTTP-Kontext verfügbar.";

        // Web UI users authenticated by cookie (no Bearer header): authorize by their AppRole mapped to
        // the tool's required level (Viewer→read, Operator→write, Admin→admin). Previously ANY
        // authenticated cookie was granted full access, which let a Viewer invoke admin-level tools
        // (e.g. execute_command = root on the host) by POSTing to /mcp with their session cookie.
        if (httpContext.User.Identity?.IsAuthenticated == true
            && !httpContext.Request.Headers.Authorization.Any())
        {
            var requiredLevel = McpPermissionLevels.DefaultToolLevels.GetValueOrDefault(toolName, McpPermissionLevels.Admin);
            if (McpPermissionLevels.HasAccess(WebUserLevel(httpContext), requiredLevel))
                return null;
            return $"Zugriff verweigert: Ihre Rolle hat keine Berechtigung für '{toolName}'.";
        }

        // Extract API key from Bearer token (scheme is case-insensitive per RFC 7235).
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader == null || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return "Kein API-Key angegeben.";

        var key = authHeader["Bearer ".Length..];

        if (!permissionService.IsToolAllowed(key, toolName))
        {
            return $"Zugriff verweigert: Ihr API-Key hat keine Berechtigung für '{toolName}'.";
        }

        return null; // Allowed
    }

    /// <summary>Maps the authenticated web user's AppRole to an MCP permission level string.</summary>
    private static string WebUserLevel(HttpContext httpContext)
    {
        // AUTH_DISABLED trusted-LAN mode → synthetic Admin (matches CurrentUserService).
        if (httpContext.User.Identity?.AuthenticationType == "AuthDisabled")
            return McpPermissionLevels.Admin;

        var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value;
        var role = httpContext.RequestServices.GetService<RoleService>()?.GetRole(email) ?? AppRole.Viewer;
        return role switch
        {
            AppRole.Admin => McpPermissionLevels.Admin,
            AppRole.Operator => McpPermissionLevels.Write,
            _ => McpPermissionLevels.Read,
        };
    }
}
