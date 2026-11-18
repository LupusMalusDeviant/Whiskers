using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Whiskers.Mcp;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Docker;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Tests.ComponentTests;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0021: <c>network.create</c> with a LAN driver and <c>network.connect</c> to a LAN network carry a
/// <see cref="ExposureKind.LanNetwork"/> in the resolved target, so the agent's path confirms them like a published
/// compose exposure; the create is bound to the decided driver, the connect to the resolved network id.
/// </summary>
public sealed class NetworkToolsGatewayTests : IDisposable
{
    private const string Approver = "admin@example.com";

    private readonly McpFilterHarness _mcp = new();
    private readonly HttpContextAccessor _http = new();
    private readonly IMcpPermissionService _permissions;
    private readonly FakeServerConfig _servers = new(new ServerConfig { Id = "local", Name = "Local", IsDefault = true });
    private readonly FakeDocker _docker = new(new ContainerInfo { Id = "c0ffee000001", Name = "web", ServerId = "local", State = "running" })
    {
        AllowMutations = true,
    };

    public NetworkToolsGatewayTests()
    {
        var (permissions, script) = ReadBackVerificationTests.Scripted<IMcpPermissionService>.Create();
        script.On[nameof(IMcpPermissionService.IsToolAllowed)] = _ => true;
        script.On[nameof(IMcpPermissionService.ValidateKey)] = _ => new McpApiKeyConfig { Id = "k1", Name = "ops" };
        _permissions = permissions;
        _mcp.Targets = new MutationTargetResolver(new TestServices(_docker, _servers));
        NewRequest();
    }

    public void Dispose() => _mcp.Dispose();

    private void NewRequest()
    {
        _mcp.Http = new DefaultHttpContext();
        _mcp.Http.Request.Headers.Authorization = "Bearer ops-key";
        _http.HttpContext = _mcp.Http;
    }

    private NetworkInfo Network(string id, string name, string driver)
    {
        var network = new NetworkInfo { Id = id, Name = name, Driver = driver, ServerId = "local" };
        _docker.Networks.Add(network);
        return network;
    }

    private static readonly IServiceProvider NoServices = new ServiceCollection().BuildServiceProvider();

    private async Task<(string Text, McpCallOutcome Outcome)> CreateAsync(string name, string driver)
    {
        var result = await _mcp.CallAsync("create_network", async _ => Ok(await NetworkTools.CreateNetwork(_http, _permissions,
            _docker, new NullToolAuditLog(), NoServices, name, driver, "local")), new { name, driver, serverId = "local" });
        return (Text(result), McpCallOutcomes.Get(_mcp.Http).Last());
    }

    private async Task<(string Text, McpCallOutcome Outcome)> ConnectAsync(string networkId, Action? beforeTheTool = null)
    {
        var result = await _mcp.CallAsync("connect_container_to_network", async _ =>
        {
            beforeTheTool?.Invoke();
            return Ok(await NetworkTools.ConnectContainerToNetwork(_http, _permissions, _docker, NoServices, networkId, "web", "local"));
        }, new { networkId, containerId = "web", serverId = "local" });
        return (Text(result), McpCallOutcomes.Get(_mcp.Http).Last());
    }

    private static CallToolResult Ok(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    private static string Text(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private async Task<TargetResolution> ResolveAsync(string capabilityId, object arguments)
    {
        var catalog = new CapabilityCatalog();
        var capability = catalog.Find(capabilityId)!;
        var args = JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        var principal = new MutationPrincipal(MutationPrincipalKind.ApiKey, "key:k1", "ci", McpPermissionLevels.Admin);
        return await new MutationTargetResolver(new TestServices(_docker, _servers)).ResolveAsync(
            new MutationRequest(capabilityId, principal, MutationSurface.Mcp, MutationTargets.FromArguments(capability, args), args),
            capability, CancellationToken.None);
    }

    // ---------------------------------------------------------------- the resolver

    [Fact]
    public async Task Network_create_with_macvlan_carries_a_lan_exposure()
    {
        var resolution = await ResolveAsync("network.create", new { name = "lan", driver = "macvlan", serverId = "local" });

        var target = Assert.IsType<MutationTarget>(resolution.Target);
        Assert.Equal([ComposeExposures.Identity("network lan", ExposureKind.LanNetwork, "lan", "macvlan")], target.Exposures);
        Assert.Empty(target.Withheld);                       // typed by the caller, audited as it is
        Assert.Equal("local", target.ServerId);
    }

    [Fact]
    public async Task Network_create_with_bridge_carries_none()
    {
        Assert.Empty((await ResolveAsync("network.create", new { name = "lan", driver = "bridge", serverId = "local" })).Target!.Exposures);
        // No driver is the method's default, bridge.
        Assert.Empty((await ResolveAsync("network.create", new { name = "lan", serverId = "local" })).Target!.Exposures);
    }

    [Fact]
    public async Task Network_connect_to_an_ipvlan_network_carries_a_lan_exposure_for_the_container()
    {
        Network("n-lan-0001", "lan", "ipvlan");
        Network("n-back-001", "back", "bridge");

        var lan = (await ResolveAsync("network.connect", new { networkId = "lan", containerId = "web", serverId = "local" })).Target!;
        var back = (await ResolveAsync("network.connect", new { networkId = "back", containerId = "web", serverId = "local" })).Target!;
        var disconnect = (await ResolveAsync("network.disconnect", new { networkId = "lan", containerId = "web", serverId = "local" })).Target!;

        Assert.Equal([ComposeExposures.Identity("web", ExposureKind.LanNetwork, "lan", "ipvlan")], lan.Exposures);
        Assert.Equal("n-lan-0001", lan.Id);
        Assert.Empty(back.Exposures);
        Assert.Empty(disconnect.Exposures);                  // a disconnect narrows the exposure (out of scope, § 6)
    }

    // ---------------------------------------------------------------- MCP create_network

    [Fact]
    public async Task Create_network_macvlan_over_mcp_ends_in_confirm_under_an_admin_autonomous_policy()
    {
        var (text, outcome) = await CreateAsync("lan", "macvlan");

        Assert.Equal(McpCallResult.PendingApproval, outcome.Result);
        Assert.Contains("network lan gets its own address on the LAN through network 'lan' (driver macvlan)", text);
        Assert.Empty(_docker.NetworkCalls);

        // Control: a bridge network runs under the same policy; Docker gets the driver trimmed, as IsLan judged it.
        NewRequest();
        var (_, bridge) = await CreateAsync("back", " bridge ");
        Assert.Equal(McpCallResult.Executed, bridge.Result);
        Assert.Equal([("create", "back", "bridge", "local")], _docker.NetworkCalls.Select(c => (c.Op, c.Network, c.Arg, c.ServerId)));
    }

    [Fact]
    public async Task Create_network_refuses_a_driver_changed_after_the_decision()
    {
        var connections = 0;
        var manager = new UiCreatedResourceAuditTests.Stub<IDockerConnectionManager>()
            .On(nameof(IDockerConnectionManager.GetClientAsync), _ => { connections++; throw new InvalidOperationException("docker reached"); });
        var docker = new DockerService(manager.Object, _servers, new UiCreatedResourceAuditTests.Stub<Whiskers.Services.Metrics.IPrometheusMetricsSource>().Object,
            new UiCreatedResourceAuditTests.Stub<Whiskers.Services.Observability.IFleetSnapshotCache>().Object, NullLogger<DockerService>.Instance);
        var target = new MutationTarget("network", "", "lan", "local")
        {
            Exposures = [LanDrivers.CreateIdentity("lan", "macvlan")!],
        };
        var principal = new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(Approver), Approver, McpPermissionLevels.Admin);
        using var scope = MutationScope.Begin("network.create", "op-lan", principal,
            new MutationRequest("network.create", principal, MutationSurface.Ui, target, new Dictionary<string, JsonElement>()));

        // A delegate that creates another LAN driver, or another name, than the one decided: refused before Docker is asked.
        await Assert.ThrowsAsync<MutationScopeViolationException>(() => docker.CreateNetworkAsync("lan", "ipvlan", "local"));
        await Assert.ThrowsAsync<MutationScopeViolationException>(() => docker.CreateNetworkAsync("other", "macvlan", "local"));
        await Assert.ThrowsAsync<MutationScopeViolationException>(() => docker.CreateNetworkAsync("lan", "macvlan", "elsewhere"));
        Assert.Equal(0, connections);

        // The decided driver, and a driver that is not LAN, go on to Docker.
        Assert.Equal("docker reached",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => docker.CreateNetworkAsync("lan", "macvlan", "local"))).Message);
        Assert.Equal("docker reached",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => docker.CreateNetworkAsync("lan", "bridge", "local"))).Message);
        // The decided driver with spaces around it is the same driver, trimmed (as IsLan judges it).
        Assert.Equal("docker reached",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => docker.CreateNetworkAsync("lan", " macvlan ", "local"))).Message);
        Assert.Equal(3, connections);
    }

    // ---------------------------------------------------------------- MCP connect_container_to_network

    [Fact]
    public async Task Connect_uses_the_resolved_network_id_not_the_callers_name()
    {
        Network("n-back-001", "back", "bridge");

        var (_, outcome) = await ConnectAsync("back");

        Assert.Equal(McpCallResult.Executed, outcome.Result);
        Assert.Equal([("connect", "n-back-001", "c0ffee000001")], _docker.NetworkCalls.Select(c => (c.Op, c.Network, c.Arg)));
    }

    [Fact]
    public async Task A_container_recreated_under_the_same_name_is_not_connected_under_the_old_approval()
    {
        Network("n-lan-0001", "lan", "ipvlan");

        var (_, first) = await ConnectAsync("lan");
        Assert.Equal(McpCallResult.PendingApproval, first.Result);
        var pending = _mcp.Approvals.GetPending().Last();
        await _mcp.Approvals.DecideAsync(pending.Id, true, ApprovalResolver.User(Approver, AppRole.Admin));

        // The container is removed and re-created under the same name: another id, so the approval does not cover it.
        _docker.Remove("c0ffee000001");
        _docker.Add(new ContainerInfo { Id = "c0ffee000002", Name = "web", ServerId = "local", State = "running" });
        NewRequest();
        var (_, retry) = await ConnectAsync("lan");

        Assert.Equal(McpCallResult.PendingApproval, retry.Result);
        Assert.Empty(_docker.NetworkCalls);
        Assert.NotEqual(pending.Id, _mcp.Approvals.GetPending().Last().Id);
    }

    [Fact]
    public async Task A_container_recreated_after_the_decision_is_not_the_one_connected()
    {
        Network("n-back-001", "back", "bridge");

        // Re-created under the same name between the decision and the run: the tool connects the decided id only.
        var (_, outcome) = await ConnectAsync("back", beforeTheTool: () =>
        {
            _docker.Remove("c0ffee000001");
            _docker.Add(new ContainerInfo { Id = "c0ffee000002", Name = "web", ServerId = "local", State = "running" });
        });

        Assert.Equal(McpCallResult.Executed, outcome.Result);
        Assert.Equal([("connect", "c0ffee000001")], _docker.NetworkCalls.Select(c => (c.Op, c.Arg)));
    }

    [Fact]
    public async Task A_network_recreated_under_the_same_name_after_the_decision_fails_the_connect()
    {
        var old = Network("n-back-001", "back", "bridge");

        // Removed and re-created under the same name between the decision and the run: the decided id is gone.
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectAsync("back", beforeTheTool: () =>
        {
            _docker.Networks.Remove(old);
            Network("n-back-002", "back", "macvlan");
        }));

        Assert.Contains("n-back-001 not found", failed.Message);
        Assert.Equal(McpCallResult.Failed, McpCallOutcomes.Get(_mcp.Http).Last().Result);
        Assert.Equal([("connect", "n-back-001")], _docker.NetworkCalls.Select(c => (c.Op, c.Network)));
    }

    [Fact]
    public async Task An_approval_for_network_id_x_does_not_cover_a_network_recreated_under_the_same_name()
    {
        var old = Network("n-lan-0001", "lan", "ipvlan");

        var (text, first) = await ConnectAsync("lan");
        Assert.Equal(McpCallResult.PendingApproval, first.Result);
        Assert.Contains("web gets its own address on the LAN through network 'lan' (driver ipvlan)", text);
        var pending = _mcp.Approvals.GetPending().Last();
        await _mcp.Approvals.DecideAsync(pending.Id, true, ApprovalResolver.User(Approver, AppRole.Admin));

        // The network is removed and re-created under the same name and driver: only the id differs, decided again.
        _docker.Networks.Remove(old);
        Network("n-lan-0002", "lan", "ipvlan");
        NewRequest();
        var (_, retry) = await ConnectAsync("lan");

        Assert.Equal(McpCallResult.PendingApproval, retry.Result);
        Assert.Empty(_docker.NetworkCalls);
        Assert.NotEqual(pending.Id, _mcp.Approvals.GetPending().Last().Id);

        // Approved for the new id, it connects by that id.
        await _mcp.Approvals.DecideAsync(_mcp.Approvals.GetPending().Last().Id, true, ApprovalResolver.User(Approver, AppRole.Admin));
        NewRequest();
        var (_, approved) = await ConnectAsync("lan");
        Assert.Equal(McpCallResult.Executed, approved.Result);
        Assert.Equal([("connect", "n-lan-0002")], _docker.NetworkCalls.Select(c => (c.Op, c.Network)));
    }

    private sealed class NullToolAuditLog : Whiskers.Services.AuditLog.IToolAuditLog
    {
        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId, string targetName,
            string? details = null, string? serverId = null, bool success = true) => Task.CompletedTask;
    }
}
