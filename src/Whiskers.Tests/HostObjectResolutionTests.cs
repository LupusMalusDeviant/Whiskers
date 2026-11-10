using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;
using static Whiskers.Tests.ComponentTests.UiCreatedResourceAuditTests;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0012 (D11, Q1): an nginx site, a systemd unit and a removed firewall rule are resolved on their server
/// against the host's own listing; unknown is refused before policy, and a rule list renumbered between the decision
/// and the run is refused as changed.
/// </summary>
public sealed class HostObjectResolutionTests
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    private readonly List<FirewallRule> _rules =
    [
        new() { Number = 1, Action = "ALLOW", Port = "22/tcp", Raw = "[ 1] 22/tcp ALLOW IN Anywhere" },
        new() { Number = 2, Action = "ALLOW", Port = "80/tcp", Raw = "[ 2] 80/tcp ALLOW IN Anywhere" },
    ];

    private readonly MutationTargetResolver _resolver;

    public HostObjectResolutionTests()
    {
        var services = new ServiceCollection()
            .AddSingleton<IServerConfigService>(new FakeServerConfig(new ServerConfig { Id = "web", Name = "Web", IsDefault = true }))
            .AddSingleton(new Stub<INginxService>().On(nameof(INginxService.ListSitesAsync),
                _ => Task.FromResult(new List<NginxSite> { new() { Name = "shop.conf" }, new() { Name = "default" } })).Object)
            .AddSingleton(new Stub<ISystemdService>().On(nameof(ISystemdService.ListUnitNamesAsync),
                _ => Task.FromResult<IReadOnlyList<string>>(["nginx.service", "docker.service"])).Object)
            .AddSingleton(new Stub<IFirewallService>().On(nameof(IFirewallService.GetStatusAsync),
                _ => Task.FromResult(new FirewallStatus { Active = true, Rules = [.. _rules] })).Object)
            .BuildServiceProvider();
        _resolver = new MutationTargetResolver(services);
    }

    private static MutationRequest Request(string capability, string json)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var catalog = new CapabilityCatalog().Find(capability)!;
        return new MutationRequest(capability, Admin, MutationSurface.Ui, MutationTargets.FromArguments(catalog, args), args);
    }

    private Task<TargetResolution> ResolveAsync(MutationRequest request) =>
        _resolver.ResolveAsync(request, new CapabilityCatalog().Find(request.CapabilityId)!, CancellationToken.None);

    [Theory]
    [InlineData("nginx.update", """{"serverId":"web","siteName":"shop.conf","content":"x"}""", "shop.conf")]
    [InlineData("nginx.site_enable", """{"serverId":"web","siteName":"default"}""", "default")]
    [InlineData("systemd.manage", """{"serverId":"web","serviceName":"nginx","action":"restart"}""", "nginx")]
    [InlineData("systemd.manage", """{"serverId":"web","serviceName":"docker.service","action":"stop"}""", "docker.service")]
    [InlineData("firewall.rule_remove", """{"serverId":"web","ruleNumber":2}""", "2")]
    public async Task A_known_host_object_resolves_to_its_server_and_name(string capability, string json, string sub)
    {
        var resolution = await ResolveAsync(Request(capability, json));

        var host = Assert.IsType<ResolvedHostObject>(resolution.Object);
        Assert.Equal(("web", sub), (host.ServerId, host.SubObject));
        Assert.Equal(TargetResolutionKind.Object, new CapabilityCatalog().Find(capability)!.Resolution);
    }

    [Theory]
    [InlineData("nginx.site_disable", """{"serverId":"web","siteName":"shop"}""")]
    [InlineData("systemd.manage", """{"serverId":"web","serviceName":"sshd","action":"stop"}""")]
    [InlineData("firewall.rule_remove", """{"serverId":"web","ruleNumber":7}""")]
    public async Task An_unknown_host_object_is_refused(string capability, string json)
    {
        var resolution = await ResolveAsync(Request(capability, json));

        Assert.Null(resolution.Target);
        Assert.Equal(MutationReasonCodes.TargetUnknown, resolution.Code);
    }

    [Fact]
    public async Task A_rule_list_renumbered_between_decision_and_run_is_refused_as_changed()
    {
        var request = Request("firewall.rule_remove", """{"serverId":"web","ruleNumber":2}""");
        var capability = new CapabilityCatalog().Find("firewall.rule_remove")!;
        var decided = (await ResolveAsync(request)).Object!;
        _rules.RemoveAt(0);
        _rules[0].Number = 1;
        _rules.Add(new FirewallRule { Number = 2, Port = "443/tcp", Raw = "[ 2] 443/tcp ALLOW IN Anywhere" });

        var confirmed = await _resolver.ConfirmAsync(request, capability, decided, CancellationToken.None);

        Assert.Null(confirmed.Target);
        Assert.Equal(MutationReasonCodes.TargetChanged, confirmed.Code);
    }

    [Fact]
    public async Task Host_wide_operations_keep_the_server_as_their_object()
    {
        var resolution = await ResolveAsync(Request("nginx.reload", """{"serverId":"web"}"""));

        var host = Assert.IsType<ResolvedHostObject>(resolution.Object);
        Assert.Null(host.SubObject);
        Assert.Equal(TargetResolutionKind.Server, new CapabilityCatalog().Find("nginx.reload")!.Resolution);
    }
}
