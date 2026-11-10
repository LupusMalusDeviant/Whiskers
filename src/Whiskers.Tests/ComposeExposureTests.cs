using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>Contract 0006: what the plan model and the helpers read as an exposure of a compose deployment.</summary>
public sealed class ComposeExposureTests
{
    private static readonly IReadOnlyList<string> Prefixes = GuardrailPolicy.DefaultExposureLabelPrefixes;

    private static IReadOnlyList<ComposeExposure> Of(string services, Dictionary<string, string?>? env = null,
        IReadOnlyList<string>? prefixes = null, string service = "web")
    {
        var (exposures, _) = ComposeModel.ExposuresOf([("/srv/app/compose.yaml", services)],
            env ?? new Dictionary<string, string?>(), prefixes ?? Prefixes);
        return exposures.GetValueOrDefault(service) ?? [];
    }

    private static ComposeExposure Single(string services, Dictionary<string, string?>? env = null) =>
        Assert.Single(Of(services, env));

    // ---------------------------------------------------------------- each kind, short and long forms

    [Theory]
    [InlineData("\"8080:80\"", "80/tcp", "0.0.0.0:8080")]
    [InlineData("\"0.0.0.0:8080:80/udp\"", "80/udp", "0.0.0.0:8080")]
    [InlineData("\"80\"", "80/tcp", "0.0.0.0:*")]                   // an ephemeral host port
    [InlineData("\"[2001:db8::1]:8080:80\"", "80/tcp", "2001:db8::1:8080")]
    [InlineData("{ target: 80, published: 8080 }", "80/tcp", "0.0.0.0:8080")]
    [InlineData("{ target: 53, published: \"5353\", host_ip: 10.0.0.1, protocol: udp }", "53/udp", "10.0.0.1:5353")]
    public void A_published_port_is_read_in_the_short_and_the_long_form(string entry, string key, string value)
    {
        var port = Single($"services:\n  web:\n    image: nginx\n    ports:\n      - {entry}\n");

        Assert.Equal(ExposureKind.PublishedPort, port.Kind);
        Assert.Equal(key, port.Key);
        Assert.Equal(value, port.Value);
        Assert.False(port.Withheld);
    }

    [Theory]
    [InlineData("\"127.0.0.1:8080:80\"")]
    [InlineData("\"[::1]:8080:80\"")]
    [InlineData("{ target: 80, published: 8080, host_ip: 127.0.0.1 }")]
    public void A_loopback_port_is_no_exposure(string entry) =>
        Assert.Empty(Of($"services:\n  web:\n    image: nginx\n    ports:\n      - {entry}\n"));

    [Fact]
    public void A_port_range_is_one_entry_per_port()
    {
        var ports = Of("services:\n  web:\n    image: nginx\n    ports:\n      - \"8000-8001:80-81\"\n");

        Assert.Equal(["80/tcp=0.0.0.0:8000", "81/tcp=0.0.0.0:8001"], ports.Select(p => $"{p.Key}={p.Value}"));
    }

    [Theory]
    [InlineData("network_mode: host", ExposureKind.HostNetwork, "network_mode")]
    [InlineData("privileged: true", ExposureKind.Privileged, "privileged")]
    [InlineData("cap_add: [CAP_NET_ADMIN]", ExposureKind.AddedCapability, "NET_ADMIN")]
    [InlineData("cap_add: [net_admin]", ExposureKind.AddedCapability, "NET_ADMIN")]
    [InlineData("pid: host", ExposureKind.HostNamespace, "pid")]
    [InlineData("ipc: host", ExposureKind.HostNamespace, "ipc")]
    [InlineData("uts: host", ExposureKind.HostNamespace, "uts")]
    [InlineData("userns_mode: host", ExposureKind.HostNamespace, "userns_mode")]
    [InlineData("cgroup: host", ExposureKind.HostNamespace, "cgroup")]
    [InlineData("devices: [\"/dev/ttyUSB0:/dev/ttyUSB0\"]", ExposureKind.Device, "/dev/ttyUSB0")]
    [InlineData("devices: [{ source: /dev/kvm, target: /dev/kvm, permissions: rw }]", ExposureKind.Device, "/dev/kvm")]
    [InlineData("security_opt: [\"seccomp:unconfined\"]", ExposureKind.SecurityOpt, "seccomp")]
    [InlineData("security_opt: [\"apparmor=unconfined\"]", ExposureKind.SecurityOpt, "apparmor")]
    [InlineData("security_opt: [\"label=disable\"]", ExposureKind.SecurityOpt, "label")]
    [InlineData("security_opt: [\"systempaths=unconfined\"]", ExposureKind.SecurityOpt, "systempaths")]
    [InlineData("device_cgroup_rules: [\"c 189:* rmw\"]", ExposureKind.DeviceCgroupRule, "c 189:* rmw")]
    public void Every_host_row_is_read_and_is_of_class_host(string line, ExposureKind kind, string key)
    {
        var exposure = Single($"services:\n  web:\n    image: nginx\n    {line}\n");

        Assert.Equal(kind, exposure.Kind);
        Assert.Equal(key, exposure.Key);
        Assert.Equal(ExposureClasses.Host, ExposureClasses.Of(exposure.Kind, exposure.Key, Prefixes));
    }

    [Theory]
    [InlineData("security_opt: [\"no-new-privileges:true\"]")]
    [InlineData("pid: \"\"")]
    [InlineData("network_mode: bridge")]
    [InlineData("privileged: false")]
    public void A_harmless_setting_is_no_exposure(string line) =>
        Assert.Empty(Of($"services:\n  web:\n    image: nginx\n    {line}\n"));

    [Fact]
    public void An_external_network_called_host_is_the_host_network()
    {
        var exposure = Single("services:\n  web:\n    image: nginx\n    networks: [hostnet]\n" +
                              "networks:\n  hostnet:\n    external: true\n    name: host\n");

        Assert.Equal(ExposureKind.HostNetwork, exposure.Kind);
        // Compared like network_mode: host.
        Assert.Equal(ComposeExposures.Identity("web", ExposureKind.HostNetwork, "network_mode", "host"),
            ComposeExposures.ComparisonKey(exposure.Identity));
    }

    // ---------------------------------------------------------------- labels

    [Theory]
    [InlineData("labels:\n      docktail.funnel.enable: \"true\"\n")]
    [InlineData("labels:\n      - docktail.funnel.enable=true\n")]
    public void A_funnel_label_is_read_in_map_and_list_form_and_is_public(string labels)
    {
        var label = Single($"services:\n  web:\n    image: nginx\n    {labels}");

        Assert.Equal(ExposureKind.RouteLabel, label.Kind);
        Assert.Equal("docktail.funnel.enable", label.Key);
        Assert.Equal("docktail.funnel.enable", label.Detail);   // the key only
        Assert.False(label.Withheld);                          // "true" is a shape that may be shown
        Assert.Equal(ExposureClasses.Public, ExposureClasses.Of(label.Kind, label.Key, Prefixes));
    }

    [Fact]
    public void A_label_without_an_exposure_prefix_is_no_exposure() =>
        Assert.Empty(Of("services:\n  web:\n    image: nginx\n    labels:\n      com.example.team: blue\n"));

    [Fact]
    public void Label_keys_match_case_insensitively_and_an_operator_prefix_is_public()
    {
        var upper = Single("services:\n  web:\n    image: nginx\n    labels:\n      DockTail.Funnel.Enable: \"true\"\n");
        Assert.Equal(ExposureClasses.Public, ExposureClasses.Of(upper.Kind, upper.Key, Prefixes));

        var custom = Assert.Single(Of("services:\n  web:\n    image: nginx\n    labels:\n      my.proxy.host: x\n",
            prefixes: [.. Prefixes, "my.proxy."]));
        Assert.Equal(ExposureClasses.Public, ExposureClasses.Of(custom.Kind, custom.Key, [.. Prefixes, "my.proxy."]));
    }

    [Fact]
    public void A_docktail_service_label_is_published_only()
    {
        var label = Single("services:\n  web:\n    image: nginx\n    labels:\n      docktail.service.enable: \"true\"\n");

        Assert.Equal(ExposureClasses.Published, ExposureClasses.Of(label.Kind, label.Key, Prefixes));
    }

    [Fact]
    public void An_interpolated_label_and_port_are_withheld()
    {
        var env = new Dictionary<string, string?> { ["FUNNEL"] = "true", ["PORT"] = "8443", ["KEY"] = "docktail.funnel.port" };
        var exposures = Of("services:\n  web:\n    image: nginx\n    ports:\n      - \"${PORT}:443\"\n" +
                           "    labels:\n      docktail.funnel.enable: \"${FUNNEL}\"\n      ${KEY}: \"443\"\n", env);

        var port = Assert.Single(exposures, e => e.Kind == ExposureKind.PublishedPort);
        Assert.Equal("0.0.0.0:8443", port.Value);   // compared on the real value
        Assert.True(port.Withheld);
        var funnel = Assert.Single(exposures, e => e.Key == "docktail.funnel.enable");
        Assert.True(funnel.ValueWithheld);
        Assert.False(funnel.KeyWithheld);
        var fromKey = Assert.Single(exposures, e => e.Key == "docktail.funnel.port");
        Assert.True(fromKey.KeyWithheld);
        Assert.Equal("label (withheld)", fromKey.Detail);
    }

    [Fact]
    public void A_variable_value_of_an_allowed_shape_is_withheld()
    {
        var label = Single("services:\n  web:\n    image: nginx\n    labels:\n      docktail.funnel.enable: \"${X:-true}\"\n");

        Assert.Equal("true", label.Value);
        Assert.True(label.ValueWithheld);
    }

    [Theory]
    [InlineData("labels:\n      docktail.funnel.enable: \"${UNSET}\"\n")]         // a value under a prefix
    [InlineData("labels:\n      ${UNSET_KEY}: x\n")]                              // any key
    [InlineData("ports:\n      - \"${UNSET}:80\"\n")]                              // a port
    [InlineData("network_mode: \"${UNSET}\"\n")]                                  // a network
    [InlineData("privileged: \"${UNSET}\"\n")]                                    // a privilege field
    public void A_value_that_cannot_be_interpolated_is_refused(string line) =>
        Assert.Throws<ComposePlanException>(() => Of($"services:\n  web:\n    image: nginx\n    {line}"));

    [Fact]
    public void An_unresolved_value_under_a_key_without_a_prefix_is_ignored() =>
        Assert.Empty(Of("services:\n  web:\n    image: nginx\n    labels:\n      com.example.owner: \"${UNSET}\"\n"));

    [Theory]
    [InlineData("caddy.tls.dns", "cloudflare s3cr3t-token", false)]
    [InlineData("traefik.http.routers.api.rule", "Host(`api.x`) && Header(`X-Api-Key`, `s3cr3t`)", false)]
    [InlineData("traefik.http.routers.api.rule", "Host(`a.x`) || Host(`b.x`)", true)]
    [InlineData("traefik.http.routers.api.rule", "PathPrefix(`/admin`)", false)]
    [InlineData("traefik.http.routers.api.rule", "Path(`/`)", false)]
    [InlineData("traefik.http.routers.api.rule", "Host(`a.x`) &&", false)]                 // does not parse
    [InlineData("traefik.http.routers.api.rule", "!(Host(`a.x`)) && Method(`GET`)", true)]
    [InlineData("traefik.http.services.api.loadbalancer.server.port", "8080", true)]
    public void Only_literal_values_of_a_safe_shape_are_shown(string key, string value, bool shown) =>
        Assert.Equal(shown, ComposeExposures.ShowsLabelValue(key, value));

    [Fact]
    public void A_literal_secret_label_value_is_withheld_and_the_detail_is_the_key()
    {
        var label = Single("services:\n  web:\n    image: nginx\n    labels:\n      caddy.tls.dns: \"cloudflare s3cr3t\"\n");

        Assert.True(label.ValueWithheld);
        Assert.Equal("caddy.tls.dns", label.Detail);
        Assert.DoesNotContain("s3cr3t", label.Display);
    }

    // ---------------------------------------------------------------- shared namespaces, files

    [Theory]
    [InlineData("network_mode", "service:vpn")]
    [InlineData("pid", "service:vpn")]
    [InlineData("ipc", "container:helper")]
    public void A_shared_namespace_is_noted(string field, string mode)
    {
        var (_, shared) = ComposeModel.ExposuresOf(
            [("/srv/app/compose.yaml", $"services:\n  web:\n    image: nginx\n    {field}: \"{mode}\"\n  vpn:\n    image: wg\n")],
            new Dictionary<string, string?>(), Prefixes);

        var entry = Assert.Single(shared);
        Assert.Equal(("web", field, mode, false), (entry.Service, entry.Field, entry.Reference, entry.FromVariable));
    }

    [Fact]
    public void A_relative_label_file_is_refused_also_after_interpolation()
    {
        Assert.Throws<ComposePlanException>(() => Of("services:\n  web:\n    image: nginx\n    label_file: ./labels\n"));
        Assert.Throws<ComposePlanException>(() => Of("services:\n  web:\n    image: nginx\n    label_file: ${LF}\n",
            new Dictionary<string, string?> { ["LF"] = "labels.env" }));
        Assert.Empty(Of("services:\n  web:\n    image: nginx\n    label_file: ${LF}\n",
            new Dictionary<string, string?> { ["LF"] = "/etc/app/labels.env" }));
    }

    [Fact]
    public void Merge_keys_and_extends_in_the_same_file_are_followed()
    {
        var exposures = Of("x-base: &base\n  privileged: true\nservices:\n  base:\n    image: nginx\n    cap_add: [SYS_ADMIN]\n" +
                           "  web:\n    <<: *base\n    image: nginx\n    extends: base\n");

        Assert.Contains(exposures, e => e.Kind == ExposureKind.Privileged);
        Assert.Contains(exposures, e => e is { Kind: ExposureKind.AddedCapability, Key: "SYS_ADMIN" });
    }

    // ---------------------------------------------------------------- what docker inspect shows (the re-check)

    [Fact]
    public void An_inspected_container_is_normalised_like_the_plan()
    {
        using var document = JsonDocument.Parse("""
            {"Id":"abc","Name":"/shop-web-1","Config":{"Labels":{"com.docker.compose.service":"web","docktail.funnel.enable":"true"}},
             "HostConfig":{"NetworkMode":"container:0123abcd","PidMode":"host","Privileged":false,"CapAdd":["CAP_NET_ADMIN"],
               "PortBindings":{"80/tcp":[{"HostIp":"","HostPort":"8080"},{"HostIp":"127.0.0.1","HostPort":"9090"}],
                               "53/udp":[{"HostIp":"0.0.0.0","HostPort":""}]}}}
            """);

        var (exposures, shared) = ComposeExposures.FromInspect("web", document.RootElement, Prefixes);

        Assert.Contains(exposures, e => e is { Kind: ExposureKind.AddedCapability, Key: "NET_ADMIN" });
        Assert.Contains(exposures, e => e is { Kind: ExposureKind.HostNamespace, Key: "pid" });
        Assert.Contains(exposures, e => e is { Kind: ExposureKind.PublishedPort, Key: "80/tcp", Value: "0.0.0.0:8080" });
        Assert.Contains(exposures, e => e is { Kind: ExposureKind.PublishedPort, Key: "53/udp", Value: "0.0.0.0:*" });
        Assert.DoesNotContain(exposures, e => e.Value.Contains("9090", StringComparison.Ordinal));   // loopback
        Assert.Contains(exposures, e => e is { Kind: ExposureKind.RouteLabel, Key: "docktail.funnel.enable" });
        Assert.Equal(("network_mode", "0123abcd"), Assert.Single(shared));
        Assert.Equal(("abc", "shop-web-1", "web"), ComposeExposures.IdentityOf(document.RootElement));
    }

    [Fact]
    public void The_same_key_with_another_value_is_another_identity()
    {
        var decided = ComposeExposures.Identity("web", ExposureKind.PublishedPort, "80/tcp", "0.0.0.0:8080");
        var running = ComposeExposures.Identity("web", ExposureKind.PublishedPort, "80/tcp", "0.0.0.0:9090");

        Assert.NotEqual(ComposeExposures.ComparisonKey(decided), ComposeExposures.ComparisonKey(running));
    }

    [Fact]
    public void An_image_line_that_cannot_be_read_fails_closed()
    {
        Assert.Throws<ComposePlanException>(() => ComposeExposures.ParseImages("nginx\tnot-an-id\t{}\n", ["nginx"]));
        Assert.Throws<ComposePlanException>(() => ComposeExposures.ParseImages("other\tMISSING\n", ["nginx"]));
        Assert.Throws<ComposePlanException>(() => ComposeExposures.ParseImages("nginx\tsha256:1\n", ["nginx"]));
        // The labels come from the image's whole Config (Docker 29 fails .Config.Labels on an image without labels).
        Assert.Throws<ComposePlanException>(() => ComposeExposures.ParseImages("nginx\tsha256:1\t{\"Labels\":[1]}\n", ["nginx"]));
        Assert.Throws<ComposePlanException>(() => ComposeExposures.ParseImages("nginx\tsha256:1\t[]\n", ["nginx"]));
        var parsed = ComposeExposures.ParseImages(
            "nginx\tsha256:1\t{\"Env\":[\"A=1\"],\"Labels\":{\"caddy\":\"x\"}}\nredis\tMISSING\n" +
            "built\tsha256:3\tnull\nbare\tsha256:4\t{\"Cmd\":[\"sh\"]}\n",
            ["nginx", "redis", "built", "bare"]);
        Assert.Equal(new Dictionary<string, string> { ["caddy"] = "x" }, parsed["nginx"]!.Value.Labels);
        Assert.Empty(parsed["bare"]!.Value.Labels);
        Assert.Empty(parsed["built"]!.Value.Labels);
        Assert.Equal("sha256:1", parsed["nginx"]!.Value.Id);
        Assert.Null(parsed["redis"]);
    }

    [Fact]
    public void The_image_inspection_reads_the_whole_config_not_its_labels()
    {
        var command = ComposeExposures.InspectImagesCommand(["nginx"]);

        Assert.Contains("{{json .Config}}", command);
        Assert.DoesNotContain(".Config.Labels", command);
    }
}
