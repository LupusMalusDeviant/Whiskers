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
        Assert.Contains("RequireAsync(\"scheduler.delete\"", page);
        Assert.Contains("RequireRoleAsync(RoleOf(capabilityId), capabilityId, arguments)", page);
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

    /// <summary>The one handler that still states its own role for a capability, held here by name until its role is
    /// decided: the webhook test fires the webhook's real action and requires Operator, while the catalog requires
    /// Admin for <c>compose.deploy</c>, <c>gitdeploy.deploy</c> and <c>webhook.save</c> (the gateway decides those at
    /// Admin in any case). A new entry here is a level decision, not a test fix.</summary>
    private static readonly string[] UndecidedPageRoles = ["WebhooksView.razor:TestWebhook"];

    /// <summary>
    /// No page or panel states its own role in front of a capability (INV-13): a role check in a handler that runs,
    /// refuses or guards a mutation (<c>Mutations.*</c>, <c>RefuseAsync</c>, <c>RequireAsync</c>) names no
    /// <c>AppRole.*</c> and no <c>_isAdmin</c> — it asks the catalog (<c>RoleOf(capabilityId)</c>), also where the
    /// capability id is computed (Dashboard rows, cloud actions). A literal role is left only in front of a read
    /// (a plain <c>Require</c> in a handler that changes nothing) and in markup, which only decides what is shown.
    /// </summary>
    [Fact]
    public void No_page_states_its_own_role_in_front_of_a_capability()
    {
        var components = Path.Combine(SourceRoot(), "src", "Whiskers", "Components");
        var files = Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(components, "*.cs", SearchOption.AllDirectories))
            .ToList();
        var found = files.SelectMany(f => PageRoleLiterals(Path.GetFileName(f), File.ReadAllText(f))).Distinct().ToList();

        Assert.Empty(found.Except(UndecidedPageRoles));
        // A stale exception is caught as well: once its role is decided, the entry goes.
        Assert.Equal(UndecidedPageRoles.Order(), found.Intersect(UndecidedPageRoles).Order());

        // The scan sees the handlers it is about: dozens run a capability, and they ask the catalog.
        var guarded = files.Sum(f => Methods(File.ReadAllText(f)).Count(m => RunsCapability.IsMatch(m.Body)));
        Assert.True(guarded > 100, $"only {guarded} handlers run a capability: the method scan is broken");
        var asking = files.Sum(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"CapabilityCatalog\.RoleOf\(|\bRoleOf\(capabilityId\)").Count);
        Assert.True(asking > 20, $"only {asking} role checks ask the catalog");
    }

    /// <summary>Every page's role-check helper takes a capability id and no role, and asks the catalog for the role.
    /// Only the two pages whose handler needs a role the catalog cannot state statically keep a role-taking core
    /// (<c>RequireRoleAsync</c>): Scheduled tasks for "Run now" (the task's own requirement, from
    /// <c>SchedulerRequirementResolver</c>) and Webhooks for the undecided test above.</summary>
    [Fact]
    public void Every_page_role_check_helper_asks_the_catalog()
    {
        var components = Path.Combine(SourceRoot(), "src", "Whiskers", "Components");
        var helpers = 0;
        foreach (var file in Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var source = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source,
                         @"Task<bool>\s+(Require\w*Async)\(([^)]*)\)"))
            {
                helpers++;
                if (m.Groups[1].Value == "RequireRoleAsync")
                {
                    Assert.Contains(name, new[] { "ScheduledTasksView.razor", "WebhooksView.razor" });
                    continue;
                }
                Assert.Equal("string capabilityId, object arguments", m.Groups[2].Value);
                var body = source[m.Index..(source.IndexOf("\n    }", m.Index, StringComparison.Ordinal) is var end and > 0 ? end : source.Length)];
                Assert.Matches(@"RoleOf\(capabilityId\)", body);
            }
        }
        Assert.True(helpers >= 20, $"only {helpers} role-check helpers found");
    }

    /// <summary>The scan above catches what it must: a literal role in a capability check, also with a computed id,
    /// an <c>_isAdmin</c> gate in front of a refusal, a plain <c>Require</c> in a handler that runs a mutation — and
    /// leaves a catalog role, a read and the markup alone.</summary>
    [Fact]
    public void The_page_role_scan_catches_a_literal_role_and_leaves_the_catalog_role()
    {
        static string Page(string body) => "@if (_role >= AppRole.Admin) { <b>shown</b> }\n@code {\n    private async Task Handler()\n    {\n" + body + "\n    }\n}\n";

        Assert.Equal(["P.razor:Handler"], PageRoleLiterals("P.razor", Page("        if (!await RequireAsync(AppRole.Admin, \"container.remove\", new { })) return;")));
        Assert.Equal(["P.razor:Handler"], PageRoleLiterals("P.razor", Page("        var (capability, arguments) = RowAction(c, \"start\");\n        if (!await RequireAsync(AppRole.Operator, capability, arguments)) return;")));
        Assert.Equal(["P.razor:Handler"], PageRoleLiterals("P.razor", Page("        if (!_isAdmin)\n        {\n            await Mutations.RefuseAsync(\"config.agent\", new { }, \"no\");\n            return;\n        }")));
        Assert.Equal(["P.razor:Handler"], PageRoleLiterals("P.razor", Page("        if (!Require(AppRole.Operator)) return;\n        await Mutations.RunAsync(\"container.start\", new { }, _ => Task.CompletedTask);")));
        Assert.Equal(["P.razor:Handler"], PageRoleLiterals("P.razor", Page("        if (!await CurrentUser.HasRoleAsync(Whiskers.Models.AppRole.Admin)) { await Mutations.RefuseAsync(\"x.y\", new { }, \"no\"); return; }")));
        Assert.Equal(["P.razor:Expression"], PageRoleLiterals("P.razor", "@code {\n    private Task<bool> Expression() => RequireAsync(AppRole.Admin, \"x.y\", new { });\n}\n"));

        Assert.Empty(PageRoleLiterals("P.razor", Page("        if (!await RequireAsync(\"container.remove\", new { })) return;")));
        Assert.Empty(PageRoleLiterals("P.razor", Page("        if (_role < CapabilityCatalog.RoleOf(\"terminal.host_open\")) return;\n        await Mutations.RunAsync(\"terminal.host_open\", new { }, _ => Task.CompletedTask);")));
        Assert.Empty(PageRoleLiterals("P.razor", Page("        if (!Require(AppRole.Admin)) return;\n        var result = await NginxService.TestConfigAsync(ServerId);")));
    }

    private static readonly System.Text.RegularExpressions.Regex RunsCapability = new(
        @"(?<![\w.])Mutations\.\w+|\bRefuseAsync\(|\bRequire\w*Async\(");   // the injected runner, not the namespace
    private static readonly System.Text.RegularExpressions.Regex RoleLiteral = new(
        @"\bAppRole\.(?:Admin|Operator|Viewer)\b|\b_?isAdmin\b");

    /// <summary>"File:Method" for every handler of <paramref name="source"/> that states a role of its own in front of
    /// a capability: a method that runs, refuses or guards a mutation and names a role literal or <c>_isAdmin</c>,
    /// or any capability-check call (<c>Require…Async</c>) whose role argument is a literal, wherever it sits.</summary>
    private static List<string> PageRoleLiterals(string file, string source)
    {
        var found = new List<string>();
        foreach (var (method, body) in Methods(source))
            if (RunsCapability.IsMatch(body) && RoleLiteral.IsMatch(body))
                found.Add($"{file}:{method}");
        foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(source,
                     @"\bRequire\w*Async\(\s*(?:Whiskers\.Models\.)?AppRole\."))
        {
            var owner = System.Text.RegularExpressions.Regex.Matches(source[..call.Index],
                    @"(?:private|protected|public|internal)[^\n;=(]*?\b(\w+)\s*\(")
                .LastOrDefault()?.Groups[1].Value ?? "?";
            found.Add($"{file}:{owner}");
        }
        return found.Distinct().ToList();
    }

    /// <summary>The fixed rule (Viewer↔read, Operator↔write, Admin↔admin) is spelled out in several places that turn
    /// a role into a level or back; every spelling in the product says the same, so no surface maps differently.</summary>
    [Fact]
    public void Every_role_to_level_mapping_in_the_product_is_the_fixed_rule()
    {
        var expected = new Dictionary<string, string> { ["Admin"] = "Admin", ["Operator"] = "Write", ["Viewer"] = "Read" };
        var product = Path.Combine(SourceRoot(), "src", "Whiskers");
        var arms = 0;
        foreach (var file in Directory.EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(product, "*.razor", SearchOption.AllDirectories))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source,
                         @"AppRole\.(\w+)\s*=>\s*McpPermissionLevels\.(\w+)"))
            {
                arms++;
                Assert.True(expected.GetValueOrDefault(m.Groups[1].Value) == m.Groups[2].Value, $"{Path.GetFileName(file)}: {m.Value}");
            }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source,
                         @"McpPermissionLevels\.(\w+)\s*=>\s*(?:Whiskers\.Models\.)?AppRole\.(\w+)"))
            {
                arms++;
                Assert.True(expected.GetValueOrDefault(m.Groups[2].Value) == m.Groups[1].Value, $"{Path.GetFileName(file)}: {m.Value}");
            }
        }
        Assert.True(arms >= 16, $"only {arms} mapping arms found: the scan is broken");
    }

    /// <summary>The C# methods of a component (name and body), found by brace matching.</summary>
    private static IEnumerable<(string Name, string Body)> Methods(string source)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(source,
                     @"\n\s+(?:private|protected|public|internal)[^\n;=]*?\b(\w+)\s*\([^)]*\)\s*\n\s*\{"))
        {
            var depth = 1;
            var i = m.Index + m.Length;
            while (depth > 0 && i < source.Length)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}') depth--;
                i++;
            }
            yield return (m.Groups[1].Value, source[(m.Index + m.Length)..i]);
        }
    }

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
