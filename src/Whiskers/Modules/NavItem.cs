using Whiskers.Models;

namespace Whiskers.Modules;

/// <summary>
/// A single navigation entry contributed by a module. RoadToSAP Phase 0 scaffolding: the type exists
/// so modules can declare their nav in one place; <c>NavMenu.razor</c> is switched to render from the
/// <see cref="IModuleRegistry"/> in Phase 1.
/// </summary>
/// <param name="Href">Route the link points at (relative, e.g. <c>"servers"</c>; empty = dashboard).</param>
/// <param name="LocKey">Display text. Holds the current German label until F2 (i18n) swaps in a real
/// <c>IStringLocalizer</c> key.</param>
/// <param name="Icon">MudBlazor icon constant (e.g. <c>Icons.Material.Filled.Storage</c>).</param>
/// <param name="Group">Sidebar group heading the item sits under; empty = a top-level link.</param>
/// <param name="MinRole">Lowest role that may see the entry.</param>
/// <param name="Order">Ascending sort order within the group.</param>
public record NavItem(
    string Href,
    string LocKey,
    string Icon,
    string Group,
    AppRole MinRole,
    int Order);
