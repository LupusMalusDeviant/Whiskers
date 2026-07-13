using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests;

/// <summary>F9 (server groups &amp; tags): the optional Group + Tags on <see cref="ServerConfig"/> must
/// survive persistence to servers.json and the edit-dialog Clone(), so filtering a fleet by group/tag
/// stays stable across restarts and edits.</summary>
public sealed class ServerOrganizationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"server-org-{Guid.NewGuid():N}");

    public ServerOrganizationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private ServerConfigService Service(string store)
        => new(Options.Create(new DockerSettings { SocketPath = "npipe://./pipe/docker_engine" }),
            NullLogger<ServerConfigService>.Instance, storePath: store);

    [Fact]
    public async Task Group_and_tags_survive_a_persist_and_reload()
    {
        var store = Path.Combine(_dir, "servers.json");
        var svc = Service(store);
        await svc.InitializeAsync();

        await svc.AddServerAsync(new ServerConfig
        {
            Name = "prod-eu-1",
            ConnectionType = ConnectionType.SSH,
            SshHost = "10.0.0.1",
            Group = "Production",
            Tags = new List<string> { "eu", "database" },
        });

        // A fresh service instance over the same file reloads from disk — proving it was persisted.
        var reloaded = Service(store);
        await reloaded.InitializeAsync();
        var server = Assert.Single(reloaded.GetServers(), s => s.Name == "prod-eu-1");
        Assert.Equal("Production", server.Group);
        Assert.Equal(new[] { "eu", "database" }, server.Tags);
    }

    [Fact]
    public void Clone_deep_copies_tags_so_the_edit_dialog_cannot_mutate_the_live_config()
    {
        var original = new ServerConfig { Name = "x", Tags = new List<string> { "a", "b" } };
        var copy = original.Clone();

        copy.Tags.Add("c");           // the dialog edits the clone...
        copy.Group = "Staging";

        Assert.Equal(new[] { "a", "b" }, original.Tags);   // ...the live config is untouched
        Assert.Null(original.Group);
    }
}
