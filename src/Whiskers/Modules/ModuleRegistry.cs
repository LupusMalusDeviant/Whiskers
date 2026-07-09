namespace Whiskers.Modules;

/// <summary>Default <see cref="IModuleRegistry"/>: holds the aggregated nav entries and the set of enabled
/// module ids, both handed to it at composition time from <see cref="ModuleCatalog.DiscoverEnabled"/>
/// (RoadToSAP Phase 1).</summary>
public sealed class ModuleRegistry : IModuleRegistry
{
    private readonly HashSet<string> _enabledIds;

    public ModuleRegistry(IReadOnlyList<NavItem> navItems, IEnumerable<string> enabledModuleIds)
    {
        NavItems = navItems;
        _enabledIds = enabledModuleIds.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<NavItem> NavItems { get; }

    public bool IsEnabled(string moduleId) => _enabledIds.Contains(moduleId);
}
