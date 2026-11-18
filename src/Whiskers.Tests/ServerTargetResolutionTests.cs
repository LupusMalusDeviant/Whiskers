using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests;

/// <summary>Everything a human or an agent reads names a server by its DISPLAY NAME ("Host-D (Hetzner)"),
/// while every tool takes the id ("host-d"). Once alerts started arriving from remote hosts, an agent
/// acting on one passed the name, got "Server not found" and concluded the container did not exist — so the
/// lookup accepts both, with ids winning.</summary>
public sealed class ServerTargetResolutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"server-resolve-{Guid.NewGuid():N}");

    public ServerTargetResolutionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private async Task<ServerConfigService> ServiceWith(params Whiskers.Models.ServerConfig[] servers)
    {
        var svc = new ServerConfigService(
            Options.Create(new DockerSettings { SocketPath = "npipe://./pipe/docker_engine" }),
            NullLogger<ServerConfigService>.Instance,
            storePath: Path.Combine(_dir, $"servers-{Guid.NewGuid():N}.json"));

        await svc.InitializeAsync();
        foreach (var existing in svc.GetServers().ToList())
            if (servers.All(s => s.Id != existing.Id))
                await svc.RemoveServerAsync(existing.Id);
        foreach (var server in servers)
            if (svc.GetServers().All(s => s.Id != server.Id))
                await svc.AddServerAsync(server);

        return svc;
    }

    private static Whiskers.Models.ServerConfig Server(string id, string name, bool isDefault = false) =>
        new() { Id = id, Name = name, IsDefault = isDefault, ConnectionType = ConnectionType.TCP };

    [Fact]
    public async Task Resolves_by_id()
    {
        var svc = await ServiceWith(Server("local", "Host-A (local)", isDefault: true), Server("host-d", "Host-D (Hetzner)"));
        Assert.Equal("Host-D (Hetzner)", svc.GetServer("host-d")?.Name);
    }

    [Fact]
    public async Task Resolves_by_display_name()
    {
        var svc = await ServiceWith(Server("local", "Host-A (local)", isDefault: true), Server("host-d", "Host-D (Hetzner)"));
        Assert.Equal("host-d", svc.GetServer("Host-D (Hetzner)")?.Id);
    }

    [Fact]
    public async Task Resolution_is_case_insensitive()
    {
        var svc = await ServiceWith(Server("local", "Host-A (local)", isDefault: true), Server("host-d", "Host-D (Hetzner)"));
        Assert.Equal("host-d", svc.GetServer("HOST-D")?.Id);
        Assert.Equal("host-d", svc.GetServer("host-d (hetzner)")?.Id);
    }

    [Fact]
    public async Task An_id_wins_over_another_servers_name()
    {
        // Pathological but decidable: "host-d" is one server's id and another's name. The id wins, so a
        // tool call can never be silently redirected to a different host.
        var svc = await ServiceWith(
            Server("local", "Host-A (local)", isDefault: true),
            Server("host-d", "Host-D (Hetzner)"),
            Server("other", "host-d"));

        Assert.Equal("Host-D (Hetzner)", svc.GetServer("host-d")?.Name);
    }

    [Fact]
    public async Task An_unknown_target_still_resolves_to_nothing()
    {
        var svc = await ServiceWith(Server("local", "Host-A (local)", isDefault: true));
        Assert.Null(svc.GetServer("f7d10a7c-770e-4d1e-a09e-51a32246171a"));  // an id a model invented
        Assert.Null(svc.GetServer(""));
    }
}
