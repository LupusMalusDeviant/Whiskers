using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Whiskers.Models;

namespace Whiskers.Modules.HelloWorld;

/// <summary>A deliberately minimal example module — <b>living documentation</b> for the RoadToSAP module
/// system (§6 DoD). It exercises every part of the <see cref="IWhiskersModule"/> contract in the smallest
/// possible form: an id + feature flag, one nav entry, one service registered in
/// <see cref="ConfigureServices"/>, and a page gated by <c>ModuleGuard</c>. It ships <b>disabled by default</b>
/// (<see cref="EnabledByDefault"/> = <c>false</c>), so it is inert in production unless an operator sets
/// <c>Features:hello-world:Enabled=true</c>. Copy this folder as the starting point for a new real module —
/// then move the service under <c>Services/</c>, add any <c>[McpServerToolType]</c> classes to
/// <see cref="McpToolTypes"/>, and register the whole thing in <c>ModuleCatalog</c>.</summary>
public sealed class HelloWorldModule : IWhiskersModule
{
    /// <summary>Stable kebab-case id; drives the <c>Features:hello-world:Enabled</c> flag.</summary>
    public string Id => "hello-world";

    public string DisplayName => "Hello World";

    /// <summary>Off by default — this is an example, not a real feature.</summary>
    public bool EnabledByDefault => false;

    /// <summary>Keep empty — prefer no-op Core contracts for soft dependencies (see the real modules).</summary>
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    /// <summary>One sidebar entry, merged into the nav registry and shown only while the module is enabled.</summary>
    public IReadOnlyList<NavItem> NavItems { get; } = new[]
    {
        new NavItem("hello-world", "Nav_HelloWorld", Icons.Material.Filled.WavingHand, "Übersicht", AppRole.Viewer, 5),
    };

    /// <summary>No MCP tools in this example. A real module lists its <c>[McpServerToolType]</c> classes here;
    /// a disabled module's tools then drop off both the MCP surface and the in-process agent.</summary>
    public IReadOnlyList<Type> McpToolTypes => Array.Empty<Type>();

    /// <summary>One-time async warm-up; runs (in <c>IInitializable.Order</c>) only for enabled modules.</summary>
    public Task InitializeAsync(IServiceProvider sp, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Register the module's own services here — only called when the module is enabled, so a disabled
    /// module contributes nothing to the container.</summary>
    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<IHelloWorldGreeter, HelloWorldGreeter>();
    }
}
