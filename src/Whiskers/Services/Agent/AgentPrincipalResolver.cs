using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Auth;
using Whiskers.Services.Mcp;

namespace Whiskers.Services.Agent;

/// <summary>Derives the AgentPrincipal from the HTTP context — mirrors the resolution in
/// McpPermissionCheck, so the agent inherits exactly the rights of its trigger: bearer MCP key
/// (the key's Level + AllowedTools) or cookie web user (role → level). AuthDisabled = admin.</summary>
public sealed class AgentPrincipalResolver : IAgentPrincipalResolver
{
    private readonly IMcpPermissionService _permissions;
    private readonly IRoleService _roles;

    public AgentPrincipalResolver(IMcpPermissionService permissions, IRoleService roles)
    {
        _permissions = permissions;
        _roles = roles;
    }

    public AgentPrincipal Resolve(HttpContext httpContext)
    {
        // Bearer-MCP-Key hat Vorrang (externer Agent, z.B. Claude Code).
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var key = authHeader["Bearer ".Length..];
            var config = _permissions.ValidateKey(key);
            if (config != null)
                return new AgentPrincipal(
                    AgentPrincipalKind.McpKey, config.Name, config.PermissionLevel,
                    config.AllowedTools, McpKeyId: config.Id);
        }

        // Web-User per Cookie.
        if (httpContext.User.Identity?.AuthenticationType == "AuthDisabled")
            return new AgentPrincipal(AgentPrincipalKind.WebUser, "local",
                McpPermissionLevels.Admin, null, UserEmail: "local@serverwatch.local");

        var email = httpContext.User.FindFirst(ClaimTypes.Email)?.Value;
        var role = _roles.GetRole(email);
        var level = role switch
        {
            AppRole.Admin => McpPermissionLevels.Admin,
            AppRole.Operator => McpPermissionLevels.Write,
            _ => McpPermissionLevels.Read,
        };
        return new AgentPrincipal(AgentPrincipalKind.WebUser, email ?? "unbekannt", level, null, UserEmail: email);
    }
}
