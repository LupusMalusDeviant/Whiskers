using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Modules;
using Whiskers.Modules.Dns;
using Whiskers.Services.Dns;

namespace Whiskers.Tests;

/// <summary>The Dns module: metadata, DI registration, the feature flag, and — the trap this repo has fallen
/// into before — that every one of its tools is present in the runtime permission map with the level and
/// category it is meant to have. (The generic level/attribute drift is covered by <c>McpToolLevelTests</c>;
/// this pins the intended levels so a later "let's make delete admin" is a visible decision.)</summary>
public class DnsModuleTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void Contributes_the_dns_tools_and_no_nav_entry()
    {
        var module = new DnsModule();
        Assert.Equal("dns", module.Id);
        Assert.Empty(module.NavItems);
        Assert.Equal(new[] { typeof(DnsTools) }, module.McpToolTypes);
    }

    [Fact]
    public void ConfigureServices_registers_the_record_service_and_the_infomaniak_client()
    {
        var services = new ServiceCollection();
        new DnsModule().ConfigureServices(services, Config());
        Assert.Contains(services, d => d.ServiceType == typeof(IDnsRecordService) && d.ImplementationType == typeof(DnsRecordService));
        Assert.Contains(services, d => d.ServiceType == typeof(IDnsProviderClient));
    }

    [Fact]
    public void Record_service_resolves_from_the_container_with_the_client_injected()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new DnsModule().ConfigureServices(services, Config(("Dns:ApiToken", "x"), ("Dns:AllowedZones:0", "example.com")));
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var svc = sp.GetRequiredService<IDnsRecordService>();
        Assert.True(svc.IsConfigured);
    }

    [Fact]
    public void Enabled_by_default_and_excluded_when_the_flag_is_off()
    {
        Assert.Contains(ModuleCatalog.DiscoverEnabled(Config()), m => m.Id == "dns");
        var off = ModuleCatalog.DiscoverEnabled(Config(("Features:dns:Enabled", "false")));
        Assert.DoesNotContain(off, m => m.Id == "dns");
        Assert.Contains(off, m => m.Id == "cloud-control");
    }

    [Theory]
    [InlineData("list_dns_records", McpPermissionLevels.Read)]
    [InlineData("set_dns_record", McpPermissionLevels.Write)]
    [InlineData("delete_dns_record", McpPermissionLevels.Write)]
    public void Every_dns_tool_is_in_the_runtime_permission_map_with_its_intended_level(string tool, string level)
    {
        // An unlisted tool resolves to admin and is silently refused to the agent on every call.
        Assert.True(McpPermissionLevels.DefaultToolLevels.TryGetValue(tool, out var actual), $"{tool} is missing from DefaultToolLevels");
        Assert.Equal(level, actual);
        Assert.Equal("DNS", McpPermissionLevels.ToolCategories.GetValueOrDefault(tool));
    }

    [Fact]
    public void Settings_bind_from_configuration_including_the_zone_list()
    {
        var services = new ServiceCollection();
        new DnsModule().ConfigureServices(services, Config(
            ("Dns:Provider", "infomaniak"),
            ("Dns:ApiToken", "abc"),
            ("Dns:DefaultTtl", "600"),
            ("Dns:AllowedZones:0", "example.com"),
            ("Dns:AllowedZones:1", "example.org")));
        using var sp = services.BuildServiceProvider();

        var s = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Whiskers.Configuration.DnsSettings>>().Value;
        Assert.Equal(600, s.DefaultTtl);
        Assert.Equal(new[] { "example.com", "example.org" }, s.AllowedZones);
        Assert.True(s.IsConfigured);
    }
}
