using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests;

/// <summary>V4 (kubernetesImplement Track A): the default "local" Docker server is only seeded on a
/// fresh install when it can exist — WHISKERS_DISABLE_LOCAL_DOCKER=true or a missing unix socket
/// (the Kubernetes pod case) starts with an empty fleet instead of a dead entry. Env-var tests run
/// sequentially within this class (xUnit runs a class's tests serially).</summary>
public sealed class DefaultLocalServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"local-default-{Guid.NewGuid():N}");

    public DefaultLocalServerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("WHISKERS_DISABLE_LOCAL_DOCKER", null);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ServerConfigService Service(string socketPath)
        => new(Options.Create(new DockerSettings { SocketPath = socketPath }),
            NullLogger<ServerConfigService>.Instance,
            storePath: Path.Combine(_dir, $"servers-{Guid.NewGuid():N}.json"));

    [Fact]
    public async Task Fresh_install_with_npipe_socket_keeps_the_historical_default()
    {
        // Windows named pipes can't be probed with File.Exists — historical behaviour is kept.
        var svc = Service("npipe://./pipe/docker_engine");
        await svc.InitializeAsync();
        var server = Assert.Single(svc.GetServers());
        Assert.Equal("local", server.Id);
    }

    [Fact]
    public async Task Fresh_install_without_the_unix_socket_starts_with_an_empty_fleet()
    {
        var svc = Service($"unix://{_dir.Replace('\\', '/')}/does-not-exist.sock");
        await svc.InitializeAsync();
        Assert.Empty(svc.GetServers());
        Assert.True(svc.IsInitialized);
    }

    [Fact]
    public async Task Fresh_install_with_an_existing_unix_socket_seeds_local()
    {
        // Any existing file stands in for the socket (File.Exists is the probe).
        var sockPath = Path.Combine(_dir, "docker.sock");
        await File.WriteAllTextAsync(sockPath, "");
        var svc = Service($"unix://{sockPath.Replace('\\', '/')}");
        await svc.InitializeAsync();
        Assert.Single(svc.GetServers());
    }

    [Fact]
    public async Task Disable_env_var_wins_even_when_a_socket_exists()
    {
        var sockPath = Path.Combine(_dir, "docker2.sock");
        await File.WriteAllTextAsync(sockPath, "");
        Environment.SetEnvironmentVariable("WHISKERS_DISABLE_LOCAL_DOCKER", "true");
        try
        {
            var svc = Service($"unix://{sockPath.Replace('\\', '/')}");
            await svc.InitializeAsync();
            Assert.Empty(svc.GetServers());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DISABLE_LOCAL_DOCKER", null);
        }
    }

    [Fact]
    public async Task Existing_store_is_never_touched()
    {
        // An existing servers.json (even with a local entry) is loaded as-is — the V4 gate only
        // applies to FRESH installs; upgrades keep their fleet.
        var store = Path.Combine(_dir, "servers-existing.json");
        // build the store via a first init...
        var first = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "npipe://./pipe/docker_engine" }),
            NullLogger<ServerConfigService>.Instance, storePath: store);
        await first.InitializeAsync();
        Assert.Single(first.GetServers());

        // ...then re-init with a config that would NOT seed (missing unix socket): entry survives.
        Environment.SetEnvironmentVariable("WHISKERS_DISABLE_LOCAL_DOCKER", "true");
        try
        {
            var second = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "unix:///nope.sock" }),
                NullLogger<ServerConfigService>.Instance, storePath: store);
            await second.InitializeAsync();
            Assert.Single(second.GetServers());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DISABLE_LOCAL_DOCKER", null);
        }
    }
}
