using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0021: LAN networks in a compose plan — external and pre-existing networks decided by their live driver, the
/// host's networks read again at step 4 and in the re-check after <c>up</c>, the agent's confirmation, and no
/// <c>.env</c> value in any sink.
/// </summary>
public sealed partial class ComposeImpactExposureTests
{
    private const string ExternalLanYaml =
        "name: shop\nservices:\n  web:\n    image: nginx\n    networks: [lan]\nnetworks:\n  lan:\n    external: true\n";
    private const string ExternalLanConfig =
        """{"name":"shop","networks":{"lan":{"name":"lan","external":true}},"services":{"web":{"image":"nginx","networks":{"lan":null}}}}""";

    private NetworkInfo LiveNetwork(string id, string name, string driver)
    {
        var network = new NetworkInfo { Id = id, Name = name, Driver = driver, ServerId = "local" };
        _docker.Networks.Add(network);
        return network;
    }

    private static IEnumerable<ComposeExposure> LanOf(ComposePlan plan) => plan.Exposures().Where(e => e.Kind == ExposureKind.LanNetwork);

    // ---------------------------------------------------------------- planning

    [Fact]
    public async Task An_external_macvlan_network_is_a_lan_exposure()
    {
        LiveNetwork("n-lan", "lan", "macvlan");

        var plan = await PlanAsync(ExternalLanYaml, ExternalLanConfig);

        var lan = Assert.Single(LanOf(plan));
        Assert.Equal(ComposeExposures.Identity("web", ExposureKind.LanNetwork, "lan", "macvlan"), lan.Identity);
        Assert.False(lan.Withheld);                                  // the model read the name literally
        Assert.Contains(new ComposeNetworkRef("web", "lan", External: true, Withheld: false), plan.Networks);
    }

    [Fact]
    public async Task An_external_network_that_does_not_exist_refuses_the_plan()
    {
        var ex = await Assert.ThrowsAsync<ComposePlanException>(() => PlanAsync(ExternalLanYaml, ExternalLanConfig));

        Assert.Equal("The external network 'lan' does not exist on local.", ex.Message);
    }

    [Fact]
    public async Task An_external_network_name_that_is_not_unique_refuses_the_plan()
    {
        LiveNetwork("n-lan-1", "lan", "bridge");
        LiveNetwork("n-lan-2", "lan", "overlay");

        var ex = await Assert.ThrowsAsync<ComposePlanException>(() => PlanAsync(ExternalLanYaml, ExternalLanConfig));

        Assert.Contains("is not unique", ex.Message);
    }

    [Fact]
    public async Task Unreadable_networks_refuse_the_plan()
    {
        _docker.NetworksFail = true;

        var ex = await Assert.ThrowsAsync<ComposePlanException>(() => PlanAsync(PlainYaml, PlainConfig));

        Assert.Equal("The networks of local cannot be read, so a LAN network cannot be ruled out.", ex.Message);
    }

    [Fact]
    public async Task A_pre_existing_non_external_network_is_decided_by_its_live_driver()
    {
        const string config = """{"name":"shop","networks":{"default":{"name":"shop_default"}},"services":{"web":{"image":"nginx","networks":{"default":null}}}}""";
        // Not on the host yet: compose creates it with its declared driver (none: bridge) — no exposure.
        Assert.Empty(LanOf(await PlanAsync(PlainYaml, config)));

        LiveNetwork("n-def", "shop_default", "ipvlan");
        var plan = await PlanAsync(PlainYaml, config);

        var lan = Assert.Single(LanOf(plan));
        Assert.Equal(ComposeExposures.Identity("web", ExposureKind.LanNetwork, "shop_default", "ipvlan"), lan.Identity);
    }

    // ---------------------------------------------------------------- the agent's decision

    [Fact]
    public async Task Deploy_compose_with_macvlan_ends_in_confirm_under_an_admin_autonomous_policy()
    {
        _host.ComposeConfig = """{"name":"shop","networks":{"lan":{"name":"shop_lan","driver":"macvlan"}},"services":{"web":{"image":"nginx","networks":{"lan":null}}}}""";

        var (outcome, _) = await McpAsync(new
        {
            serverId = "local", projectName = "shop",
            composeContent = "name: shop\nservices:\n  web:\n    image: nginx\n    networks: [lan]\nnetworks:\n  lan:\n    driver: macvlan\n",
        });

        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        Assert.Contains("web gets its own address on the LAN through network 'shop_lan' (driver macvlan)", outcome.Reason);
    }

    // ---------------------------------------------------------------- step 4 and the re-check

    [Fact]
    public async Task Step_4_denies_an_external_network_recreated_as_macvlan_after_planning()
    {
        var bridge = LiveNetwork("n-lan", "lan", "bridge");
        var plan = await PlanAsync(ExternalLanYaml, ExternalLanConfig);
        Assert.Empty(LanOf(plan));
        // Removed and re-created under the same name as macvlan between planning and the run.
        _docker.Networks.Remove(bridge);
        LiveNetwork("n-lan-2", "lan", "macvlan");
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Equal("'web' would join the LAN network lan (macvlan) (class 'host'), which was not decided; nothing was started.",
            result.Refusal);
        Assert.Equal(-1, IndexOf(" up -d"));
    }

    [Fact]
    public async Task The_re_check_stops_a_container_attached_to_an_undecided_lan_network_under_the_agent_rule_set()
    {
        LiveNetwork("n-lan", "lan", "macvlan");
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        // What up started is attached to the LAN network (a change on the host the plan did not see).
        _upCreates.Add(("new-web", "web",
            """{"NetworkMode":"lan"},"NetworkSettings":{"Networks":{"lan":{"NetworkID":"n-lan"}}}""", ""));
        using var _ = Decide(plan, agent: true);

        var result = await RunAsync(plan);

        Assert.False(result.Success);
        Assert.Contains("web: undecided LAN network lan (class 'host')", result.Violations);
        Assert.Equal([("disable_restart", "new-web"), ("stop", "new-web")],
            _docker.Actions.Where(a => a.ContainerId == "new-web").Select(a => (a.Op, a.ContainerId)));
        Assert.Contains(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureViolation);
    }

    [Fact]
    public async Task The_re_check_counts_unreadable_networks_as_undecided_host()
    {
        var plan = await PlanAsync(PlainYaml, PlainConfig);
        // An attached network that is not in the host's list cannot be checked.
        _upCreates.Add(("new-web", "web",
            """{"NetworkMode":"gone"},"NetworkSettings":{"Networks":{"gone":{"NetworkID":"n-gone"}}}""", ""));
        using (Decide(plan, agent: true))
        {
            var result = await RunAsync(plan);

            Assert.Contains("web: its networks could not be checked (counts as an undecided LAN network, class 'host')",
                result.Violations);
            Assert.Contains(_docker.Actions, a => a is { Op: "stop", ContainerId: "new-web" });
        }

        // A person's run whose network list cannot be read at all: reported (exposure_violation), not stopped.
        _docker.Actions.Clear();
        _upCreates.Clear();
        _upCreates.Add(("new-web-2", "web", NoHost, ""));
        _docker.NetworksFail = true;
        using (Decide(plan, agent: false))
        {
            var result = await RunAsync(plan);

            Assert.False(result.Success);
            Assert.Contains("web: its networks could not be checked (counts as an undecided LAN network, class 'host')",
                result.Violations);
            Assert.DoesNotContain(_docker.Actions, a => a.ContainerId == "new-web-2");
            Assert.Contains(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureViolation);
        }
    }

    // ---------------------------------------------------------------- sinks

    [Fact]
    public async Task A_lan_exposure_value_from_the_env_appears_in_no_sink()
    {
        const string name = "s3cr3t-net";
        const string driver = "vendor-s3cr3t/lan";
        var plan = await PlanAsync("name: shop\nservices:\n  web:\n    image: nginx\n    networks: [lan]\n" +
                                   "networks:\n  lan:\n    name: ${NET_NAME}\n    driver: ${NET_DRIVER}\n",
            """{"name":"shop","networks":{"lan":{"name":"NAME","driver":"DRIVER"}},"services":{"web":{"image":"nginx","networks":{"lan":null}}}}"""
                .Replace("NAME", name, StringComparison.Ordinal).Replace("DRIVER", driver, StringComparison.Ordinal),
            env: $"NET_NAME={name}\nNET_DRIVER={driver}\n");
        var lan = Assert.Single(LanOf(plan));
        Assert.True(lan.Withheld);

        var args = Args(ComposeImpact.ExposureArguments(plan));
        var audit = SinkRedaction.Arguments(args);
        var card = SinkRedaction.Json(System.Text.Json.JsonSerializer.Serialize(SinkRedaction.WithoutPayloads(args)));
        foreach (var sink in new[] { audit, card, args["exposure"].GetString() ?? "" })
        {
            Assert.DoesNotContain(name, sink);
            Assert.DoesNotContain("s3cr3t", sink);
        }

        // The run's notification for a person: the kind only.
        using (Decide(plan, agent: false)) await RunAsync(plan);
        Assert.Contains(_notifications.Sent, n => n.EventType == NotificationEventTypes.ExposureChange);
        Assert.All(_notifications.Sent, n => Assert.DoesNotContain("s3cr3t", n.ImageInfo));
        Assert.Contains(_notifications.Sent, n => n.ImageInfo.Contains("web: LAN network (withheld)", StringComparison.Ordinal));
    }
}
