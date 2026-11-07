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
        // R1-49: the own password change is the one read-level capability (own account only, UI only).
        if (id == "account.password_change")
        {
            Assert.Equal(McpPermissionLevels.Read, capability.RequiredLevel);
            Assert.Null(capability.McpTool);
            return;
        }
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
        Assert.True(McpPermissionLevels.HasAccess(CapabilityCatalog.LevelFor(role), level));
        if (role > AppRole.Viewer)
            Assert.False(McpPermissionLevels.HasAccess(CapabilityCatalog.LevelFor(role - 1), level));
    }

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
        Assert.Contains("RequireAsync(\"scheduler.delete\"", page);
        Assert.Contains("RequireRoleAsync(CapabilityCatalog.RoleOf(capabilityId), capabilityId, arguments)", page);
    }

    // ── INV-13: a page takes the role in front of a capability from the catalog, never from its own source ──────

    /// <summary>The role a page requires for a capability is the catalog's: <see cref="CapabilityCatalog.RoleOf"/>
    /// is <see cref="CapabilityCatalog.RoleFor"/> of the table's level for every id, and an id the table does not know
    /// is held to Admin (fail closed).</summary>
    [Theory]
    [MemberData(nameof(Capabilities))]
    public void The_page_role_of_a_capability_is_the_catalog_role(string id) =>
        Assert.Equal(CapabilityCatalog.RoleFor(Catalog.Find(id)!.RequiredLevel), CapabilityCatalog.RoleOf(id));

    [Theory]
    [InlineData("container.no_such_verb")]
    [InlineData("cloud.unknown_action")]   // CloudView builds "cloud.{action}" for an action it does not map
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_capability_needs_admin_on_a_page(string? id) =>
        Assert.Equal(AppRole.Admin, CapabilityCatalog.RoleOf(id!));

    /// <summary>The fixed rule, both ways (INV-13): <see cref="CapabilityCatalog.LevelFor"/> is the one role → level
    /// mapping (anything but Admin and Operator holds read), and <see cref="CapabilityCatalog.RoleFor"/> maps a
    /// required level to a role — a level that is not read, write or admin is held to Admin, never to Viewer.</summary>
    [Theory]
    [InlineData(AppRole.Admin, McpPermissionLevels.Admin)]
    [InlineData(AppRole.Operator, McpPermissionLevels.Write)]
    [InlineData(AppRole.Viewer, McpPermissionLevels.Read)]
    [InlineData((AppRole)99, McpPermissionLevels.Read)]
    [InlineData((AppRole)(-1), McpPermissionLevels.Read)]
    public void A_role_holds_the_level_of_the_fixed_rule(AppRole role, string level) =>
        Assert.Equal(level, CapabilityCatalog.LevelFor(role));

    [Theory]
    [InlineData("admin", AppRole.Admin)]
    [InlineData(" Write ", AppRole.Operator)]
    [InlineData("READ", AppRole.Viewer)]
    [InlineData("wirte", AppRole.Admin)]
    [InlineData("", AppRole.Admin)]
    [InlineData(null, AppRole.Admin)]
    public void A_required_level_maps_to_its_role_and_anything_else_to_admin(string? level, AppRole role) =>
        Assert.Equal(role, CapabilityCatalog.RoleFor(level!));

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    [InlineData(AppRole.Admin)]
    public void The_two_directions_of_the_rule_agree(AppRole role) =>
        Assert.Equal(role, CapabilityCatalog.RoleFor(CapabilityCatalog.LevelFor(role)));

    /// <summary>The same operation offered under different capability ids on different surfaces needs the same
    /// level everywhere (R1-05): one table, no cheaper door.</summary>
    [Theory]
    [InlineData("container.env_set", "container.env_file_save")]      // MCP .env write + compose up / page .env save
    [InlineData("host.exec", "container.exec")]                       // a command as root / inside a container
    [InlineData("app.deploy", "compose.deploy")]
    [InlineData("compose.deploy", "gitdeploy.deploy")]
    [InlineData("ssl.renew", "certificate.renew_mtls")]               // certificate renewals on the host
    [InlineData("selfbackup.create", "selfbackup.delete")]
    [InlineData("logalert.create", "logalert.delete")]
    [InlineData("checks.pause", "checks.resume")]
    public void Equivalent_operations_need_the_same_level(string a, string b) =>
        Assert.Equal(Catalog.Find(a)!.RequiredLevel, Catalog.Find(b)!.RequiredLevel);

    /// <summary>Anything root-equivalent on a host is Admin. The scheduler's create / run are the exception that
    /// proves the rule: their level depends on the task, and the gateway resolves it per call (custom commands,
    /// DB dumps, retention and self-backups are held to Admin).</summary>
    [Theory]
    [MemberData(nameof(Capabilities))]
    public void A_root_equivalent_capability_needs_admin(string id)
    {
        var capability = Catalog.Find(id)!;
        if (capability.Risk != CapabilityRisk.RootEquivalent || id is "scheduler.create" or "scheduler.run") return;
        Assert.Equal(McpPermissionLevels.Admin, capability.RequiredLevel);
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }
}
