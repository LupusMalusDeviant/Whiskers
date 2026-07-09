namespace Whiskers.Modules;

/// <summary>Default <see cref="IModuleRegistry"/>: holds the aggregated module metadata handed to it at
/// composition time. RoadToSAP Phase 0 scaffolding.</summary>
public sealed class ModuleRegistry : IModuleRegistry
{
    public ModuleRegistry(IReadOnlyList<NavItem> navItems) => NavItems = navItems;

    public IReadOnlyList<NavItem> NavItems { get; }
}
