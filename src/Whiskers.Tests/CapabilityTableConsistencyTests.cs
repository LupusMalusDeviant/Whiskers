using Whiskers.Models;
using Whiskers.Services.Mutations;
using Xunit;

namespace Whiskers.Tests;

/// <summary>R1-05: the capability catalog is the one level table; every surface must agree with it.</summary>
public class CapabilityTableConsistencyTests
{
    private static readonly CapabilityCatalog Catalog = new();

    public static IEnumerable<object[]> Capabilities() => Catalog.All.Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void Every_mutating_capability_has_one_level_on_every_surface(string id)
    {
        var capability = Catalog.Find(id)!;
        Assert.Contains(capability.RequiredLevel, new[] { McpPermissionLevels.Write, McpPermissionLevels.Admin });
        if (capability.McpTool is null) return;
        // The MCP gate, the guardrail filter and the approval card read DefaultToolLevels; the tool attribute is
        // checked against DefaultToolLevels by McpToolLevelTests.
        Assert.Equal(capability.RequiredLevel, McpPermissionLevels.DefaultToolLevels[capability.McpTool]);
        Assert.Equal(capability.RequiredLevel, CapabilityCatalog.McpToolLevels[capability.McpTool]);
        Assert.Same(capability, Catalog.FindByMcpTool(capability.McpTool));
    }

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void Root_equivalent_capabilities_are_admin(string id)
    {
        var capability = Catalog.Find(id)!;
        // A scheduled task is root-equivalent only when it runs a command: SchedulerRequirementResolver raises those
        // to Admin per task (ApprovalSeparationOfDutyTests), a container restart stays Write.
        if (id is "scheduler.create" or "scheduler.run") return;
        if (capability.Risk == CapabilityRisk.RootEquivalent)
            Assert.Equal(McpPermissionLevels.Admin, capability.RequiredLevel);
    }

    [Fact]
    public void Gitdeploy_deploy_is_admin() =>
        Assert.Equal(McpPermissionLevels.Admin, Catalog.Find("gitdeploy.deploy")!.RequiredLevel);

    /// <summary>The UI pages for these areas require Admin; the MCP tools were Write. The stricter level won.</summary>
    [Theory]
    [InlineData("firewall.")]
    [InlineData("nginx.")]
    [InlineData("systemd.")]
    [InlineData("network.")]
    [InlineData("cloud.")]
    [InlineData("hetzner.")]
    public void Host_network_and_cloud_areas_are_admin_on_every_surface(string prefix)
    {
        var area = Catalog.All.Where(c => c.Id.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(area);
        Assert.All(area, c => Assert.Equal(McpPermissionLevels.Admin, c.RequiredLevel));
    }

    [Fact]
    public void Every_mcp_tool_of_a_capability_is_a_known_tool() =>
        Assert.All(CapabilityCatalog.McpToolLevels.Keys, t => Assert.True(McpPermissionLevels.DefaultToolLevels.ContainsKey(t), t));

    /// <summary>Only reads bypass the gateway: the guardrail filter sends every tool with a capability to the gateway
    /// and only the rest to <c>DirectMcpCallGuard</c>, so a non-read tool without a capability would skip the table.</summary>
    [Fact]
    public void Every_non_read_mcp_tool_has_a_capability() =>
        Assert.All(McpPermissionLevels.DefaultToolLevels.Where(kv => kv.Value != McpPermissionLevels.Read),
            kv => Assert.NotNull(Catalog.FindByMcpTool(kv.Key)));

    /// <summary>A UI role holds exactly the catalog level it maps to, so pages that ask the catalog agree with MCP.</summary>
    [Theory]
    [MemberData(nameof(Capabilities))]
    public void The_ui_role_of_every_capability_holds_its_level_and_no_more(string id)
    {
        var level = Catalog.Find(id)!.RequiredLevel;
        var role = CapabilityCatalog.RoleFor(level);
        Assert.True(McpPermissionLevels.HasAccess(Level(role), level));
        if (role > AppRole.Viewer)
            Assert.False(McpPermissionLevels.HasAccess(Level(role - 1), level));
    }

    private static string Level(AppRole role) => role switch
    {
        AppRole.Admin => McpPermissionLevels.Admin,
        AppRole.Operator => McpPermissionLevels.Write,
        _ => McpPermissionLevels.Read,
    };

    /// <summary>Every capability a page runs through the gateway is in the catalog (UI-only ones included), and the
    /// Scheduled tasks page takes the delete level from the catalog instead of a hard-coded role.</summary>
    [Fact]
    public void Every_capability_a_page_runs_is_in_the_catalog()
    {
        var components = Path.Combine(SourceRoot(), "src", "Whiskers", "Components");
        var ids = Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"RunAsync\(\s*""([a-z_]+\.[a-z_.]+)""")
                .Select(m => (File: Path.GetFileName(f), Id: m.Groups[1].Value)))
            .ToList();
        Assert.NotEmpty(ids);
        Assert.All(ids, x => Assert.True(Catalog.Find(x.Id) is not null, $"{x.File}: {x.Id}"));
        Assert.Contains(ids, x => Catalog.Find(x.Id)!.McpTool is null);

        var page = File.ReadAllText(Path.Combine(components, "Pages", "ScheduledTasksView.razor"));
        Assert.Contains("Require(RoleOf(\"scheduler.delete\"))", page);
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }
}
