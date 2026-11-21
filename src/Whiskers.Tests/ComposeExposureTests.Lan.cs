using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0021: a service that gets its own address on the host's LAN — a macvlan / ipvlan network, or a driver
/// Whiskers does not know — is a <see cref="ExposureKind.LanNetwork"/> (class <c>host</c>, key the network's name on the
/// host, value the driver), read from the files, from <c>docker compose config</c> and from a container's networks.
/// </summary>
public sealed partial class ComposeExposureTests
{
    private static IReadOnlyList<ComposeExposure> Lan(string yaml, Dictionary<string, string?>? env = null, string service = "web") =>
        ComposeModel.ExposuresOf([("/srv/app/compose.yaml", yaml)], env ?? new Dictionary<string, string?>(), Prefixes, "shop")
            .Exposures.GetValueOrDefault(service)?.Where(e => e.Kind == ExposureKind.LanNetwork).ToList() ?? [];

    [Fact]
    public void A_macvlan_network_in_the_file_is_a_lan_exposure_of_every_service_on_it()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n    networks: [lan]\n  db:\n    image: redis\n    networks:\n      lan: {}\n" +
                            "  cache:\n    image: redis\n    networks: [back]\n" +
                            "networks:\n  lan:\n    driver: macvlan\n  back: {}\n";

        foreach (var service in (string[])["web", "db"])
        {
            var lan = Assert.Single(Lan(yaml, service: service));
            Assert.Equal("shop_lan", lan.Key);           // the network's real name: <project>_<key>
            Assert.Equal("macvlan", lan.Value);
            Assert.False(lan.Withheld);
            Assert.Equal(ExposureClasses.Host, ExposureClasses.Of(lan.Kind, lan.Key, Prefixes));
            Assert.Equal($"{service}: LAN network shop_lan (macvlan)", ComposeExposures.Describe(lan.Identity, withheld: false));
        }
        Assert.Empty(Lan(yaml, service: "cache"));
    }

    [Fact]
    public void An_ipvlan_default_network_reaches_services_without_a_networks_key()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n  host:\n    image: nginx\n    network_mode: host\n" +
                            "networks:\n  default:\n    name: lan-default\n    driver: ipvlan\n";

        var lan = Assert.Single(Lan(yaml));
        Assert.Equal(("lan-default", "ipvlan"), (lan.Key, lan.Value));
        // network_mode takes a service out of the project's networks.
        Assert.Empty(Lan(yaml, service: "host"));
    }

    [Fact]
    public void A_driver_from_an_unset_variable_refuses_the_plan()
    {
        var ex = Assert.Throws<ComposePlanException>(() =>
            Lan("services:\n  web:\n    image: nginx\n    networks: [lan]\nnetworks:\n  lan:\n    driver: ${NET_DRIVER}\n"));

        Assert.Contains("driver of network 'lan'", ex.Message);
    }

    [Fact]
    public void A_driver_from_a_variable_is_withheld()
    {
        var lan = Assert.Single(Lan("services:\n  web:\n    image: nginx\n    networks: [lan]\nnetworks:\n  lan:\n    driver: ${NET_DRIVER}\n",
            new() { ["NET_DRIVER"] = "macvlan" }));

        Assert.True(lan.KeyWithheld);
        Assert.True(lan.ValueWithheld);
        Assert.Equal("macvlan", lan.Value);               // compared and bound with its real value
        Assert.DoesNotContain("macvlan", ComposeExposures.Shown(lan));
    }

    [Theory]
    [InlineData("bridge")]
    [InlineData("overlay")]
    public void A_bridge_or_overlay_network_is_no_exposure(string driver) =>
        Assert.Empty(Lan($"services:\n  web:\n    image: nginx\n    networks: [n]\nnetworks:\n  n:\n    driver: {driver}\n"));

    [Theory]
    [InlineData("vendor/net:latest")]
    [InlineData("Macvlan")]
    [InlineData("weave")]
    [InlineData("transparent")]
    public void An_unknown_or_plugin_qualified_driver_counts_as_lan(string driver)
    {
        var lan = Assert.Single(Lan($"services:\n  web:\n    image: nginx\n    networks: [n]\nnetworks:\n  n:\n    driver: \"{driver}\"\n"));

        Assert.Equal(driver, lan.Value);
        Assert.True(LanDrivers.IsLan(driver));
    }

    [Fact]
    public void A_config_only_network_driver_null_is_no_exposure()
    {
        Assert.False(LanDrivers.IsLan("null"));
        var container = JsonDocument.Parse("""{"NetworkSettings":{"Networks":{"cfg":{"NetworkID":"n-null"}}}}""").RootElement;

        var found = ComposeExposures.LanNetworksOf("web", container, [new NetworkInfo { Id = "n-null", Name = "cfg", Driver = "null" }]);

        Assert.NotNull(found);
        Assert.Empty(found);
    }

    [Fact]
    public void Compose_config_driver_is_read_and_withheld_unless_the_model_read_it()
    {
        const string yaml = "services:\n  web:\n    image: nginx\n    networks: [lan]\nnetworks:\n  lan:\n    driver: macvlan\n";
        const string config = """
            {"name":"shop","networks":{"lan":{"name":"shop_lan","driver":"macvlan"},"default":{"name":"shop_default"}},
             "services":{"web":{"image":"nginx","networks":{"lan":null}},"api":{"image":"nginx","networks":{"default":null,"lan":null}}}}
            """;

        var active = ComposeConfigEffects.ActiveServices(config, "shop", Prefixes);
        var fromConfig = Assert.Single(active.Single(a => a.Name == "api").Exposures);
        Assert.Equal(ExposureKind.LanNetwork, fromConfig.Kind);
        Assert.Equal(("shop_lan", "macvlan"), (fromConfig.Key, fromConfig.Value));
        Assert.True(fromConfig.Withheld);                  // only compose read it

        // The model read the same identity for web: the plan keeps the model's (shown) entry, as the planner unites them.
        var model = Assert.Single(Lan(yaml));
        var compose = Assert.Single(active.Single(a => a.Name == "web").Exposures);
        Assert.Equal(model.Identity, compose.Identity);
        Assert.False(model.Withheld);
    }

    [Fact]
    public void Network_mode_naming_a_network_is_decided_by_its_live_driver()
    {
        var refs = ComposeModel.PlanModelOf([("/srv/app/compose.yaml", "services:\n  web:\n    image: nginx\n    network_mode: lan\n")],
            new Dictionary<string, string?>(), Prefixes, "shop").Networks;

        var named = Assert.Single(refs);
        Assert.Equal(new ComposeNetworkRef("web", "lan", External: true, Withheld: false), named);
        var lan = Assert.Single(ComposeLiveNetworks.Decide(refs, [new NetworkInfo { Id = "n1", Name = "lan", Driver = "macvlan" }], "srv1"));
        Assert.Equal(ComposeExposures.Identity("web", ExposureKind.LanNetwork, "lan", "macvlan"), lan.Identity);
        Assert.Empty(ComposeLiveNetworks.Decide(refs, [new NetworkInfo { Id = "n1", Name = "lan", Driver = "bridge" }], "srv1"));
        Assert.Throws<ComposePlanException>(() => ComposeLiveNetworks.Decide(refs, [], "srv1"));
    }

    [Fact]
    public void A_service_sharing_the_network_namespace_of_a_lan_service_inherits_it()
    {
        var lan = Assert.Single(Lan("services:\n  web:\n    image: nginx\n    networks: [lan]\nnetworks:\n  lan:\n    driver: macvlan\n"));

        // Reachable at the same address: not an own-only exposure, so a service:web / container:x sharer inherits it.
        Assert.False(ComposeExposures.IsOwnOnly(lan));
    }

    [Fact]
    public void A_container_x_attached_to_a_lan_network_passes_it_on()
    {
        var container = JsonDocument.Parse("""
            {"Config":{"Labels":{}},"HostConfig":{"NetworkMode":"lan"},
             "NetworkSettings":{"Networks":{"lan":{"NetworkID":"n-lan"},"bridge":{"NetworkID":"n-br"}}}}
            """).RootElement;
        NetworkInfo[] networks = [new() { Id = "n-lan", Name = "lan", Driver = "macvlan", Internal = true }, new() { Id = "n-br", Name = "bridge", Driver = "bridge" }];

        var (exposures, _) = ComposeExposures.FromInspect("shared", container, Prefixes, networks);

        var lan = Assert.Single(exposures);
        Assert.Equal(ComposeExposures.Identity("shared", ExposureKind.LanNetwork, "lan", "macvlan"), lan.Identity);
        Assert.Equal("LAN network lan (macvlan internal)", lan.Detail);   // internal is shown, never compared (D3)
        // An attached network that is not in the list cannot be read: fail closed.
        Assert.Throws<ComposePlanException>(() => ComposeExposures.FromInspect("shared", container, Prefixes, [networks[1]]));
        Assert.Throws<ComposePlanException>(() => ComposeExposures.FromInspect("shared",
            JsonDocument.Parse("""{"Config":{"Labels":{}},"HostConfig":{}}""").RootElement, Prefixes, networks));
    }

    [Theory]
    [InlineData("macvlan", true)]
    [InlineData("ipvlan", true)]
    [InlineData("bridge", false)]
    [InlineData(" bridge ", false)]                  // trimmed
    [InlineData("overlay", false)]
    [InlineData("host", false)]
    [InlineData("none", false)]
    [InlineData("null", false)]
    [InlineData("Bridge", true)]                     // not the built-in driver: resolved as a plugin name
    [InlineData("BRIDGE", true)]
    [InlineData("l2bridge", true)]
    [InlineData("", true)]
    public void LanDrivers_is_ordinal_and_exact(string driver, bool lan) =>
        Assert.Equal(lan, LanDrivers.IsLan(driver));

    [Fact]
    public void The_plan_model_reads_networks_with_the_policys_exposure_prefixes()
    {
        // One read gives exposures and networks: a label under the policy's own prefix that cannot be interpolated
        // refuses it, also where only the networks are wanted (no second read with the default prefixes).
        const string yaml = "services:\n  web:\n    image: nginx\n    labels:\n      acme.expose.port: ${UNSET_VALUE}\n    networks: [lan]\nnetworks:\n  lan:\n    driver: macvlan\n";
        var files = new List<(string, string)> { ("/srv/app/compose.yaml", yaml) };
        var env = new Dictionary<string, string?>();

        var withDefaults = ComposeModel.PlanModelOf(files, env, Prefixes, "shop");
        Assert.Equal([new ComposeNetworkRef("web", "shop_lan", External: false, Withheld: false)], withDefaults.Networks);
        Assert.Single(withDefaults.Exposures["web"], e => e.Kind == ExposureKind.LanNetwork);
        Assert.Throws<ComposePlanException>(() => ComposeModel.PlanModelOf(files, env, ["acme.expose."], "shop"));
    }

    [Fact]
    public void A_created_but_never_started_container_is_read_by_the_network_name()
    {
        // Docker leaves NetworkID empty until the container starts; the entry's key is the network's name then.
        var container = JsonDocument.Parse("""
            {"NetworkSettings":{"Networks":{"shop_lan":{"NetworkID":""},"shop_default":{"NetworkID":""}}}}
            """).RootElement;
        NetworkInfo[] networks =
        [
            new() { Id = "n-lan", Name = "shop_lan", Driver = "macvlan" },
            new() { Id = "n-def", Name = "shop_default", Driver = "bridge" },
        ];

        var found = ComposeExposures.LanNetworksOf("web", container, networks);

        var lan = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ComposeExposure>>(found));
        Assert.Equal(("shop_lan", "macvlan"), (lan.Key, lan.Value));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("twice")]
    [InlineData("case")]
    public void An_empty_network_id_whose_name_is_not_exactly_once_in_the_list_is_unchecked(string variant)
    {
        var container = JsonDocument.Parse("""{"NetworkSettings":{"Networks":{"shop_lan":{"NetworkID":""}}}}""").RootElement;
        NetworkInfo[] networks = variant switch
        {
            "missing" => [new() { Id = "n-other", Name = "other", Driver = "bridge" }],
            "twice" => [new() { Id = "n-a", Name = "shop_lan", Driver = "bridge" }, new() { Id = "n-b", Name = "shop_lan", Driver = "macvlan" }],
            _ => [new() { Id = "n-lan", Name = "Shop_Lan", Driver = "bridge" }],
        };

        Assert.Null(ComposeExposures.LanNetworksOf("web", container, networks));
    }
}
