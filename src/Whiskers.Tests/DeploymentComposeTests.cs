using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Deployment;
using Whiskers.Services.Mutations;
using YamlDotNet.RepresentationModel;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0013, D1: the Deploy page's form and MCP <c>deploy_app</c> become a compose file through one pure mapper —
/// every row of the mapping table, every refusal, <c>$</c> written as <c>$$</c>, a named volume keeping its name, the
/// ownership marker and the <c>bridge</c> default — and the same request gives the same file on every path.
/// </summary>
public sealed class DeploymentComposeTests
{
    private static DeploymentRequest Request(string image = "nginx:1.27", string? name = "web") =>
        new() { Image = image, ContainerName = name };

    private static YamlMappingNode Root(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Service(DeploymentComposeFile file) =>
        (YamlMappingNode)((YamlMappingNode)Root(file.Yaml)["services"])[file.Name];

    private static string Scalar(YamlNode node, string key) => ((YamlScalarNode)((YamlMappingNode)node)[key]).Value!;

    private static IReadOnlyList<string> Sequence(YamlNode node, string key) =>
        ((YamlSequenceNode)((YamlMappingNode)node)[key]).Children.Select(c => ((YamlScalarNode)c).Value!).ToList();

    [Fact]
    public void The_name_is_project_service_and_container_name_and_the_file_starts_with_the_marker()
    {
        var file = DeploymentCompose.ToCompose(Request(name: "My.App_1"));

        Assert.Equal("myapp_1", file.Project);
        Assert.Equal("My.App_1", file.Name);
        Assert.StartsWith(DeploymentCompose.Marker + "\n", file.Yaml, StringComparison.Ordinal);
        Assert.Equal("myapp_1", Scalar(Root(file.Yaml), "name"));
        Assert.Equal("My.App_1", Scalar(Service(file), "container_name"));
        Assert.Equal("nginx:1.27", Scalar(Service(file), "image"));
        Assert.Equal("/opt/deployments/myapp_1/docker-compose.yml", file.Deployment("local").Files[0].Path);
        Assert.Null(file.Deployment("local").EnvFileContent);
    }

    [Theory]
    [InlineData("nginx", "nginx")]
    [InlineData("ghcr.io/org/app:1.2", "app")]
    [InlineData("localhost:5000/team/api@sha256:abcd", "api")]
    [InlineData("registry.example.com:443/x/y:latest@sha256:ef", "y")]
    public void An_empty_name_comes_from_the_images_last_path_segment(string image, string expected)
    {
        var file = DeploymentCompose.ToCompose(Request(image, name: "  "));

        Assert.Equal(expected, file.Name);
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("a/b")]
    [InlineData("x;rm")]
    [InlineData("__")]          // normalises to nothing
    [InlineData("-.-")]         // normalises to "-"... trimmed to empty
    public void An_invalid_name_or_project_name_is_refused(string name) =>
        Assert.Throws<DeploymentComposeException>(() => DeploymentCompose.ToCompose(Request(name: name)));

    [Theory]
    [InlineData("")]
    [InlineData("nginx:${TAG}")]
    [InlineData("nginx latest")]
    public void An_empty_image_or_one_with_dollar_or_whitespace_is_refused(string image) =>
        Assert.Throws<DeploymentComposeException>(() => DeploymentCompose.ToCompose(Request(image)));

    [Fact]
    public void Ports_are_tcp_short_syntax_with_an_optional_bind_ip_ipv6_bracketed()
    {
        var request = Request();
        request.PortMappings["8080"] = "80";
        request.PortMappings["8443"] = "443";
        request.PortMappings["9000"] = "9000";
        request.PortBindIps["8443"] = "127.0.0.1";
        request.PortBindIps["9000"] = "::1";

        var ports = Sequence(Service(DeploymentCompose.ToCompose(request)), "ports");

        Assert.Equal(["8080:80/tcp", "127.0.0.1:8443:443/tcp", "[::1]:9000:9000/tcp"], ports);
    }

    [Theory]
    [InlineData("0", "80", null)]
    [InlineData("70000", "80", null)]
    [InlineData("80", "http", null)]
    [InlineData("8080", "80", "localhost")]
    public void A_bad_port_or_bind_ip_is_refused(string host, string container, string? ip)
    {
        var request = Request();
        request.PortMappings[host] = container;
        if (ip is not null) request.PortBindIps[host] = ip;

        Assert.Throws<DeploymentComposeException>(() => DeploymentCompose.ToCompose(request));
    }

    [Fact]
    public void Volumes_are_short_syntax_and_a_named_volume_keeps_its_own_name()
    {
        var request = Request();
        request.Volumes = ["/srv/data:/data", "appdata:/var/lib/app:ro", "/etc/x:/x:Z"];

        var file = DeploymentCompose.ToCompose(request);

        Assert.Equal(["/srv/data:/data", "appdata:/var/lib/app:ro", "/etc/x:/x:Z"], Sequence(Service(file), "volumes"));
        var volume = (YamlMappingNode)((YamlMappingNode)Root(file.Yaml)["volumes"])["appdata"];
        Assert.Equal("appdata", Scalar(volume, "name"));
        // The planner sees the same volume: not project-scoped.
        var (_, _, services) = ComposeModel.MergeWithSource([("/opt/deployments/web/docker-compose.yml", file.Yaml)],
            new Dictionary<string, string?>(), "/opt/deployments/web");
        var mounts = Assert.Single(services).Mounts;
        Assert.Contains(mounts, m => m is { Source: "appdata", ProjectScoped: false });
        Assert.Contains(mounts, m => m.Source == "/srv/data");
    }

    [Theory]
    [InlineData("/data")]                 // one part
    [InlineData("/a:/b:/c:/d")]           // four parts
    [InlineData("./data:/data")]          // relative
    [InlineData("data/sub:/data")]        // relative
    [InlineData("~/data:/data")]          // relative
    [InlineData("/data:/data:rx")]        // unknown mode
    [InlineData("/data:data")]            // relative destination
    [InlineData("${HOME}:/data")]         // a variable
    public void A_bad_volume_is_refused(string volume)
    {
        var request = Request();
        request.Volumes = [volume];

        Assert.Throws<DeploymentComposeException>(() => DeploymentCompose.ToCompose(request));
    }

    [Fact]
    public void Environment_values_are_quoted_and_a_dollar_is_written_twice()
    {
        var request = Request();
        request.EnvironmentVars["PASSWORD"] = "pa$$w0rd: \"x\" # not a comment";
        request.EnvironmentVars["EMPTY"] = "";

        var file = DeploymentCompose.ToCompose(request);

        var env = (YamlMappingNode)Service(file)["environment"];
        Assert.Equal("pa$$$$w0rd: \"x\" # not a comment", Scalar(env, "PASSWORD"));
        Assert.Equal("", Scalar(env, "EMPTY"));
    }

    [Theory]
    [InlineData("1KEY")]
    [InlineData("MY-KEY")]
    [InlineData("A B")]
    [InlineData("")]
    public void An_invalid_environment_key_is_refused(string key)
    {
        var request = Request();
        request.EnvironmentVars[key] = "v";

        Assert.Throws<DeploymentComposeException>(() => DeploymentCompose.ToCompose(request));
    }

    [Fact]
    public void A_key_that_is_only_distinct_by_whitespace_is_a_duplicate()
    {
        var request = Request();
        request.EnvironmentVars["KEY"] = "a";
        request.EnvironmentVars[" KEY"] = "b";

        var ex = Assert.Throws<DeploymentComposeException>(() => DeploymentCompose.ToCompose(request));
        Assert.Contains("'KEY'", ex.Message);
    }

    [Theory]
    [InlineData("unless-stopped", "unless-stopped")]
    [InlineData("", "unless-stopped")]
    [InlineData("always", "always")]
    [InlineData("no", "no")]
    [InlineData("on-failure", "on-failure:5")]
    public void Restart_policies_map_as_before(string policy, string expected)
    {
        var request = Request();
        request.RestartPolicy = policy;

        Assert.Equal(expected, Scalar(Service(DeploymentCompose.ToCompose(request)), "restart"));
    }

    [Fact]
    public void An_unknown_restart_policy_is_refused()
    {
        var request = Request();
        request.RestartPolicy = "sometimes";

        Assert.Throws<DeploymentComposeException>(() => DeploymentCompose.ToCompose(request));
    }

    [Theory]
    [InlineData(null, "bridge")]
    [InlineData("", "bridge")]
    [InlineData("bridge", "bridge")]
    [InlineData("host", "host")]
    [InlineData("none", "none")]
    public void The_network_defaults_to_bridge_as_before(string? network, string mode)
    {
        var request = Request();
        request.NetworkName = network;

        Assert.Equal(mode, Scalar(Service(DeploymentCompose.ToCompose(request)), "network_mode"));
    }

    [Fact]
    public void Another_network_is_joined_as_an_external_network()
    {
        var request = Request();
        request.NetworkName = "proxy";

        var file = DeploymentCompose.ToCompose(request);

        Assert.Equal(["proxy"], Sequence(Service(file), "networks"));
        Assert.Equal("true", Scalar(((YamlMappingNode)Root(file.Yaml)["networks"])["proxy"], "external"));
        Assert.False(Service(file).Children.ContainsKey("network_mode"));
    }

    [Fact]
    public void A_network_with_a_dollar_is_refused()
    {
        var request = Request();
        request.NetworkName = "${NET}";

        Assert.Throws<DeploymentComposeException>(() => DeploymentCompose.ToCompose(request));
    }

    // ---------------------------------------------------------------- deploy_app's arguments

    [Theory]
    [InlineData("8080", null)]
    [InlineData("8080:80:tcp", null)]
    [InlineData("8080:80,:443", null)]
    [InlineData("8080:80,8080:81", null)]
    [InlineData(null, "NOEQUALS")]
    [InlineData(null, "=value")]
    [InlineData(null, "A=1,A=2")]
    public void Deploy_app_refuses_a_malformed_entry_instead_of_dropping_it(string? ports, string? env)
    {
        var ex = Assert.Throws<DeploymentComposeException>(() =>
            DeploymentCompose.FromToolArguments("web", "nginx", ports, env, null, null, null));
        Assert.DoesNotContain("NOEQUALS", ex.Message);
        Assert.DoesNotContain("value", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("../x")]
    public void Deploy_app_refuses_an_invalid_app_name(string name) =>
        Assert.Throws<DeploymentComposeException>(() =>
            DeploymentCompose.FromToolArguments(name, "nginx", null, null, null, null, null));

    [Fact]
    public void The_same_request_gives_the_same_file_from_the_form_and_from_deploy_app()
    {
        var form = new DeploymentRequest
        {
            Image = "postgres:17", ContainerName = "db", RestartPolicy = "always", NetworkName = "backend",
            PortMappings = new() { ["5432"] = "5432" },
            EnvironmentVars = new() { ["POSTGRES_PASSWORD"] = "s3cret$", ["PGDATA"] = "/var/lib/postgresql/data" },
            Volumes = ["pgdata:/var/lib/postgresql/data"],
        };
        var tool = DeploymentCompose.FromToolArguments("db", "postgres:17", "5432:5432",
            "POSTGRES_PASSWORD=s3cret$,PGDATA=/var/lib/postgresql/data", "pgdata:/var/lib/postgresql/data", "always", "backend");

        Assert.Equal(DeploymentCompose.ToCompose(form), DeploymentCompose.ToCompose(tool));
        Assert.Equal(DeploymentCompose.ToCompose(tool).Yaml, DeploymentCompose.ToCompose(tool).Yaml);
    }

    [Fact]
    public void The_planner_reads_the_file_as_one_service_with_its_container_name_and_published_port()
    {
        var request = Request();
        request.PortMappings["8080"] = "80";
        var file = DeploymentCompose.ToCompose(request);

        var (name, _, services) = ComposeModel.MergeWithSource([("/opt/deployments/web/docker-compose.yml", file.Yaml)],
            new Dictionary<string, string?>(), "/opt/deployments/web");
        var (exposures, _) = ComposeModel.ExposuresOf([("/opt/deployments/web/docker-compose.yml", file.Yaml)],
            new Dictionary<string, string?>(), GuardrailPolicy.DefaultExposureLabelPrefixes);

        Assert.Equal("web", name);
        var service = Assert.Single(services);
        Assert.Equal(("web", "web"), (service.Name, service.ContainerName));
        Assert.Contains(exposures["web"], e => e.Kind == ExposureKind.PublishedPort);
    }
}
