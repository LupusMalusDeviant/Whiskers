namespace Whiskers.Modules;

/// <summary>
/// Aggregates cross-cutting metadata from the enabled modules and answers "is this module on?".
/// <c>NavMenu.razor</c> renders from <see cref="NavItems"/>; <c>ModuleGuard</c> gates a disabled module's
/// pages via <see cref="IsEnabled"/> (RoadToSAP Phase 1).
/// </summary>
public interface IModuleRegistry
{
    /// <summary>Every enabled module's navigation entries, unordered here (consumers group by
    /// <see cref="NavItem.Group"/> and sort by <see cref="NavItem.Order"/>).</summary>
    IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>Every enabled module's MCP tool classes (<c>[McpServerToolType]</c>), aggregated. The MCP
    /// server and the in-process agent's <c>AgentToolRegistry</c> both derive their tool set from this, so a
    /// disabled module's tools are exposed to neither — external callers nor the agent (RoadToSAP §2.3).</summary>
    IReadOnlyList<Type> McpToolTypes { get; }

    /// <summary>True if the module with this id is enabled (i.e. part of the built module set). Lets a
    /// page/component show a clean "module disabled" notice instead of failing when the module's services
    /// aren't registered.</summary>
    bool IsEnabled(string moduleId);
}
