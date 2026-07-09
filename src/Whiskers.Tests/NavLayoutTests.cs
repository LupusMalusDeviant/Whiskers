using Whiskers.Modules;

namespace Whiskers.Tests;

/// <summary>Verifies the sidebar layout the registry-driven NavMenu renders (RoadToSAP Phase 1) still matches
/// the previous hard-coded sidebar: same groups in the same order, same items in order, same top-level
/// entries, nothing dropped or duplicated — using the real AllInOnePseudoModule nav as the fixture.</summary>
public class NavLayoutTests
{
    private static IReadOnlyList<NavItem> Nav => new AllInOnePseudoModule().NavItems;

    [Fact]
    public void Groups_are_ordered_by_lowest_item_order()
    {
        Assert.Equal(
            new[] { "Übersicht", "Deployment", "Infrastruktur", "Automatisierung" },
            NavLayout.Grouped(Nav).Select(g => g.Name).ToArray());
    }

    [Fact]
    public void Items_within_a_group_are_ordered()
    {
        var overview = NavLayout.Grouped(Nav).Single(g => g.Name == "Übersicht");
        Assert.Equal(
            new[] { "", "health", "cves", "logs", "graph", "diff", "notifications", "audit-log" },
            overview.Items.Select(i => i.Href).ToArray());
    }

    [Fact]
    public void TopLevel_holds_the_ungrouped_entries_in_order()
    {
        Assert.Equal(new[] { "settings", "help" }, NavLayout.TopLevel(Nav).Select(i => i.Href).ToArray());
    }

    [Fact]
    public void Every_nav_item_appears_exactly_once_across_groups_and_top_level()
    {
        var fromLayout = NavLayout.Grouped(Nav).SelectMany(g => g.Items)
            .Concat(NavLayout.TopLevel(Nav))
            .ToList();
        Assert.Equal(Nav.Count, fromLayout.Count);
        Assert.Equal(
            Nav.Select(i => i.Href).OrderBy(h => h, StringComparer.Ordinal),
            fromLayout.Select(i => i.Href).OrderBy(h => h, StringComparer.Ordinal));
    }
}
