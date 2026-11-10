using Whiskers.Models;

namespace Whiskers.Services.ServerConfig;

/// <summary>Stores and serves the configured Docker hosts, plus their SSH key material.</summary>
public interface IServerConfigService
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>True once <see cref="InitializeAsync"/> has loaded (or seeded) the server registry.
    /// Backs the <c>/readyz</c> readiness probe.</summary>
    bool IsInitialized { get; }

    [Whiskers.Services.Mutations.InventoryLookup]
    List<Models.ServerConfig> GetServers();
    List<Models.ServerConfig> GetEnabledServers();
    Models.ServerConfig? GetServer(string serverId);
    Models.ServerConfig? GetDefaultServer();
    bool SupportsTerminal(string? serverId);
    [Whiskers.Services.Mutations.MutatingOperation("server.save")]
    Task AddServerAsync(Models.ServerConfig server);
    [Whiskers.Services.Mutations.MutatingOperation("server.save")]
    Task UpdateServerAsync(Models.ServerConfig server);
    [Whiskers.Services.Mutations.MutatingOperation("server.remove")]
    Task RemoveServerAsync([Whiskers.Services.Mutations.MutationTarget] string serverId);

    /// <summary>
    /// Updates a server and decides its kubeconfig reference in the same step (R1-11): <paramref name="newKubeconfigRef"/>
    /// when the save brings a new kubeconfig, otherwise the reference the server has when it is written — never the
    /// one an older copy carries. Returns the record that was replaced (null when there was none). The default is
    /// for test doubles; the store does both under its write lock.
    /// </summary>
    [Whiskers.Services.Mutations.MutatingOperation("server.save")]
    async Task<Models.ServerConfig?> UpdateServerAsync(Models.ServerConfig server, string? newKubeconfigRef)
    {
        var current = GetServer(server.Id);
        server.KubeconfigRef = newKubeconfigRef ?? current?.KubeconfigRef;
        await UpdateServerAsync(server);
        return current;
    }

    /// <summary>
    /// As <see cref="UpdateServerAsync(Models.ServerConfig, string?)"/>, and decides the cloud API key in the same step
    /// (R1-29, <see cref="CloudKeyChange"/>). The default is for test doubles; the store does it under its write lock.
    /// </summary>
    [Whiskers.Services.Mutations.MutatingOperation("server.save")]
    async Task<Models.ServerConfig?> UpdateServerAsync(Models.ServerConfig server, string? newKubeconfigRef, CloudKeyChange cloudKey)
    {
        cloudKey.ApplyTo(server, GetServer(server.Id));
        return await UpdateServerAsync(server, newKubeconfigRef);
    }

    /// <summary>
    /// Start-up migration (R1-29): replaces the legacy plaintext cloud API key of <paramref name="serverId"/> by the vault
    /// reference <paramref name="vaultRef"/> — only while the server still carries exactly <paramref name="plaintext"/>
    /// and no reference. Returns whether it was replaced. The default is for test doubles.
    /// </summary>
    /// <remarks>A designed exemption of INV-38 (R1-29): it runs before the gateway, is not marked, and
    /// <c>CredentialAdoptionCallersTests</c> pins the migration as its only caller. R1-48: it has no default body,
    /// because one would have to call the marked <c>server.save</c> write outside a decision.</remarks>
    Task<bool> AdoptCloudKeyRefAsync(string serverId, string plaintext, string vaultRef);

    /// <summary>Removes a server and returns the record that was removed, as stored at that moment (R1-11): its
    /// kubeconfig reference is the one to clean up, not that of an older copy. The default is for test doubles.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("server.remove")]
    async Task<Models.ServerConfig?> TakeServerAsync([Whiskers.Services.Mutations.MutationTarget] string serverId)
    {
        var current = GetServer(serverId);
        await RemoveServerAsync(serverId);
        return current;
    }
    [Whiskers.Services.Mutations.MutatingOperation("server.save")]
    Task SaveSshKeyAsync(string serverId, string fileName, byte[] keyData);
    string? GetSshKeyPath(Models.ServerConfig server);

    /// <summary>Deletes the stored SSH key for a server and clears its key reference — used after a
    /// successful onboarding so no standing bootstrap key remains on disk.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("server.save")]
    Task DeleteSshKeyAsync(string serverId);
}
