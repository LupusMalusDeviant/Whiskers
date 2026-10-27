using Whiskers.Models;

namespace Whiskers.Services.ServerConfig;

/// <summary>Stores and serves the configured Docker hosts, plus their SSH key material.</summary>
public interface IServerConfigService
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>True once <see cref="InitializeAsync"/> has loaded (or seeded) the server registry.
    /// Backs the <c>/readyz</c> readiness probe.</summary>
    bool IsInitialized { get; }

    List<Models.ServerConfig> GetServers();
    List<Models.ServerConfig> GetEnabledServers();
    Models.ServerConfig? GetServer(string serverId);
    Models.ServerConfig? GetDefaultServer();
    bool SupportsTerminal(string? serverId);
    Task AddServerAsync(Models.ServerConfig server);
    Task UpdateServerAsync(Models.ServerConfig server);
    Task RemoveServerAsync(string serverId);

    /// <summary>
    /// Updates a server and decides its kubeconfig reference in the same step (R1-11): <paramref name="newKubeconfigRef"/>
    /// when the save brings a new kubeconfig, otherwise the reference the server has when it is written — never the
    /// one an older copy carries. Returns the record that was replaced (null when there was none). The default is
    /// for test doubles; the store does both under its write lock.
    /// </summary>
    async Task<Models.ServerConfig?> UpdateServerAsync(Models.ServerConfig server, string? newKubeconfigRef)
    {
        var current = GetServer(server.Id);
        server.KubeconfigRef = newKubeconfigRef ?? current?.KubeconfigRef;
        await UpdateServerAsync(server);
        return current;
    }

    /// <summary>Removes a server and returns the record that was removed, as stored at that moment (R1-11): its
    /// kubeconfig reference is the one to clean up, not that of an older copy. The default is for test doubles.</summary>
    async Task<Models.ServerConfig?> TakeServerAsync(string serverId)
    {
        var current = GetServer(serverId);
        await RemoveServerAsync(serverId);
        return current;
    }
    Task SaveSshKeyAsync(string serverId, string fileName, byte[] keyData);
    string? GetSshKeyPath(Models.ServerConfig server);

    /// <summary>Deletes the stored SSH key for a server and clears its key reference — used after a
    /// successful onboarding so no standing bootstrap key remains on disk.</summary>
    Task DeleteSshKeyAsync(string serverId);
}
