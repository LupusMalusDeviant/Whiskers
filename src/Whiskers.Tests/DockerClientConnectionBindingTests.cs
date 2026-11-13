using System.Runtime.CompilerServices;
using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Docker.Budget;
using Whiskers.Services.Observability;

namespace Whiskers.Tests;

/// <summary>
/// INV-59 (#187, Codex round 5): a cached Docker client belongs to the host its record reached when the client was
/// built. An approval for an onboarding is bound to the host the record reaches now; a client for the record's earlier
/// host must never carry that onboarding's commands.
/// </summary>
public class DockerClientConnectionBindingTests
{
    private sealed class NoTunnels : ISshTunnelManager
    {
        public Task<int> EstablishTunnelAsync(ServerConfig server) => throw new InvalidOperationException("not used");
        public void CloseTunnel(string serverId) { }
        public bool IsTunnelActive(string serverId) => true;
        public void Dispose() { }
    }

    /// <summary>Builds distinct stand-in clients (the test project cannot load a real Docker client) and remembers
    /// which host each was built for. A build for an SSH record waits for <see cref="SshTunnel"/>.</summary>
    private sealed class Builds
    {
        public Dictionary<DockerClient, string> BuiltFor { get; } = new(ReferenceEqualityComparer.Instance);
        public List<DockerClient> Disposed { get; } = [];
        public TaskCompletionSource SshTunnel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SshRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DockerClient> Build(ServerConfig server)
        {
            var host = server.ConnectionType == ConnectionType.SSH ? server.SshHost! : server.TcpHost!;
            if (server.ConnectionType == ConnectionType.SSH)
            {
                SshRequested.TrySetResult();
                await SshTunnel.Task;
            }
            var client = (DockerClient)RuntimeHelpers.GetUninitializedObject(typeof(DockerClient));
            lock (BuiltFor) BuiltFor[client] = host;
            return client;
        }

        public void Dispose(DockerClient client)
        {
            lock (Disposed) Disposed.Add(client);
        }
    }

    private static DockerConnectionManager Manager(Whiskers.Services.ServerConfig.IServerConfigService servers, Builds builds)
    {
        var options = new StaticOptionsMonitor<ServerBudgetSettings>(new ServerBudgetSettings());
        return new DockerConnectionManager(
            servers,
            new NoTunnels(),
            new ServerBudget(options, NullLogger<ServerBudget>.Instance),
            new ServerCircuitBreaker(options, new ServiceCollection().BuildServiceProvider(), NullLogger<ServerCircuitBreaker>.Instance),
            new LoopSuspensionService(new FakeNotifications(), servers, NullLogger<LoopSuspensionService>.Instance, new NoOutcomes()),
            NullLogger<DockerConnectionManager>.Instance)
        {
            BuildClientForTests = builds.Build,
            DisposeClientForTests = builds.Dispose
        };
    }

    private static ServerConfig Tcp(string id, string host) => new()
    {
        Id = id, Name = id, Enabled = true, ConnectionType = ConnectionType.TCP, TcpHost = host, TcpPort = 2376
    };

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_client_built_for_the_old_host_is_never_handed_out_after_the_record_moved()
    {
        // A background poll builds an SSH client for host A and waits for its tunnel. The record is moved to TCP host B
        // meanwhile; the edit's invalidation finds no client to drop. When A's build completes, its client must not be
        // published, and the next caller (the onboarding approved for B) gets a client for B.
        var servers = new MutableServerConfig(new ServerConfig
        {
            Id = "neuhof", Name = "neuhof", Enabled = true, ConnectionType = ConnectionType.SSH, SshHost = "100.64.0.20"
        });
        var builds = new Builds();
        using var docker = Manager(servers, builds);

        var building = docker.GetClientAsync("neuhof");
        await builds.SshRequested.Task.WaitAsync(Wait);

        await servers.UpdateServerAsync(Tcp("neuhof", "10.0.0.66"));
        docker.InvalidateClient("neuhof");
        builds.SshTunnel.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => building.WaitAsync(Wait));
        var client = await docker.GetClientAsync("neuhof").WaitAsync(Wait);
        Assert.Equal("10.0.0.66", builds.BuiltFor[client]);
        var stale = Assert.Single(builds.BuiltFor, b => b.Value == "100.64.0.20").Key;
        Assert.Contains(stale, builds.Disposed);   // A's client was dropped, not kept somewhere
    }

    [Fact]
    public async Task A_cached_client_is_not_used_for_a_record_that_now_reaches_another_host()
    {
        var servers = new MutableServerConfig(Tcp("neuhof", "100.64.0.20"));
        var builds = new Builds();
        using var docker = Manager(servers, builds);
        var before = await docker.GetClientAsync("neuhof");
        Assert.Same(before, await docker.GetClientAsync("neuhof"));   // the cache still works for an unchanged record

        // Moved without anyone invalidating the client (another path than the Servers page).
        await servers.UpdateServerAsync(Tcp("neuhof", "10.0.0.66"));

        var after = await docker.GetClientAsync("neuhof");
        Assert.Equal("10.0.0.66", builds.BuiltFor[after]);
        Assert.Contains(before, builds.Disposed);
    }

    /// <summary>A registry whose next read answers with a record from before a change: a caller that read the record,
    /// then waited for the build lock while the record moved and another caller built the new client.</summary>
    private sealed class StaleRead(MutableServerConfig inner) : Whiskers.Services.ServerConfig.IServerConfigService
    {
        public ServerConfig? Stale { get; set; }
        public bool IsInitialized => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<ServerConfig> GetServers() => inner.GetServers();
        public List<ServerConfig> GetEnabledServers() => inner.GetEnabledServers();
        public ServerConfig? GetServer(string serverId)
        {
            var stale = Stale;
            Stale = null;
            return stale ?? inner.GetServer(serverId);
        }
        public ServerConfig? GetDefaultServer() => inner.GetDefaultServer();
        public bool SupportsTerminal(string? serverId) => false;
        public Task AddServerAsync(ServerConfig server) => inner.AddServerAsync(server);
        public Task UpdateServerAsync(ServerConfig server) => inner.UpdateServerAsync(server);
        public Task RemoveServerAsync(string serverId) => inner.RemoveServerAsync(serverId);
        public Task SaveSshKeyAsync(string serverId, string fileName, byte[] keyData) => Task.CompletedTask;
        public string? GetSshKeyPath(ServerConfig server) => null;
        public Task DeleteSshKeyAsync(string serverId) => Task.CompletedTask;
        public Task<bool> AdoptCloudKeyRefAsync(string serverId, string plaintext, string vaultRef) => Task.FromResult(false);
    }

    [Fact]
    public async Task A_caller_that_read_the_old_host_keeps_the_client_built_for_the_new_one()
    {
        // Codex round 6: a caller read host A, then waited for the build lock while the record moved to B and another
        // caller built B's client. It must hand out B's client, not drop it and build one for A.
        var inner = new MutableServerConfig(Tcp("neuhof", "10.0.0.66"));
        var servers = new StaleRead(inner);
        var builds = new Builds();
        using var docker = Manager(servers, builds);
        var current = await docker.GetClientAsync("neuhof");

        servers.Stale = Tcp("neuhof", "100.64.0.20");
        var handed = await docker.GetClientAsync("neuhof").WaitAsync(Wait);

        Assert.Same(current, handed);
        Assert.DoesNotContain(current, builds.Disposed);
        Assert.Single(builds.BuiltFor);   // nothing was built for the earlier host
    }
}
