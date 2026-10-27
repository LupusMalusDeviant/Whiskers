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
    /// <summary>Removes <paramref name="server"/>; returns false when its kubeconfig could not be removed afterwards.</summary>
    public static async Task<bool> RemoveAsync(IServerConfigService servers, IVaultService vault, Models.ServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(server);

        await servers.RemoveServerAsync(server.Id);
        if (server.ConnectionType != ConnectionType.Kubernetes || !vault.IsEnabled) return true;
        try
        {
            await vault.DeleteSecretAsync(KubernetesClientCache.KubeconfigVaultKey(server.Id));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;   // the server is gone; its kubeconfig is an orphan nobody uses
        }
    }
}
