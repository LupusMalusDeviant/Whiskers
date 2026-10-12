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
/// <see cref="Whiskers.Startup.WhiskersHostingExtensions"/> therefore passes them as <c>IEnumerable&lt;Type&gt;</c>.
///
/// <para>Plan-0013 WP2 added the per-module counts below. The original surface check asserted only
/// <c>count &gt; 40</c> — a lower bound that stays green while an entire module drops out of the catalog and its
/// tools silently vanish from the served surface. Since that is exactly the failure class this file exists for,
/// the expectation is now pinned per module.</para></summary>
public class McpToolRegistrationTests
{
    /// <summary>Tools per module, pinned. Changing a module's tool set is a deliberate act: update this map in
    /// the same commit. A module that disappears from <see cref="ModuleCatalog"/>, or a new tool-bearing module
    /// that nobody listed here, fails the build instead of quietly changing what the server serves.</summary>
    private static readonly Dictionary<string, int> ExpectedToolsPerModule = new(StringComparer.Ordinal)
    {
        ["all-in-one"]    = 47,   // ContainerTools 12, ServerTools 14, MonitoringTools 4, NetworkTools 5, DatabaseTools 6, LoopSuspensionTools 3, SelfStatusTools 1, HostLoadTools 1, ActionOutcomeTools 1
        ["scheduler"]     = 4,
        ["logmonitor"]    = 4,    // LogTools 3, LogHygieneTools 1 (Plan-0007 WP-MCP)
        ["cve"]           = 4,
        ["cloud-control"] = 15,   // CloudTools 8, HetznerTools 7
        ["agent"]         = 1,
        ["gitdeploy"]     = 1,    // Plan-0013 WP4 — read-only
        ["volumebackups"] = 2,    // Plan-0013 WP4 — read-only
        ["notifications"] = 1,    // Plan-0013 WP4 — read-only
        ["image-updates"] = 2,    // GAP-6 assess_update_risk + GAP-7 get_blast_radius, both read-only
        ["dns"]           = 3,    // list_dns_records, set_dns_record, delete_dns_record (Infomaniak)
    };

    private static int RegisteredToolCount(Action<IMcpServerBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services.AddMcpServer());
        return services.BuildServiceProvider().GetServices<McpServerTool>().Count();
    }

    private static IReadOnlyList<IWhiskersModule> EnabledModules() =>
        ModuleCatalog.DiscoverEnabled(new ConfigurationBuilder().Build());

    // Mirrors WhiskersHostingExtensions.AddWhiskersModules: the default (all-on) module set feeds WithTools.
    private static IEnumerable<Type> DefaultModuleToolTypes() =>
        EnabledModules().SelectMany(m => m.McpToolTypes).ToArray();

    [Fact]
    public void Default_module_pipeline_registers_the_full_tool_surface()
    {
        IEnumerable<Type> toolTypes = DefaultModuleToolTypes(); // static type IEnumerable<Type> → intended overload
        var count = RegisteredToolCount(b => b.WithTools(toolTypes));
        Assert.Equal(ExpectedToolsPerModule.Values.Sum(), count);
    }

    [Fact]
    public void Passing_tool_types_as_array_is_the_trap_and_registers_nothing()
    {
        Type[] asArray = DefaultModuleToolTypes().ToArray();   // static type Type[] → binds to WithTools<T>(T target)
        Assert.Equal(0, RegisteredToolCount(b => b.WithTools(asArray)));
    }

    [Fact]
    public void Every_module_registers_exactly_the_tools_it_is_expected_to()
    {
        var actual = EnabledModules()
            .Where(m => m.McpToolTypes.Count > 0)
            .ToDictionary(
                m => m.Id,
                m => RegisteredToolCount(b => b.WithTools(m.McpToolTypes.AsEnumerable())),
                StringComparer.Ordinal);

        var problems = new List<string>();

        foreach (var (id, expected) in ExpectedToolsPerModule)
        {
            if (!actual.TryGetValue(id, out var got))
                problems.Add($"module '{id}' contributes no tools any more (expected {expected}) — it left the catalog, was disabled, or lost its McpToolTypes");
            else if (got != expected)
                problems.Add($"module '{id}': expected {expected} tools, registered {got}");
        }

        foreach (var id in actual.Keys.Where(k => !ExpectedToolsPerModule.ContainsKey(k)))
            problems.Add($"module '{id}' contributes {actual[id]} tools but is not pinned here — add it deliberately");

        Assert.True(problems.Count == 0, "The served tool surface changed:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void Module_tool_types_are_not_registered_twice()
    {
        // A type listed by two modules would register its tools twice and inflate every count above. The
        // all-in-one pseudo module shrinks as features are extracted — that migration is exactly when a type
        // can end up in both places.
        var all = EnabledModules().SelectMany(m => m.McpToolTypes).ToList();
        var duplicates = all.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key.Name).ToList();

        Assert.True(duplicates.Count == 0,
            "These tool types are claimed by more than one module: " + string.Join(", ", duplicates));
    }
}
