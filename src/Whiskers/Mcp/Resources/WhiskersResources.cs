using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Whiskers.Models;
using Whiskers.Modules;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;

namespace Whiskers.Mcp.Resources;

/// <summary>
/// Read-only MCP resources a client can attach as context. Both describe the MCP surface itself and contain no
/// secrets: the tool catalog is the same information as <c>docs/guide/mcp-tool-catalog.md</c>, and the permissions
/// resource only tells callers what they themselves may do. Neither exposes infrastructure data — that stays
/// behind the scoped, guardrailed tools.
/// </summary>
[McpServerResourceType]
public sealed class WhiskersResources
{
    [McpServerResource(UriTemplate = "whiskers://tools/catalog", Name = "tool_catalog", Title = "Tool catalog",
        MimeType = "text/markdown")]
    [Description("Every tool this Whiskers instance serves, with the permission level it requires and whether an admin has switched it off.")]
    public static string ToolCatalog(IModuleRegistry modules, IMcpPermissionService permissions)
    {
        var tools = permissions.GetPermissionData().Tools;
        var sb = new StringBuilder()
            .AppendLine("# Whiskers MCP tools")
            .AppendLine()
            .AppendLine("Levels: `read` < `write` < `admin`. A key reaches a tool only if its scope covers the level; every")
            .AppendLine("call additionally passes the guardrail policy (Allow / Confirm / Block).")
            .AppendLine()
            .AppendLine("| Tool | Level | Enabled |")
            .AppendLine("|---|---|---|");
        foreach (var d in McpToolLevelCatalog.Declarations(modules.McpToolTypes))
        {
            var level = McpPermissionLevels.DefaultToolLevels.GetValueOrDefault(d.ToolName, McpPermissionLevels.Admin);
            var enabled = !tools.TryGetValue(d.ToolName, out var cfg) || cfg.Enabled;
            sb.AppendLine($"| `{d.ToolName}` | {level} | {(enabled ? "yes" : "no")} |");
        }
        return sb.ToString();
    }

    [McpServerResource(UriTemplate = "whiskers://me/permissions", Name = "my_permissions", Title = "My permissions",
        MimeType = "text/markdown")]
    [Description("The calling key's (or signed-in user's) scope and the tools it may call — useful before planning a task.")]
    public static string MyPermissions(IHttpContextAccessor http, IModuleRegistry modules, IMcpPermissionService permissions,
        ICapabilityCatalog catalog, ICapabilityOverrides overrides)
    {
        var ctx = http.HttpContext;
        if (ctx is null) return "No caller context available.";

        var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
        string who, level, scope;
        Func<string, bool> keyAllows;
        if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var key = auth["Bearer ".Length..];
            var cfg = permissions.ValidateKey(key);
            if (cfg is null) return "The presented API key is not valid.";
            who = $"API key \"{cfg.Name}\"";
            level = cfg.PermissionLevel;
            scope = cfg.AllowedTools is null ? level : $"{level}, limited to an explicit tool list";
            keyAllows = tool => permissions.IsToolAllowed(key, tool);
        }
        else
        {
            who = "signed-in user";
            level = McpPermissionCheck.WebUserLevel(ctx);
            scope = level;
            keyAllows = _ => true;
        }
        // Only the tools this instance serves (a disabled module's tools are not), each as a call would be checked.
        var served = McpToolLevelCatalog.Declarations(modules.McpToolTypes).Select(d => d.ToolName).ToHashSet(StringComparer.Ordinal);
        var allowed = permissions.GetPermissionData().Tools.Keys.Where(served.Contains)
            .Where(tool => keyAllows(tool) && Callable(tool, level, catalog, overrides))
            .ToHashSet(StringComparer.Ordinal);

        var sb = new StringBuilder()
            .AppendLine("# My Whiskers permissions")
            .AppendLine()
            .AppendLine($"- Caller: {who}")
            .AppendLine($"- Scope: {scope}")
            .AppendLine("- Guardrails apply on top: a call may still be blocked or need a human approval.")
            .AppendLine()
            .AppendLine($"## Tools I may call ({allowed.Count})")
            .AppendLine();
        foreach (var tool in allowed.OrderBy(t => t, StringComparer.Ordinal))
            sb.AppendLine($"- `{tool}`");
        return sb.ToString();
    }

    /// <summary>Whether a caller of <paramref name="callerLevel"/> passes the level and switch checks a call meets
    /// (<see cref="McpGuardrailFilter"/>): a tool behind a capability is held to the capability's level, raised by a
    /// stricter stored level (the Mutation Gateway); a read to its code level, raised the same way. A key's tool list
    /// never widens this (INV-16). A level that some arguments raise further (a scheduled custom command) is not
    /// known here; such a call can still be refused, like one the guardrails block.</summary>
    internal static bool Callable(string tool, string callerLevel, ICapabilityCatalog catalog, ICapabilityOverrides overrides)
    {
        if (McpPermissionLevels.IsNone(callerLevel))
            return false;
        var availability = catalog.FindByMcpTool(tool) is { } capability
            ? overrides.Resolve(capability)
            : overrides.ResolveTool(tool, McpPermissionLevels.DefaultToolLevels.GetValueOrDefault(tool, McpPermissionLevels.Admin));
        return availability.Enabled
               && McpPermissionLevels.HasAccess(McpPermissionLevels.Normalize(callerLevel), availability.RequiredLevel);
    }
}
