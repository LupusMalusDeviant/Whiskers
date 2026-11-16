using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Vault;
using Whiskers.Services.Workloads.Kubernetes;

namespace Whiskers.Services.ServerConfig;

/// <summary>What a removal did beyond the record (contract 0018): whether the secrets were cleaned up, and the
/// standing-key warning when a bootstrap line stayed on an unreachable host.</summary>
public sealed record ServerRemovalResult(bool SecretsCleaned, string? StandingKeyWarning);

/// <summary>
/// The execution of <c>server.remove</c> (R1-11): the server record goes first, and only once that is written is a
/// Kubernetes server's kubeconfig removed from the vault. A failed removal therefore leaves a working server with its
/// kubeconfig; a failed cleanup leaves at most a secret that no server names. The kubeconfig belongs to the server,
/// so it is part of the one <c>server.remove</c> decision — never a second decision that could be refused halfway
/// (R1-48: <see cref="MutationCompositions"/> lists <c>server.remove</c> → <c>vault.secret_delete</c>, <c>host.exec</c>).
/// Contract 0018 (INV-64): for a record with a bootstrap line on its host, the fixed removal command runs on the host
/// first (<see cref="Whiskers.Services.Onboarding.IBootstrapCleanup"/>, 30 s); an unreachable host does not stop the
/// removal — the sealed key goes with the record, so the line left behind has no private key anywhere.
/// </summary>
[MutationHandler("server.remove")]
public static class ServerRemoval
{
    /// <summary>Removes <paramref name="server"/>; returns false when its kubeconfig or cloud API key could not be removed
    /// from the vault afterwards — including when the vault is disabled or locked and the server named such a secret
    /// (the secret then stays in vault.json, unused).</summary>
    public static async Task<bool> RemoveAsync(IServerConfigService servers, IVaultService vault, Models.ServerConfig server,
        Whiskers.Services.Certificates.IMtlsClientCredentialStore? clientCredentials = null,
        Whiskers.Services.Onboarding.IBootstrapCleanup? bootstrapCleanup = null) =>
        (await RemoveWithReportAsync(servers, vault, server, clientCredentials, bootstrapCleanup)).SecretsCleaned;

    /// <summary>As <see cref="RemoveAsync"/>, with the standing-key warning of a bootstrap line that could not be removed.</summary>
    public static async Task<ServerRemovalResult> RemoveWithReportAsync(IServerConfigService servers, IVaultService vault,
        Models.ServerConfig server, Whiskers.Services.Certificates.IMtlsClientCredentialStore? clientCredentials = null,
        Whiskers.Services.Onboarding.IBootstrapCleanup? bootstrapCleanup = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(server);

        // R1-44 (review r1 MINOR 8): never while a certificate step holds the host — a running migration would lose
        // its slot or journal. Refused before anything is removed.
        var gate = clientCredentials?.HostLock(server.Id);
        if (gate is not null && !await gate.WaitAsync(0))
            throw new InvalidOperationException($"A certificate operation for {server.Name} is running; remove the server once it has finished.");
        try
        {
            var warning = await RemoveBootstrapLineAsync(servers, server, bootstrapCleanup);
            return new ServerRemovalResult(await RemoveLockedAsync(servers, vault, server, clientCredentials), warning);
        }
        finally
        {
            gate?.Release();
        }
    }

    /// <summary>Contract 0018: before the record goes, the bootstrap lines of the server leave its host. Returns the
    /// standing-key warning (the dead line: fingerprint, host) when they could not be removed; null otherwise.</summary>
    private static async Task<string?> RemoveBootstrapLineAsync(IServerConfigService servers, Models.ServerConfig shown,
        Whiskers.Services.Onboarding.IBootstrapCleanup? cleanup)
    {
        var stored = servers.GetServer(shown.Id) is { } s && string.Equals(s.Id, shown.Id, StringComparison.Ordinal) ? s : shown;
        if (stored.BootstrapKeyInjectedFingerprint is not { } fingerprint) return null;
        Whiskers.Services.Onboarding.BootstrapCleanupResult result;
        if (cleanup is null) result = new(false, "no clean-up service");
        else
        {
            using var cts = new CancellationTokenSource(Whiskers.Services.Onboarding.BootstrapCleanup.Bound);
            try { result = await cleanup.RemoveMarkerLinesAsync(stored, cts.Token); }
            catch (Exception ex)   // contract 0018: whatever the clean-up throws, the removal goes on and names the line
            {
                result = new(false, $"host not reachable ({ex.GetType().Name})");
            }
        }
        if (result.Removed)
        {
            MutationScope.Note($"bootstrap line {fingerprint} removed from /root/.ssh/authorized_keys before the removal");
            return null;
        }
        var warning = Whiskers.Services.Onboarding.BootstrapCleanup.DeadLineWarning(fingerprint, stored.SshHost ?? stored.TcpHost);
        MutationScope.Note($"standingBootstrapKey: {warning} ({result.Error})");
        return warning;
    }

    private static async Task<bool> RemoveLockedAsync(IServerConfigService servers, IVaultService vault, Models.ServerConfig server,
        Whiskers.Services.Certificates.IMtlsClientCredentialStore? clientCredentials)
    {
        // The record as stored when it was removed: a page showing an older copy may name a kubeconfig that a
        // rotation has replaced meanwhile.
        var removed = await servers.TakeServerAsync(server.Id) ?? server;
        // R1-44: its own mTLS client key goes with it (nothing else uses it).
        var keyRemoved = true;
        if (clientCredentials is not null && Whiskers.Services.Certificates.MtlsClientCredentialStore.IsValidServerId(removed.Id))
        {
            try { await clientCredentials.RemoveAsync(removed.Id, CancellationToken.None); }
            catch (IOException) { keyRemoved = false; }
            catch (UnauthorizedAccessException) { keyRemoved = false; }
        }
        if (!vault.IsEnabled) return keyRemoved && removed.ConnectionType != ConnectionType.Kubernetes && removed.CloudKeyRef is null;
        var ok = keyRemoved;
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
