using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Database;
using Whiskers.Services.Docker;
using Whiskers.Services.Server;
using Whiskers.Services.Terminal;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>R1-42 (INV-41; TA-41/42, E-01/02/03): a caller-supplied value that becomes an operand of a host command is
/// checked by one shared validator (<see cref="CommandOperands"/>) at the execution boundary, and positional operands
/// follow <c>--</c>. Quoting alone is not enough: <c>'-oProxyCommand=…'</c> reaches ssh as an option once the shell has
/// removed the quotes. Every boundary here refuses option-leading branch/user/host values, invalid ports and unsafe site
/// names before a process or a host command exists.</summary>
public class OptionInjectionTests
{
    // ---- The shared validator -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("-oProxyCommand=touch /tmp/pwned")]
    [InlineData("--upload-pack=x")]
    [InlineData("-x")]
    [InlineData(".hidden")]
    [InlineData("root@evil")]
    [InlineData("ro ot")]
    [InlineData("root;id")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unsafe_ssh_user_is_refused(string? user) => Assert.False(CommandOperands.IsSafeSshUser(user));

    [Theory]
    [InlineData("root")]
    [InlineData("deploy-user")]
    [InlineData("svc_whiskers")]
    [InlineData("a.b")]
    public void A_plain_ssh_user_is_accepted(string user) => Assert.True(CommandOperands.IsSafeSshUser(user));

    [Theory]
    [InlineData("-oProxyCommand=calc")]
    [InlineData("-p")]
    [InlineData("host -oProxyCommand=x")]
    [InlineData("user@host")]
    [InlineData("ssh://host")]
    [InlineData("host/path")]
    [InlineData("-host.example.com")]
    [InlineData("a.-b.example.com")]
    [InlineData("fe80::1%eth0")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unsafe_host_is_refused(string? host) => Assert.False(CommandOperands.IsSafeHost(host));

    [Theory]
    [InlineData("example.com")]
    [InlineData("web-01.internal.example.com")]
    [InlineData("badwolf")]
    [InlineData("10.0.0.1")]
    [InlineData("100.89.113.6")]
    [InlineData("fd00::1")]
    [InlineData("2001:db8::42")]
    public void A_host_name_or_ip_is_accepted(string host) => Assert.True(CommandOperands.IsSafeHost(host));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void An_invalid_port_is_refused(int port) => Assert.False(CommandOperands.IsValidPort(port));

    [Theory]
    [InlineData(1)]
    [InlineData(22)]
    [InlineData(65535)]
    public void A_valid_port_is_accepted(int port) => Assert.True(CommandOperands.IsValidPort(port));

    [Theory]
    [InlineData("..")]
    [InlineData("-x")]
    [InlineData("--help")]
    [InlineData(".htaccess")]
    [InlineData("a..b")]
    [InlineData("../etc")]
    [InlineData("sites/x")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unsafe_site_name_is_refused(string? name) => Assert.False(CommandOperands.IsSafeSiteName(name));

    [Theory]
    [InlineData("default")]
    [InlineData("example.com")]
    [InlineData("my_site-2.conf")]
    public void A_plain_site_name_is_accepted(string name) => Assert.True(CommandOperands.IsSafeSiteName(name));

    [Theory]
    [InlineData("--privileged")]
    [InlineData("-u")]
    [InlineData("/web")]
    [InlineData("web;id")]
    [InlineData("")]
    public void An_unsafe_container_reference_is_refused(string name)
        => Assert.False(CommandOperands.IsSafeContainerRef(name));

    [Fact]
    public void The_refusal_names_the_field_and_is_an_argument_exception()
    {
        var ex = Assert.Throws<UnsafeOperandException>(() => CommandOperands.SshDestination("-oProxyCommand=x", "h", 22));
        Assert.IsAssignableFrom<ArgumentException>(ex);
        Assert.Equal("SSH user", ex.Field);
        Assert.Equal("SSH port", Assert.Throws<UnsafeOperandException>(() => CommandOperands.SshDestination("root", "h", 0)).Field);
        Assert.Equal("SSH host", Assert.Throws<UnsafeOperandException>(() => CommandOperands.SshDestination("root", "-h", 22)).Field);
    }

    // ---- SSH: host command executor --------------------------------------------------------------------------------

    [Theory]
    [InlineData("-oProxyCommand=touch /tmp/pwned", "host.example.com", 22, "SSH user")]
    [InlineData("root", "-oProxyCommand=touch /tmp/pwned", 22, "SSH host")]
    [InlineData("root", "host.example.com", 0, "SSH port")]
    [InlineData("root", "host.example.com", 70000, "SSH port")]
    public async Task The_executor_refuses_an_unsafe_ssh_target_before_starting_a_process(string user, string host, int port,
        string field)
    {
        // Password auth, so no key file is needed and the check is the only thing between the value and sshpass/ssh.
        var server = new ServerConfig
        {
            Id = "edge", Name = "edge", ConnectionType = ConnectionType.SSH,
            SshUser = user, SshHost = host, SshPort = port, SshPassword = "not-used",
        };
        var executor = new HostCommandExecutor(new FakeServerConfig(server), new FakeDocker(),
            NullLogger<HostCommandExecutor>.Instance);

        var result = await executor.ExecuteAsync("edge", "true");

        Assert.Equal(-1, result.ExitCode);
        Assert.StartsWith($"Invalid {field}", result.Error);
    }

    [Fact]
    public void The_executor_puts_every_option_before_double_dash_and_the_destination_after_it()
    {
        var server = new ServerConfig { SshUser = "deploy", SshHost = "web.example.com", SshPort = 2222 };
        var args = HostCommandExecutor.BuildSshArguments(server, keyPath: "/keys/edge", "uptime && df -h");

        var dash = args.IndexOf("--");
        Assert.True(dash > 0);
        Assert.Equal(["--", "deploy@web.example.com", "uptime && df -h"], args.Skip(dash).ToArray());
        Assert.Equal("2222", args[args.IndexOf("-p") + 1]);
        Assert.Contains("-i", args.Take(dash));
        Assert.Equal(1, args.Count(a => a == "--"));
    }

    [Fact]
    public void The_executor_argument_builder_refuses_an_option_leading_user()
        => Assert.Throws<UnsafeOperandException>(() => HostCommandExecutor.BuildSshArguments(
            new ServerConfig { SshUser = "-oProxyCommand=x", SshHost = "h", SshPort = 22 }, keyPath: null, "true"));

    // ---- SSH: tunnel ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_tunnel_puts_the_destination_after_double_dash()
    {
        var server = new ServerConfig { SshUser = "root", SshHost = "10.0.0.5", SshPort = 22 };
        var args = SshTunnelManager.BuildTunnelArguments(server, keyPath: null, localPort: 40000);

        Assert.Equal(["--", "root@10.0.0.5"], args.TakeLast(2).ToArray());
        Assert.Equal(1, args.Count(a => a == "--"));
        Assert.Contains("-L", args);
    }

    [Theory]
    [InlineData("-oProxyCommand=x", "h", 22)]
    [InlineData("root", "-oProxyCommand=x", 22)]
    [InlineData("root", "h", 0)]
    public void The_tunnel_refuses_an_unsafe_target(string user, string host, int port)
        => Assert.Throws<UnsafeOperandException>(() => SshTunnelManager.BuildTunnelArguments(
            new ServerConfig { SshUser = user, SshHost = host, SshPort = port }, keyPath: null, localPort: 40000));

    // ---- SSH and docker: terminal ----------------------------------------------------------------------------------

    [Fact]
    public void A_host_terminal_puts_the_destination_after_double_dash()
    {
        var launch = TerminalLaunch.Ssh("web.example.com", 22, "root", keyPath: null);
        Assert.Equal("ssh", launch.FileName);
        Assert.Equal(["--", "root@web.example.com"], launch.Arguments.TakeLast(2).ToArray());
    }

    [Theory]
    [InlineData("web.example.com", 22, "-oProxyCommand=x")]
    [InlineData("-oProxyCommand=x", 22, "root")]
    [InlineData("web.example.com", 0, "root")]
    public void A_terminal_refuses_an_unsafe_ssh_target(string host, int port, string user)
    {
        Assert.Throws<UnsafeOperandException>(() => TerminalLaunch.Ssh(host, port, user, keyPath: null));
        Assert.Throws<UnsafeOperandException>(() => TerminalLaunch.SshDockerExec(host, port, user, keyPath: null, "c0ffee"));
    }

    [Fact]
    public void A_container_terminal_checks_the_container_and_puts_it_after_double_dash()
    {
        var local = TerminalLaunch.Local("/bin/bash", "c0ffee");
        Assert.Equal(["exec", "-i", "--", "c0ffee"], local.Arguments.Take(4).ToArray());

        var remote = TerminalLaunch.SshDockerExec("web.example.com", 22, "root", keyPath: null, "c0ffee");
        Assert.StartsWith("docker exec -it -- 'c0ffee' ", remote.Arguments[^1]);
        Assert.Equal("root@web.example.com", remote.Arguments[^2]);
        Assert.Equal("--", remote.Arguments[^3]);

        Assert.Throws<UnsafeOperandException>(() => TerminalLaunch.Local("/bin/bash", "--privileged"));
        Assert.Throws<UnsafeOperandException>(() =>
            TerminalLaunch.SshDockerExec("web.example.com", 22, "root", keyPath: null, "-u"));
    }

    // ---- nginx ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("..")]
    [InlineData("-x")]
    [InlineData("--version")]
    [InlineData("a..b")]
    public async Task Nginx_refuses_an_unsafe_site_name_before_any_host_command(string siteName)
    {
        var host = new RecordingHost();
        var nginx = new NginxService(host, NullLogger<NginxService>.Instance);

        await Assert.ThrowsAsync<UnsafeOperandException>(() => nginx.GetSiteConfigAsync("local", siteName));
        await Assert.ThrowsAsync<UnsafeOperandException>(() => nginx.UpdateSiteConfigAsync("local", siteName, "server {}"));
        await Assert.ThrowsAsync<UnsafeOperandException>(() => nginx.EnableSiteAsync("local", siteName));
        await Assert.ThrowsAsync<UnsafeOperandException>(() => nginx.DisableSiteAsync("local", siteName));
        Assert.Empty(host.Commands);
    }

    [Fact]
    public async Task Nginx_lists_no_site_whose_name_it_would_refuse()
    {
        var host = new RecordingHost { Output = "default\n..\n-evil\nexample.com\n" };
        var nginx = new NginxService(host, NullLogger<NginxService>.Instance);

        var sites = await nginx.ListSitesAsync("local");

        Assert.Equal(["default", "example.com"], sites.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // ---- database exec ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_database_command_checks_the_container_and_puts_it_after_double_dash()
    {
        var host = new RecordingHost();
        var db = new DatabaseService(host, NullLogger<DatabaseService>.Instance);
        var creds = new DatabaseCredentials { User = "postgres" };

        await db.ExecuteQueryAsync("c0ffee", "select 1", DatabaseType.PostgreSQL, creds, "local");
        Assert.StartsWith("docker exec -- 'c0ffee' ", Assert.Single(host.Commands));

        await Assert.ThrowsAsync<UnsafeOperandException>(() =>
            db.ExecuteQueryAsync("--privileged", "select 1", DatabaseType.PostgreSQL, creds, "local"));
        Assert.Single(host.Commands);
    }

    /// <summary>Records every host command and answers each with <see cref="Output"/>.</summary>
    private sealed class RecordingHost : IHostCommandExecutor
    {
        public List<string> Commands { get; } = [];
        public string Output { get; set; } = "";

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            lock (Commands) Commands.Add(command);
            return Task.FromResult(new CommandResult { ExitCode = 0, Output = Output });
        }
    }
}
