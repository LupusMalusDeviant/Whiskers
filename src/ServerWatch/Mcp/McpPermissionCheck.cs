using ServerWatch.Services.Mcp;

namespace ServerWatch.Mcp;

/// <summary>
/// Helper to check MCP tool permissions from within tool methods.
/// Extracts the API key from the current HTTP context and validates access.
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

        // Web UI users with valid auth cookie get full access
        if (httpContext.User.Identity?.IsAuthenticated == true
            && !httpContext.Request.Headers.Authorization.Any())
        {
            return null; // Allowed
        }

        // Extract API key from Bearer token
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader == null || !authHeader.StartsWith("Bearer "))
            return "Kein API-Key angegeben.";

        var key = authHeader["Bearer ".Length..];

        if (!permissionService.IsToolAllowed(key, toolName))
        {
            return $"Zugriff verweigert: Ihr API-Key hat keine Berechtigung für '{toolName}'.";
        }

        return null; // Allowed
    }
}
