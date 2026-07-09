using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Whiskers.Modules;

/// <summary>An opt-in feature module: it contributes its own service registrations, navigation, MCP tools
/// and startup work, and can be switched off via <c>Features:{Id}:Enabled</c> (RoadToSAP Phase 1).
///
/// Rules: modules consume Core interfaces, never the reverse; soft dependencies between modules go through a
/// Core contract with a no-op default (e.g. <c>INotificationService</c>), NOT through <see cref="DependsOn"/>.
/// The list of modules is explicit (<see cref="ModuleCatalog"/>) — no assembly scanning in Phase 1.</summary>
public interface IWhiskersModule
{
    /// <summary>Stable kebab-case id, e.g. <c>"terminal"</c>. Drives the <c>Features:{Id}:Enabled</c> flag.</summary>
    string Id { get; }

    /// <summary>Human-readable name (a German label today; an <c>IStringLocalizer</c> key once F2 lands).</summary>
    string DisplayName { get; }

    /// <summary>Whether the module is on when no <c>Features:{Id}:Enabled</c> value is configured.</summary>
    bool EnabledByDefault { get; }

    /// <summary>Ids of modules that must also be enabled. Keep this empty — prefer no-op Core contracts for
    /// soft dependencies; a non-empty list is only for hard "cannot function without" cases.</summary>
    IReadOnlyList<string> DependsOn { get; }

    /// <summary>Registers the module's services. Move registrations here <b>verbatim</b> from Program.cs —
    /// this is a relocation, not a rewrite. Not called at all when the module is disabled, so its hosted
    /// services never run.</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration config);

    /// <summary>The module's sidebar entries, merged into the nav registry.</summary>
    IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>The module's <c>[McpServerToolType]</c> tool classes; empty if it exposes none. A disabled
    /// module's tools are registered neither for external MCP callers nor for the in-process agent.</summary>
    IReadOnlyList<Type> McpToolTypes { get; }

    /// <summary>One-time async warm-up after the container is built; runs only for enabled modules.</summary>
    Task InitializeAsync(IServiceProvider sp, CancellationToken ct);
}
