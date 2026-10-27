using System.Reflection;
using Whiskers.Models;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>INV-13 groundwork: one catalog that covers every mutating MCP tool and every marked service method.</summary>
public class CapabilityCatalogTests
{
    private readonly CapabilityCatalog _catalog = new();

    [Fact]
    public void Every_write_or_admin_mcp_tool_maps_to_exactly_one_capability_with_the_same_level()
    {
        var mutatingTools = McpPermissionLevels.DefaultToolLevels
            .Where(kv => kv.Value != McpPermissionLevels.Read && kv.Key != "instruct_agent")
            .ToList();

        foreach (var (tool, level) in mutatingTools)
        {
            var capability = _catalog.FindByMcpTool(tool);
            Assert.True(capability is not null, $"MCP tool '{tool}' ({level}) has no capability in the catalog.");
            Assert.Equal(level, capability!.RequiredLevel);
        }
        Assert.Equal(mutatingTools.Count, _catalog.All.Count(c => c.McpTool is not null));
    }

    [Fact]
    public void Every_marked_service_method_names_a_catalog_capability()
    {
        var marked = typeof(Program).Assembly.GetTypes()
            .Where(t => t.IsInterface)
            .SelectMany(t => t.GetMethods())
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<MutatingOperationAttribute>()))
            .Where(x => x.Attr is not null)
            .ToList();

        Assert.NotEmpty(marked);
        foreach (var (method, attr) in marked)
            Assert.True(_catalog.Find(attr!.CapabilityId) is not null,
                $"{method.DeclaringType!.Name}.{method.Name} names unknown capability '{attr.CapabilityId}'.");
    }

    [Fact]
    public void Ids_follow_area_dot_verb_and_levels_and_deadlines_are_valid()
    {
        foreach (var c in _catalog.All)
        {
            Assert.Matches("^[a-z]+\\.[a-z_]+$", c.Id);
            Assert.Contains(c.RequiredLevel, new[] { McpPermissionLevels.Write, McpPermissionLevels.Admin });
            // A fleet-wide run (the nightly AutoPatch cycle) patches one container after another; everything else is
            // bounded at an hour.
            Assert.InRange(c.Deadline, TimeSpan.FromSeconds(1), TimeSpan.FromHours(c.TargetKind == "fleet" ? 4 : 1));
        }
        Assert.Equal(_catalog.All.Count, _catalog.All.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Unknown_ids_and_read_tools_resolve_to_nothing()
    {
        Assert.Null(_catalog.Find("container.explode"));
        Assert.Null(_catalog.FindByMcpTool("list_containers"));
    }
}
