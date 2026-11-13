using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests;

/// <summary>Review of main: the hardened profile's socket proxy moved from 127.0.0.1:2375 to a Unix socket, and the UI
/// cannot change a stored server's socket path. A stored server on the old port is moved to the socket on start — only
/// when this instance runs the hardened profile, never otherwise.</summary>
public sealed class HardenedProxySocketMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"proxy-move-{Guid.NewGuid():N}");

    public HardenedProxySocketMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ServerConfig Tcp(string id, string host, int port = 2375, bool tls = false) =>
        new() { Id = id, Name = id, ConnectionType = ConnectionType.TCP, TcpHost = host, TcpPort = port, TcpUseTls = tls, Enabled = true };

    private static ServerConfig Local(string id, string socketPath) =>
        new() { Id = id, Name = id, ConnectionType = ConnectionType.Local, SocketPath = socketPath, Enabled = true };

    private async Task<(ServerConfigService Service, string Store)> Start(string dockerSocket, bool socketExists, params ServerConfig[] stored)
    {
        var store = Path.Combine(_dir, $"servers-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(store, JsonSerializer.Serialize(new ServerConfigData { Servers = stored.ToList() }));
        var probed = new List<string>();
        var service = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = dockerSocket }),
            NullLogger<ServerConfigService>.Instance, storePath: store)
        {
            FileExists = path => { probed.Add(path); return socketExists; },
        };
        await service.InitializeAsync();
        if (dockerSocket == DockerSettings.HardenedProxySocketPath)
            Assert.Contains("/var/run/whiskers-proxy/docker.sock", probed);
        return (service, store);
    }

    [Fact]
    public async Task A_server_on_the_old_proxy_port_moves_to_the_socket_and_the_move_is_stored()
    {
        var (service, store) = await Start(DockerSettings.HardenedProxySocketPath, socketExists: true,
            Local("local", "http://127.0.0.1:2375"), Tcp("proxy", "localhost"), Local("tcp-scheme", "tcp://127.0.0.1:2375"));

        foreach (var id in new[] { "local", "proxy", "tcp-scheme" })
        {
            var server = service.GetServer(id)!;
            Assert.Equal(ConnectionType.Local, server.ConnectionType);
            Assert.Equal(DockerSettings.HardenedProxySocketPath, server.SocketPath);
        }

        // Stored, so the next start (and a restore of the file) sees the socket.
        var reread = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "npipe://./pipe/x" }),
            NullLogger<ServerConfigService>.Instance, storePath: store);
        await reread.InitializeAsync();
        Assert.All(reread.GetServers(), s => Assert.Equal(DockerSettings.HardenedProxySocketPath, s.SocketPath));
    }

    [Fact]
    public async Task Other_servers_are_left_alone()
    {
        var (service, _) = await Start(DockerSettings.HardenedProxySocketPath, socketExists: true,
            Tcp("tls", "127.0.0.1", tls: true), Tcp("other-port", "127.0.0.1", port: 2376), Tcp("remote", "100.64.0.7"),
            Local("raw", "unix:///var/run/docker.sock"), Local("lan", "http://192.168.1.5:2375"),
            Local("own-port", "http://127.0.0.1:2376"));

        Assert.Equal(ConnectionType.TCP, service.GetServer("tls")!.ConnectionType);
        Assert.Equal(ConnectionType.TCP, service.GetServer("other-port")!.ConnectionType);
        Assert.Equal(ConnectionType.TCP, service.GetServer("remote")!.ConnectionType);
        Assert.Equal("unix:///var/run/docker.sock", service.GetServer("raw")!.SocketPath);
        Assert.Equal("http://192.168.1.5:2375", service.GetServer("lan")!.SocketPath);
        Assert.Equal("http://127.0.0.1:2376", service.GetServer("own-port")!.SocketPath);
    }

    [Theory]
    [InlineData("unix:///var/run/docker.sock", true)]          // full profile: a proxy on 2375 is the operator's own
    [InlineData(DockerSettings.HardenedProxySocketPath, false)] // hardened file, but the socket is not there
    public async Task Without_the_hardened_proxy_socket_nothing_moves(string dockerSocket, bool socketExists)
    {
        var (service, _) = await Start(dockerSocket, socketExists, Local("local", "http://127.0.0.1:2375"), Tcp("proxy", "127.0.0.1"));

        Assert.Equal("http://127.0.0.1:2375", service.GetServer("local")!.SocketPath);
        Assert.Equal(ConnectionType.TCP, service.GetServer("proxy")!.ConnectionType);
    }

    [Fact]
    public void The_compose_file_and_the_app_name_the_same_socket()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx"))) dir = dir.Parent;
        var compose = File.ReadAllText(Path.Combine(dir!.FullName, "docker-compose.hardened.yml"));
        Assert.Contains("Docker__SocketPath=" + DockerSettings.HardenedProxySocketPath, compose);
    }
}
