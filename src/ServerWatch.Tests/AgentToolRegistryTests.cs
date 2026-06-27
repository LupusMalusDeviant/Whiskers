using System.Linq;
using System.Text.Json;
using ServerWatch.Configuration;
using ServerWatch.Models;
using ServerWatch.Models.Agent;
using ServerWatch.Services.Agent;
using ServerWatch.Services.Agent.Guardrails;

namespace ServerWatch.Tests;

public class AgentToolRegistryTests
{
    private static readonly AgentToolRegistry Registry = new();

    [Theory]
    [InlineData("ListContainers", "list_containers")]
    [InlineData("GetContainerDetails", "get_container_details")]
    [InlineData("DeployApp", "deploy_app")]
    [InlineData("GetCveSummary", "get_cve_summary")]
    [InlineData("ExecuteCommand", "execute_command")]
    public void SnakeCase_maps_method_names(string pascal, string expected)
    {
        Assert.Equal(expected, AgentToolRegistry.ToSnakeCase(pascal));
    }

    [Fact]
    public void Registry_discovers_known_tools()
    {
        Assert.Contains("list_containers", Registry.Tools.Keys);
        Assert.Contains("stop_container", Registry.Tools.Keys);
        Assert.Contains("execute_command", Registry.Tools.Keys);
        Assert.Contains("deploy_app", Registry.Tools.Keys);
    }

    [Fact]
    public void Registry_covers_every_canonical_permission_tool()
    {
        // Jeder Eintrag in DefaultToolLevels muss eine entdeckte [McpServerTool]-Methode haben —
        // sonst stimmt das snake_case-Mapping (oder die Tool-Klasse) nicht.
        var missing = McpPermissionLevels.DefaultToolLevels.Keys
            .Where(k => !AgentToolRegistry.NonAgentTools.Contains(k))   // instruct_agent ist absichtlich raus
            .Where(k => !Registry.Tools.ContainsKey(k))
            .OrderBy(k => k)
            .ToList();
        Assert.True(missing.Count == 0, "Nicht entdeckte Tools: " + string.Join(", ", missing));
    }

    [Fact]
    public void Registry_excludes_instruct_agent_to_prevent_recursion()
    {
        Assert.DoesNotContain("instruct_agent", Registry.Tools.Keys);
        Assert.Contains("instruct_agent", McpPermissionLevels.DefaultToolLevels.Keys); // aber im Permission-Register
    }

    [Fact]
    public void Schema_excludes_di_parameters()
    {
        var schema = Registry.Tools["list_containers"].Definition.JsonSchema;
        var props = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain("docker", props);
        Assert.DoesNotContain("permissionService", props);
        Assert.DoesNotContain("httpContextAccessor", props);
        Assert.Contains("serverId", props);   // echtes Tool-Argument bleibt
    }

    [Fact]
    public void Schema_marks_mandatory_args_required_and_leaves_optionals_out()
    {
        var schema = Registry.Tools["get_container_logs"].Definition.JsonSchema;
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("containerId", required);   // kein Default
        Assert.DoesNotContain("lines", required);   // hat Default 100
        Assert.DoesNotContain("serverId", required); // string?
    }

    [Fact]
    public void Level_and_category_come_from_canonical_registry()
    {
        Assert.Equal(McpPermissionLevels.Admin, Registry.Tools["execute_command"].RequiredLevel);
        Assert.Equal(McpPermissionLevels.Read, Registry.Tools["list_containers"].RequiredLevel);
    }
}

public class AgentToolCatalogTests
{
    private static readonly AgentToolCatalog Catalog =
        new(new AgentToolRegistry(), GuardrailEngine.CreateDefault());

    private static AgentContext Context(string level, GuardrailPolicy policy) =>
        new("sess", new AgentPrincipal(AgentPrincipalKind.WebUser, "t", level, null, UserEmail: "t@x"),
            AgentOrigin.WebUi, policy);

    private static GuardrailPolicy Permissive() => new()
    {
        MaxAutonomousLevel = McpPermissionLevels.Admin,
        RequireConfirmationForWrites = false,
    };

    [Fact]
    public void Read_principal_sees_read_tools_but_not_writes()
    {
        var tools = Catalog.GetVisibleTools(Context(McpPermissionLevels.Read, Permissive()))
            .Select(t => t.Name).ToHashSet();
        Assert.Contains("list_containers", tools);
        Assert.DoesNotContain("stop_container", tools);   // Auslöser-Decke blockt write
        Assert.DoesNotContain("execute_command", tools);  // admin
    }

    [Fact]
    public void Admin_principal_sees_write_and_admin_tools()
    {
        var tools = Catalog.GetVisibleTools(Context(McpPermissionLevels.Admin, Permissive()))
            .Select(t => t.Name).ToHashSet();
        Assert.Contains("stop_container", tools);
        Assert.Contains("execute_command", tools);
    }

    [Fact]
    public void ReadOnlyMode_hides_writes_even_for_admin()
    {
        var policy = Permissive();
        policy.ReadOnlyMode = true;
        var tools = Catalog.GetVisibleTools(Context(McpPermissionLevels.Admin, policy))
            .Select(t => t.Name).ToHashSet();
        Assert.Contains("list_containers", tools);
        Assert.DoesNotContain("stop_container", tools);
    }

    [Fact]
    public void Deny_list_hides_tool()
    {
        var policy = Permissive();
        policy.ToolDenyList.Add("execute_command");
        var tools = Catalog.GetVisibleTools(Context(McpPermissionLevels.Admin, policy))
            .Select(t => t.Name).ToHashSet();
        Assert.DoesNotContain("execute_command", tools);
    }
}
