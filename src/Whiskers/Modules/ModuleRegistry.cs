namespace Whiskers.Modules;

/// <summary>Default <see cref="IModuleRegistry"/>: holds the aggregated nav entries, the aggregated MCP tool
/// types and the set of enabled module ids, all handed to it at composition time from the enabled module set
/// (<see cref="ModuleCatalog.DiscoverEnabled"/>, RoadToSAP Phase 1).</summary>
public sealed class ModuleRegistry : IModuleRegistry
{
    private readonly HashSet<string> _enabledIds;

    public ModuleRegistry(
        IReadOnlyList<NavItem> navItems,
        IReadOnlyList<Type> mcpToolTypes,
        IEnumerable<string> enabledModuleIds)
    {
        NavItems = navItems;
        McpToolTypes = mcpToolTypes;
        _enabledIds = enabledModuleIds.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<NavItem> NavItems { get; }

    public IReadOnlyList<Type> McpToolTypes { get; }

    public bool IsEnabled(string moduleId) => _enabledIds.Contains(moduleId);
}
