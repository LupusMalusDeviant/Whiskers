using Whiskers.Models;
using Whiskers.Services.Mcp;

namespace Whiskers.Services.Mutations;

/// <summary>Whether a capability (or tool) may run at all and the level it requires right now.</summary>
public sealed record CapabilityAvailability(bool Enabled, string RequiredLevel);

/// <summary>
/// Operator settings applied on top of the code levels for <b>every</b> principal kind (INV-15): the MCP tool's Enabled
/// switch and a stored level stricter than the code default. A stored level can only raise the requirement; the
/// code level is the floor.
/// </summary>
public interface ICapabilityOverrides
{
    CapabilityAvailability Resolve(Capability capability);

    /// <summary>The same rule for a tool without a capability (a read), whose code level is <paramref name="codeLevel"/>.</summary>
    CapabilityAvailability ResolveTool(string toolName, string codeLevel);
}

/// <summary>Reads the switches from the MCP tool settings (Settings → MCP → Tools).</summary>
public sealed class McpToolCapabilityOverrides : ICapabilityOverrides
{
    private readonly IMcpPermissionService _permissions;

    public McpToolCapabilityOverrides(IMcpPermissionService permissions) =>
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));

    public CapabilityAvailability Resolve(Capability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return capability.McpTool is null
            ? new CapabilityAvailability(true, capability.RequiredLevel)
            : ResolveTool(capability.McpTool, capability.RequiredLevel);
    }

    public CapabilityAvailability ResolveTool(string toolName, string codeLevel)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        if (!_permissions.GetPermissionData().Tools.TryGetValue(toolName, out var tool))
            return new CapabilityAvailability(true, codeLevel);

        var stored = McpPermissionLevels.Normalize(tool.RequiredLevel);
        var level = McpPermissionLevels.GetRank(stored) > McpPermissionLevels.GetRank(codeLevel) ? stored : codeLevel;
        return new CapabilityAvailability(tool.Enabled, level);
    }
}
