using System.Reflection;
using ModelContextProtocol.Server;

namespace Whiskers.Mcp;

/// <summary>
/// Which MCP tools take a <c>serverId</c> parameter, read once from the <c>[McpServerTool]</c> methods of this
/// assembly. The gateway's MCP adapter passes the resolved server to every such tool, also when the client left it out
/// (R1-06): the decision and the operation then run on the same machine.
/// </summary>
public static class McpToolParameters
{
    private static readonly Lazy<HashSet<string>> WithServerId = new(() =>
        typeof(McpToolParameters).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null
                        && m.GetParameters().Any(p => p.Name == "serverId"))
            .Select(McpToolLevelCatalog.ResolveToolName)
            .ToHashSet(StringComparer.Ordinal));

    /// <summary>True when the tool <paramref name="toolName"/> declares a <c>serverId</c> parameter.</summary>
    public static bool TakesServerId(string? toolName) =>
        toolName is not null && WithServerId.Value.Contains(toolName);
}
