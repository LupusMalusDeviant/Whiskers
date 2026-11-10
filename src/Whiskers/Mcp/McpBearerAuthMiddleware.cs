using System.Security.Claims;
using Whiskers.Models;
using Whiskers.Services.Mcp;

namespace Whiskers.Mcp;

/// <summary>
/// Middleware that authenticates MCP requests with Bearer tokens before
/// the default Google OAuth challenge kicks in. Without this, ASP.NET
/// redirects unauthenticated MCP requests to the Google login page (302).
/// </summary>
/// <remarks>Contract 0016: the principal also carries a subject (<see cref="ClaimTypes.NameIdentifier"/>) so the MCP
/// SDK can bind a stateful session to the key that opened it. The subject is the key's record id, never the secret,
/// its hash or its hint. A key whose record id is empty or not unique gets no principal and the request is refused
/// with 401 (fail-closed) instead of running on an unbound or shared session.</remarks>
public class McpBearerAuthMiddleware
{
    /// <summary>Prefix of the subject value; keeps key subjects apart from local user ids.</summary>
    public const string SubjectPrefix = "mcp-key:";
    /// <summary>Issuer of the subject claim.</summary>
    public const string SubjectIssuer = "Whiskers.McpBearer";

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
            // NIED-1: RFC 7235 auth schemes are case-insensitive — accept "bearer"/"BEARER" too.
            if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var key = authHeader["Bearer ".Length..];
                var permService = context.RequestServices.GetService<IMcpPermissionService>();

                var config = permService?.ValidateKey(key);
                if (config != null)
                {
                    if (!HasOwnSubject(config, permService!))
                    {
                        context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<McpBearerAuthMiddleware>()
                            .LogWarning("MCP key refused: its record id is empty or shared with another key (contract 0016)");
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                    context.User = BuildPrincipal(config);
                }
            }
        }

        await _next(context);
    }

    /// <summary>The principal of a valid key: the marker claim plus the subject the SDK binds sessions to.</summary>
    internal static ClaimsPrincipal BuildPrincipal(McpApiKeyConfig config)
    {
        var claims = new[]
        {
            new Claim("mcp-api-key", "true"),
            new Claim(ClaimTypes.NameIdentifier, SubjectPrefix + config.Id, ClaimValueTypes.String, SubjectIssuer),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "McpBearer"));
    }

    /// <summary>True when the key's record id is set and no other enabled key has the same id.</summary>
    internal static bool HasOwnSubject(McpApiKeyConfig config, IMcpPermissionService permissions)
    {
        if (string.IsNullOrWhiteSpace(config.Id))
            return false;
        var sameId = permissions.GetPermissionData().ApiKeys
            .Count(k => k.Enabled && string.Equals(k.Id, config.Id, StringComparison.Ordinal));
        return sameId == 1;
    }
}
