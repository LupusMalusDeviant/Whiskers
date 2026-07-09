namespace Whiskers.Modules;

/// <summary>A named sidebar group with its ordered items.</summary>
public sealed record NavGroup(string Name, IReadOnlyList<NavItem> Items);

/// <summary>Turns the registry's flat <see cref="NavItem"/> list into the sidebar layout: named groups
/// sorted by their lowest item <see cref="NavItem.Order"/> (items ordered by <c>Order</c> within a group),
/// plus the ungrouped top-level items. Pure and static so <c>NavMenu.razor</c> stays a thin view and the
/// ordering is unit-testable (RoadToSAP Phase 1).</summary>
public static class NavLayout
{
    /// <summary>Named groups (non-empty <see cref="NavItem.Group"/>), each with its items in <c>Order</c>,
    /// the groups themselves ordered by their lowest item <c>Order</c>.</summary>
    public static IReadOnlyList<NavGroup> Grouped(IReadOnlyList<NavItem> items) =>
        items.Where(i => !string.IsNullOrEmpty(i.Group))
             .GroupBy(i => i.Group)
             .OrderBy(g => g.Min(i => i.Order))
             .Select(g => new NavGroup(g.Key, g.OrderBy(i => i.Order).ToList()))
             .ToList();

    /// <summary>The ungrouped, top-level entries (empty <see cref="NavItem.Group"/>), ordered by <c>Order</c>.</summary>
    public static IReadOnlyList<NavItem> TopLevel(IReadOnlyList<NavItem> items) =>
        items.Where(i => string.IsNullOrEmpty(i.Group))
             .OrderBy(i => i.Order)
             .ToList();
}
