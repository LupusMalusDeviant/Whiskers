using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Vault;
using Whiskers.Services.Workloads.Kubernetes;

namespace Whiskers.Services.ServerConfig;

/// <summary>
/// The execution of <c>server.remove</c> (R1-11): the server record goes first, and only once that is written is a
/// Kubernetes server's kubeconfig removed from the vault. A failed removal therefore leaves a working server with its
/// kubeconfig; a failed cleanup leaves at most a secret that no server names. The kubeconfig belongs to the server,
/// so it is part of the one <c>server.remove</c> decision — never a second decision that could be refused halfway.
/// </summary>
[MutationHandler]
public static class ServerRemoval
{
    /// <summary>Removes <paramref name="server"/>; returns false when its kubeconfig or cloud API key could not be removed
    /// from the vault afterwards — including when the vault is disabled or locked and the server named such a secret
    /// (the secret then stays in vault.json, unused).</summary>
    public static async Task<bool> RemoveAsync(IServerConfigService servers, IVaultService vault, Models.ServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(server);

        // The record as stored when it was removed: a page showing an older copy may name a kubeconfig that a
        // rotation has replaced meanwhile.
        var removed = await servers.TakeServerAsync(server.Id) ?? server;
        if (!vault.IsEnabled) return removed.ConnectionType != ConnectionType.Kubernetes && removed.CloudKeyRef is null;
        var ok = true;
        // Its kubeconfig and its cloud API key (R1-29) live in the vault: remove them with it, unless another server
        // still names the same key.
        if (removed.ConnectionType == ConnectionType.Kubernetes)
            ok &= await TryDeleteAsync(vault, KubernetesClientCache.KubeconfigKeyOf(removed));
        if (removed.CloudKeyRef is { } cloudRef && !servers.GetServers().Any(s => s.CloudKeyRef == cloudRef))
            ok &= await TryDeleteAsync(vault, cloudRef);
        return ok;
    }

    private static async Task<bool> TryDeleteAsync(IVaultService vault, string key)
    {
        try
        {
            await vault.DeleteSecretAsync(key);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;   // the server is gone; its secret is an orphan nobody uses
        }
    }
}
