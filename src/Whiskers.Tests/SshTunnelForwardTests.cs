using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Docker;

namespace Whiskers.Tests;

/// <summary>
/// Fable review (host access, Medium): an SSH tunnel forwards to the host's real docker.sock, so its local end is a
/// socket only Whiskers' user can open — not a loopback TCP port anything on the machine could connect to.
/// </summary>
public sealed class SshTunnelForwardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-tests", "tunnels-" + Guid.NewGuid().ToString("N")[..8]);

    private SshTunnelManager Manager(bool unix = true) =>
        new(new FakeServerConfig(), NullLogger<SshTunnelManager>.Instance) { SocketRoot = _dir, UseUnixSockets = unix };

    [Fact]
    public void The_local_end_is_a_unix_socket_in_a_private_directory()
    {
        var forward = Manager().NewForward("neuhof");

        Assert.Null(forward.Port);
        Assert.Equal(_dir, Path.GetDirectoryName(forward.SocketPath));
        Assert.Equal("unix", forward.Endpoint.Scheme);
        Assert.True(forward.SocketPath!.Length < 100, "a socket path is limited to about 108 bytes");
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
    }

    [Fact]
    public void Each_attempt_gets_its_own_socket_and_the_server_id_never_reaches_the_path()
    {
        var manager = Manager();
        var first = manager.NewForward("../../etc/neuhof");
        var second = manager.NewForward("../../etc/neuhof");

        Assert.NotEqual(first.SocketPath, second.SocketPath);
        Assert.DoesNotContain("..", Path.GetFileName(first.SocketPath));
        Assert.DoesNotContain("neuhof", first.SocketPath);
    }

    [Fact]
    public void Ssh_binds_the_socket_owner_only()
    {
        var forward = Manager().NewForward("neuhof");
        var args = SshTunnelManager.BuildTunnelArguments(
            new ServerConfig { SshUser = "root", SshHost = "10.0.0.5", SshPort = 22 }, keyPath: null, forward.SshSpec);

        var l = args.IndexOf("-L");
        Assert.Equal($"{forward.SocketPath}:/var/run/docker.sock", args[l + 1]);
        Assert.Contains("StreamLocalBindMask=0177", args);
        Assert.Contains("StreamLocalBindUnlink=yes", args);
    }

    [Fact]
    public void Only_without_unix_sockets_is_a_loopback_port_used()
    {
        var forward = Manager(unix: false).NewForward("neuhof");
        Assert.NotNull(forward.Port);
        Assert.Equal("127.0.0.1", forward.Endpoint.Host);
    }

    [Fact]
    public void The_platform_default_is_a_unix_socket_except_on_windows()
    {
        var manager = new SshTunnelManager(new FakeServerConfig(), NullLogger<SshTunnelManager>.Instance);
        Assert.Equal(!OperatingSystem.IsWindows(), manager.UseUnixSockets);
    }

    [SkippableFact]
    public void A_directory_another_user_could_have_prepared_is_refused()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes");
        Directory.CreateDirectory(_dir);
        File.SetUnixFileMode(_dir, (UnixFileMode)0b111_111_111);   // 0777, as a squatter in /tmp would leave it

        Assert.Throws<InvalidOperationException>(() => Manager().NewForward("neuhof"));
    }

    [SkippableFact]
    public void A_symlink_in_place_of_the_directory_is_refused()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix symlinks");
        var target = _dir + "-target";
        Directory.CreateDirectory(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Directory.CreateSymbolicLink(_dir, target);
            Assert.Throws<InvalidOperationException>(() => Manager().NewForward("neuhof"));
        }
        finally
        {
            try { Directory.Delete(_dir); } catch (IOException) { }
            Directory.Delete(target, true);
        }
    }

    [SkippableFact]
    public void Without_a_fixed_directory_each_manager_gets_a_fresh_private_one_and_removes_it()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix sockets");
        var a = new SshTunnelManager(new FakeServerConfig(), NullLogger<SshTunnelManager>.Instance);
        var b = new SshTunnelManager(new FakeServerConfig(), NullLogger<SshTunnelManager>.Instance);
        var dirA = Path.GetDirectoryName(a.NewForward("neuhof").SocketPath)!;
        var dirB = Path.GetDirectoryName(b.NewForward("neuhof").SocketPath)!;

        Assert.NotEqual(dirA, dirB);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dirA));

        a.Dispose();
        b.Dispose();
        Assert.False(Directory.Exists(dirA));
        Assert.False(Directory.Exists(dirB));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
