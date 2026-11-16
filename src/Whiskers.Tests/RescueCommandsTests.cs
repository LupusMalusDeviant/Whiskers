using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Services.Onboarding;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018 part D (INV-41): the fixed rescue and clean-up scripts. Every value reaches the shell only base64
/// encoded; the host keys are read between fixed markers; the removal takes exactly the server's bootstrap lines
/// (prefix plus 8 hex characters) and nothing else, and says so only when none is left.
/// </summary>
public sealed class RescueCommandsTests
{
    private static readonly string Blob = FakeSshToolchain.KeyText('g');
    private const string Marker = "whiskers-bootstrap-web-new-0123abcd";

    private static IEnumerable<string> Decoded(string script) => Regex
        .Matches(script, "echo ([A-Za-z0-9+/=]+) \\| base64 -d")
        .Select(m => Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value)));

    [Fact]
    public void Every_value_reaches_the_shell_only_base64_encoded()
    {
        var script = RescueCommands.InjectAndReadHostKeys($"ssh-ed25519 {Blob} {Marker}", Marker);

        Assert.DoesNotContain(Blob, script, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, script, StringComparison.Ordinal);
        Assert.Equal([$"ssh-ed25519 {Blob} {Marker}", Blob, Marker], Decoded(script));
        Assert.Contains("chmod 0700", script);
        Assert.Contains("chmod 0600", script);
        Assert.DoesNotContain("web-new", RescueCommands.RemoveMarkerLines("web-new"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_without_the_marker_gets_it_and_a_line_break_is_refused()
    {
        var script = RescueCommands.InjectAndReadHostKeys($"ssh-ed25519 {Blob} other-comment", Marker);
        Assert.Equal($"ssh-ed25519 {Blob} {Marker}", Decoded(script).First());

        Assert.Throws<ArgumentException>(() => RescueCommands.InjectAndReadHostKeys($"ssh-ed25519 {Blob}\nevil", Marker));
        Assert.Throws<ArgumentException>(() => RescueCommands.InjectAndReadHostKeys("ssh-ed25519", Marker));
    }

    [Fact]
    public void Host_keys_are_read_only_between_the_markers()
    {
        var ed = "ssh-ed25519 " + FakeSshToolchain.KeyText('h');
        var output = $"noise {ed}\n{RescueCommands.HostKeysBegin}\n{ed} root@host\nnot a key\n{RescueCommands.HostKeysEnd}\nssh-rsa {FakeSshToolchain.KeyText('r')}\n";

        Assert.Equal([$"{ed} root@host"], RescueCommands.HostKeysOf(output));
        Assert.Empty(RescueCommands.HostKeysOf(ed));
    }

    [Fact]
    public void The_marker_is_the_server_prefix_and_eight_hex_characters()
    {
        Assert.Equal("whiskers-bootstrap-web-new-abcdef01", RescueCommands.Marker("web-new", "ABCDEF0123456789"));
        Assert.Matches("^whiskers-bootstrap-web-new-[0-9a-f]{8}$", RescueCommands.Marker("web-new", "op"));
    }

    [SkippableFact]
    public void Cleanup_removes_every_bootstrap_line_of_the_server_and_no_other()
    {
        var bash = DeploymentScripts.FindBash();
        if (bash is null && DeploymentScripts.InCi) Assert.Fail("bash is required to run the clean-up script");
        Skip.If(bash is null, "no bash available");
        var dir = Directory.CreateTempSubdirectory("whiskers-cleanup-");
        try
        {
            var file = Path.Combine(dir.FullName, "authorized_keys");
            var keep = new[]
            {
                $"ssh-ed25519 {FakeSshToolchain.KeyText('a')} admin@laptop",
                $"ssh-ed25519 {FakeSshToolchain.KeyText('b')} whiskers-bootstrap-web-new-2-0123abcd",   // another server's line
                $"ssh-ed25519 {FakeSshToolchain.KeyText('c')} whiskers-bootstrap-web-new-0123abc",      // 7 hex: not ours
                $"ssh-ed25519 {FakeSshToolchain.KeyText('d')} whiskers-bootstrap-web-new-0123abcdx",    // 9 characters
                $"ssh-ed25519 {FakeSshToolchain.KeyText('e')} whiskers-bootstrap-web-new-0123ABCD",     // upper case
            };
            var ours = new[]
            {
                $"ssh-ed25519 {FakeSshToolchain.KeyText('f')} whiskers-bootstrap-web-new-0123abcd",
                $"ssh-ed25519 {FakeSshToolchain.KeyText('g')} whiskers-bootstrap-web-new-deadbeef",    // an earlier run's
            };
            File.WriteAllText(file, string.Join("\n", [keep[0], ours[0], keep[1], keep[2], ours[1], keep[3], keep[4]]) + "\n");
            var script = RescueCommands.RemoveMarkerLines("web-new").Replace("F=/root/.ssh/authorized_keys",
                "F=" + DeploymentScripts.ForBash(file), StringComparison.Ordinal);

            var run = DeploymentScripts.Run(bash!, ["-c", script]);

            Assert.True(run.Exit == 0, run.Stderr);
            Assert.Contains(RescueCommands.Clean, run.Stdout);
            Assert.Equal(keep, File.ReadAllLines(file));
            Assert.True(BootstrapCleanup.Judge(new Whiskers.Services.Server.CommandResult { ExitCode = 0, Output = run.Stdout }).Removed);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Only_a_successful_run_that_printed_the_clean_marker_counts_as_removed()
    {
        Assert.False(BootstrapCleanup.Judge(new Whiskers.Services.Server.CommandResult { ExitCode = 0, Output = "" }).Removed);
        Assert.False(BootstrapCleanup.Judge(new Whiskers.Services.Server.CommandResult { ExitCode = 4, Output = RescueCommands.Clean }).Removed);
        Assert.True(BootstrapCleanup.Judge(new Whiskers.Services.Server.CommandResult { ExitCode = 0, Output = RescueCommands.Clean + "\n" }).Removed);
    }
}
