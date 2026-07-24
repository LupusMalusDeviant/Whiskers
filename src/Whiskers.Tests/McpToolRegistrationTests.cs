using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Whiskers.Modules;

namespace Whiskers.Tests;

/// <summary>Guards the MCP tool-registration path against the <c>WithTools</c> overload trap (a regression risk
/// introduced when b722e4d moved MCP registration onto the module list). <c>WithTools</c> exposes both a
/// <c>WithTools(IEnumerable&lt;Type&gt;)</c> overload and a generic <c>WithTools&lt;T&gt;(T target)</c> overload;
/// passing the enabled modules' tool types as a <see cref="Array"/> (<c>Type[]</c>) binds to the generic one,
/// which scans the array type itself for <c>[McpServerTool]</c> methods, finds none, and registers ZERO tools —
/// collapsing the whole MCP surface to just the "logging" capability (tools/list then answers -32601).
/// <see cref="Whiskers.Startup.WhiskersHostingExtensions"/> therefore passes them as <c>IEnumerable&lt;Type&gt;</c>.</summary>
public class McpToolRegistrationTests
{
    private static int RegisteredToolCount(Action<IMcpServerBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services.AddMcpServer());
        return services.BuildServiceProvider().GetServices<McpServerTool>().Count();
    }

    // Mirrors WhiskersHostingExtensions.AddWhiskersModules: the default (all-on) module set feeds WithTools.
    private static IEnumerable<Type> DefaultModuleToolTypes() =>
        ModuleCatalog.DiscoverEnabled(new ConfigurationBuilder().Build())
            .SelectMany(m => m.McpToolTypes)
            .ToArray();

    [Fact]
    public void Default_module_pipeline_registers_the_full_tool_surface()
    {
        IEnumerable<Type> toolTypes = DefaultModuleToolTypes(); // static type IEnumerable<Type> → intended overload
        var count = RegisteredToolCount(b => b.WithTools(toolTypes));
        Assert.True(count > 40, $"expected the full MCP tool surface, got {count} tools");
    }

    [Fact]
    public void Passing_tool_types_as_array_is_the_trap_and_registers_nothing()
    {
        Type[] asArray = DefaultModuleToolTypes().ToArray();   // static type Type[] → binds to WithTools<T>(T target)
        Assert.Equal(0, RegisteredToolCount(b => b.WithTools(asArray)));
    }
}
