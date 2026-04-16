using System.Security.Claims;
using ServerWatch.Services.Mcp;

namespace ServerWatch.Mcp;

/// <summary>
/// Middleware that authenticates MCP requests with Bearer tokens before
/// the default Google OAuth challenge kicks in. Without this, ASP.NET
/// redirects unauthenticated MCP requests to the Google login page (302).
/// </summary>
public class McpBearerAuthMiddleware
{
    private readonly RequestDelegate _next;

    public McpBearerAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only intercept requests to the MCP endpoint
        if (context.Request.Path.StartsWithSegments("/mcp"))
        {
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (authHeader != null && authHeader.StartsWith("Bearer "))
            {
                var key = authHeader["Bearer ".Length..];
                var permService = context.RequestServices.GetService<McpPermissionService>();

                if (permService?.ValidateKey(key) != null)
                {
                    // Set a minimal authenticated identity so RequireAuthorization passes
                    var claims = new[] { new Claim("mcp-api-key", "true") };
                    var identity = new ClaimsIdentity(claims, "McpBearer");
                    context.User = new ClaimsPrincipal(identity);
                }
            }
        }

        await _next(context);
    }
}
