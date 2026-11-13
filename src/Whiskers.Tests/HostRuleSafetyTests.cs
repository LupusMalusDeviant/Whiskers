using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// Fable review (host access, Medium): a firewall value with a trailing newline must not reach the shell, and an nginx
/// config that `nginx -t` refuses must not stay live.
/// </summary>
public class HostRuleSafetyTests
{
    private sealed class RecordingHost(Func<string, CommandResult>? answer = null) : IHostCommandExecutor
    {
        public List<string> Commands { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Commands.Add(command);
            return Task.FromResult(answer?.Invoke(command) ?? new CommandResult { ExitCode = 0, Output = "" });
        }
    }

    [Theory]
    [InlineData("22\n", null)]
    [InlineData("22", "10.0.0.5\n")]
    [InlineData("22\r\n", null)]
    [InlineData("22", "Anywhere\n")]
    public async Task A_firewall_value_with_a_trailing_newline_never_reaches_the_host(string port, string? from)
    {
        var host = new RecordingHost();
        var firewall = new FirewallService(host, NullLogger<FirewallService>.Instance);

        var result = await firewall.AddRuleAsync("srv", port, "tcp", "allow", from);

        Assert.False(result.Success);
        Assert.Empty(host.Commands);
    }

    [Fact]
    public async Task A_plain_firewall_rule_still_goes_through()
    {
        var host = new RecordingHost();
        var result = await new FirewallService(host, NullLogger<FirewallService>.Instance).AddRuleAsync("srv", "22", "tcp", "allow", "10.0.0.5/32");
        Assert.True(result.Success);
        Assert.Equal(["ufw allow from 10.0.0.5/32 to any port 22 proto tcp"], host.Commands);
    }

    [Fact]
    public async Task A_config_nginx_refuses_is_replaced_by_the_previous_file()
    {
        var host = new RecordingHost(c => c.StartsWith("nginx -t", StringComparison.Ordinal)
            ? new CommandResult { ExitCode = 1, Error = "emerg" }
            : new CommandResult { ExitCode = 0 });
        var nginx = new NginxService(host, NullLogger<NginxService>.Instance);

        var result = await nginx.UpdateSiteConfigAsync("srv", "shop", "server { broken");

        Assert.False(result.Success);
        Assert.Contains("previous file was put back", result.Error);
        var write = host.Commands[0];
        Assert.True(write.IndexOf("cp -p", StringComparison.Ordinal) < write.IndexOf("> '/etc/nginx/sites-available/shop'", StringComparison.Ordinal),
            "the previous file is kept before the new one is written");
        Assert.StartsWith("rm -f '/etc/nginx/sites-available/shop.whiskers-prev'", write);   // never a stale kept file
        Assert.Equal("nginx -t 2>&1", host.Commands[1]);
        Assert.Contains("mv -f '/etc/nginx/sites-available/shop.whiskers-prev' '/etc/nginx/sites-available/shop'", host.Commands[2]);
        Assert.Contains("else rm -f '/etc/nginx/sites-available/shop'", host.Commands[2]);   // a new site is removed again
        Assert.DoesNotContain(host.Commands, c => c.Contains("reload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_config_nginx_accepts_drops_the_kept_file_and_reloads()
    {
        var host = new RecordingHost();
        var result = await new NginxService(host, NullLogger<NginxService>.Instance).UpdateSiteConfigAsync("srv", "shop", "server {}");

        Assert.True(result.Success);
        Assert.Contains(host.Commands, c => c == "rm -f '/etc/nginx/sites-available/shop.whiskers-prev'");
        Assert.DoesNotContain(host.Commands.Skip(1), c => c.Contains("mv -f", StringComparison.Ordinal));   // no restore after a passed test
    }

    [Fact]
    public async Task A_write_that_fails_half_way_puts_the_previous_file_back()
    {
        var host = new RecordingHost(c => c.Contains("base64 -d", StringComparison.Ordinal)
            ? new CommandResult { ExitCode = 1, Error = "No space left on device" }
            : new CommandResult { ExitCode = 0 });

        var result = await new NginxService(host, NullLogger<NginxService>.Instance).UpdateSiteConfigAsync("srv", "shop", "server {}");

        Assert.False(result.Success);
        // The restore is decided inside the write itself, where the failing step is known — nothing runs after it.
        var write = Assert.Single(host.Commands);
        Assert.Contains("if cat '/etc/nginx/sites-available/shop.whiskers-next' > '/etc/nginx/sites-available/shop'; then", write);
        Assert.Contains("cp -p '/etc/nginx/sites-available/shop' '/etc/nginx/sites-available/shop.whiskers-keep' && mv -f", write);
    }

    /// <summary>A host that runs each command in a real <c>sh</c>, sites-available mapped to <paramref name="dir"/>;
    /// <paramref name="failing"/> names a command replaced by one that fails (a full disk at that step).</summary>
    private static RecordingHost RealShell(string dir, string? failing = null) => new(c =>
    {
        if (c.StartsWith("nginx -t", StringComparison.Ordinal)) return new CommandResult { ExitCode = 1, Error = "emerg" };
        var psi = new System.Diagnostics.ProcessStartInfo("sh") { RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(c.Replace("/etc/nginx/sites-available", dir, StringComparison.Ordinal));
        if (failing is not null)
        {
            var bin = Path.Combine(dir + "-bin");
            Directory.CreateDirectory(bin);
            var stub = Path.Combine(bin, failing);
            File.WriteAllText(stub, "#!/bin/sh\n: > \"$(eval echo \\${$#})\" 2>/dev/null\nexit 1\n");   // truncates its target, then fails
            File.SetUnixFileMode(stub, (UnixFileMode)0b111_101_101);
            psi.Environment["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH");
        }
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        return new CommandResult { ExitCode = p.ExitCode };
    });

    [SkippableFact]
    public async Task On_a_real_shell_a_copy_that_fails_on_a_full_disk_leaves_the_site_intact()
    {
        Skip.If(OperatingSystem.IsWindows(), "needs a POSIX shell");
        var dir = Path.Combine(Path.GetTempPath(), "nginx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shop"), "old");

            var result = await new NginxService(RealShell(dir, failing: "cp"), NullLogger<NginxService>.Instance)
                .UpdateSiteConfigAsync("srv", "shop", "server {}");

            Assert.False(result.Success);
            Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "shop")));
            Assert.Equal(["shop"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, true); if (Directory.Exists(dir + "-bin")) Directory.Delete(dir + "-bin", true); }
    }

    [SkippableFact]
    public async Task On_a_real_shell_a_site_write_that_fails_restores_the_old_file_or_removes_a_new_one()
    {
        Skip.If(OperatingSystem.IsWindows(), "needs a POSIX shell");
        var dir = Path.Combine(Path.GetTempPath(), "nginx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shop"), "old");
            var nginx = new NginxService(RealShell(dir, failing: "cat"), NullLogger<NginxService>.Instance);

            Assert.False((await nginx.UpdateSiteConfigAsync("srv", "shop", "server {}")).Success);
            Assert.False((await nginx.UpdateSiteConfigAsync("srv", "blog", "server {}")).Success);

            Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "shop")));
            Assert.Equal(["shop"], Directory.GetFiles(dir).Select(Path.GetFileName));   // no half-written new site
        }
        finally { Directory.Delete(dir, true); if (Directory.Exists(dir + "-bin")) Directory.Delete(dir + "-bin", true); }
    }

    [Fact]
    public async Task Working_files_left_by_an_interrupted_update_are_not_listed_as_sites()
    {
        var host = new RecordingHost(c => c.Contains("sites-available", StringComparison.Ordinal)
            ? new CommandResult { ExitCode = 0, Output = "shop\nshop.whiskers-prev\nshop.whiskers-next\n" }
            : new CommandResult { ExitCode = 0, Output = "" });

        var sites = await new NginxService(host, NullLogger<NginxService>.Instance).ListSitesAsync("srv");

        Assert.Equal(["shop"], sites.Select(s => s.Name));
    }

    [SkippableFact]
    public async Task On_a_real_shell_a_failed_write_and_a_refused_config_both_leave_the_old_file()
    {
        Skip.If(OperatingSystem.IsWindows(), "needs a POSIX shell");
        var dir = Path.Combine(Path.GetTempPath(), "nginx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shop"), "old");
            File.WriteAllText(Path.Combine(dir, "shop" + NginxService.PreviousSuffix), "stale from a crash");
            var shell = new RecordingHost(c =>
            {
                if (c.StartsWith("nginx -t", StringComparison.Ordinal)) return new CommandResult { ExitCode = 1, Error = "emerg" };
                var psi = new System.Diagnostics.ProcessStartInfo("sh") { RedirectStandardError = true };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(c.Replace("/etc/nginx/sites-available", dir, StringComparison.Ordinal));
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit();
                return new CommandResult { ExitCode = p.ExitCode };
            });

            await new NginxService(shell, NullLogger<NginxService>.Instance).UpdateSiteConfigAsync("srv", "shop", "server { broken");

            Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "shop")));   // never the stale kept file
            Assert.Equal(["shop"], Directory.GetFiles(dir).Select(Path.GetFileName));
        }
        finally { Directory.Delete(dir, true); }
    }

    [SkippableFact]
    public async Task On_a_real_shell_a_refused_config_leaves_the_old_file_in_place()
    {
        Skip.If(OperatingSystem.IsWindows(), "needs a POSIX shell");
        var dir = Path.Combine(Path.GetTempPath(), "nginx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "shop"), "old");
            var shell = new RecordingHost(c =>
            {
                if (c.StartsWith("nginx -t", StringComparison.Ordinal)) return new CommandResult { ExitCode = 1, Error = "emerg" };
                var psi = new System.Diagnostics.ProcessStartInfo("sh") { RedirectStandardError = true };
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(c.Replace("/etc/nginx/sites-available", dir, StringComparison.Ordinal));
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit();
                return new CommandResult { ExitCode = p.ExitCode };
            });

            await new NginxService(shell, NullLogger<NginxService>.Instance).UpdateSiteConfigAsync("srv", "shop", "server { broken");

            Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "shop")));
            Assert.False(File.Exists(Path.Combine(dir, "shop" + NginxService.PreviousSuffix)));
        }
        finally { Directory.Delete(dir, true); }
    }
}
