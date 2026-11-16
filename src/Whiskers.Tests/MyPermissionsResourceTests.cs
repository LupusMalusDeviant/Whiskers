using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Mcp.Resources;
using Whiskers.Models;
using Whiskers.Modules;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Xunit;

namespace Whiskers.Tests;

/// <summary><c>whiskers://me/permissions</c> lists only tools a call of the caller would pass: the key's tool list,
/// the tool switch and the level a call is held to (capability level for mutations, raised by a stored level) — a tool
/// list never widens the key's level (INV-16).</summary>
public sealed class MyPermissionsResourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mypermissions-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private async Task<(McpPermissionService Permissions, string Key)> KeyAsync(string level, params string[]? allowedTools)
    {
        Directory.CreateDirectory(_dir);
        var permissions = new McpPermissionService(NullLogger<McpPermissionService>.Instance, new DataPathOptions(_dir));
        await permissions.InitializeAsync();
        var created = await permissions.AddApiKeyAsync("lister", level);
        if (allowedTools is { Length: > 0 })
        {
            var data = permissions.GetPermissionData();
            data.ApiKeys.Single().AllowedTools = allowedTools.ToList();
            await permissions.SavePermissionDataAsync(data);
        }
        return (permissions, created.Key!);
    }

    private static string Read(IMcpPermissionService permissions, string? bearer)
    {
        var ctx = new DefaultHttpContext();
        if (bearer is not null)
            ctx.Request.Headers.Authorization = "Bearer " + bearer;
        var modules = new ModuleRegistry(Array.Empty<NavItem>(), AgentToolTestHelpers.DefaultModuleToolTypes(), Array.Empty<string>());
        return WhiskersResources.MyPermissions(new HttpContextAccessor { HttpContext = ctx }, modules, permissions,
            new CapabilityCatalog(), new McpToolCapabilityOverrides(permissions));
    }

    [Fact]
    public async Task A_tool_list_does_not_list_a_tool_above_the_keys_level()
    {
        var (permissions, key) = await KeyAsync(McpPermissionLevels.Read, "list_containers", "execute_command", "restart_container");
        // The key's own check passes these three; the call path still holds each to its level (McpGuardrailFilter).
        Assert.True(permissions.IsToolAllowed(key, "execute_command"));

        var mine = Read(permissions, key);

        Assert.Contains("`list_containers`", mine);
        Assert.DoesNotContain("`execute_command`", mine);
        Assert.DoesNotContain("`restart_container`", mine);
        Assert.Contains("Tools I may call (1)", mine);
        Assert.Contains("Scope: read, limited to an explicit tool list", mine);
    }

    [Fact]
    public async Task A_switched_off_or_raised_tool_is_not_listed()
    {
        var (permissions, key) = await KeyAsync(McpPermissionLevels.Write);
        Assert.Contains("`restart_container`", Read(permissions, key));
        Assert.Contains("`list_containers`", Read(permissions, key));

        var data = permissions.GetPermissionData();
        data.Tools["restart_container"].RequiredLevel = McpPermissionLevels.Admin;
        data.Tools["list_containers"].Enabled = false;
        await permissions.SavePermissionDataAsync(data);

        var mine = Read(permissions, key);
        Assert.DoesNotContain("`restart_container`", mine);
        Assert.DoesNotContain("`list_containers`", mine);
    }

    [Fact]
    public void Every_listed_tool_passes_the_level_a_call_is_held_to()
    {
        var catalog = new CapabilityCatalog();
        var overrides = new FixedOverrides();
        foreach (var capability in catalog.All.Where(c => c.McpTool is not null))
        {
            Assert.Equal(McpPermissionLevels.HasAccess(McpPermissionLevels.Read, capability.RequiredLevel),
                WhiskersResources.Callable(capability.McpTool!, McpPermissionLevels.Read, catalog, overrides));
        }
        Assert.False(WhiskersResources.Callable("list_containers", McpPermissionLevels.None, catalog, overrides));
    }

    private sealed class FixedOverrides : ICapabilityOverrides
    {
        public CapabilityAvailability Resolve(Capability capability) => new(true, capability.RequiredLevel);
        public CapabilityAvailability ResolveTool(string toolName, string codeLevel) => new(true, codeLevel);
    }
}
