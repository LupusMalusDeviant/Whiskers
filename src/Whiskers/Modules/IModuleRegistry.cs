namespace Whiskers.Modules;

/// <summary>
/// Aggregates cross-cutting metadata contributed by the enabled modules — starting, in Phase 0, with
/// the navigation entries. RoadToSAP Phase 0 scaffolding: the registry is populated by a single
/// all-in-one placeholder and is not yet consumed anywhere. Phase 1 makes <c>NavMenu.razor</c>
/// (and later the MCP tool list) read from it, and grows the interface with the real module contract.
/// </summary>
public interface IModuleRegistry
{
    /// <summary>Every module's navigation entries, unordered here (consumers group by
    /// <see cref="NavItem.Group"/> and sort by <see cref="NavItem.Order"/>).</summary>
    IReadOnlyList<NavItem> NavItems { get; }
}
