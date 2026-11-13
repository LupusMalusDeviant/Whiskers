namespace Whiskers.Services.Certificates;

/// <summary>
/// INV-59 (#187): what an onboarding decision is bound to on the host side. <see cref="Holds"/> says whether a server
/// already holds mTLS state that an onboarding would replace — TLS client material in its record, or anything in the
/// per-host credential store; whatever cannot be read counts as state (fail closed). <see cref="ConnectionOf"/> is the
/// identity of the host a record reaches, so an approval given for one host cannot be used on another after the record
/// was edited.
/// </summary>
public static class MtlsOnboardingState
{
    public static bool Holds(Models.ServerConfig server, IMtlsClientCredentialStore? store)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.TcpUseTls || !string.IsNullOrWhiteSpace(server.TcpClientCertPath)
            || !string.IsNullOrWhiteSpace(server.TcpClientKeyPath) || !string.IsNullOrWhiteSpace(server.TcpCaCertPath))
            return true;
        // Without a store (a build without per-host credentials) nothing is stored there.
        if (store is null) return false;
        try
        {
            return store.HoldsState(server.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>The fields that decide which host a record reaches (connection type, SSH and TCP endpoint, socket,
    /// Tailscale SSH, Kubernetes context), normalised. Names, groups, tags and credentials are not part of it.</summary>
    public static string ConnectionOf(Models.ServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);
        static string Host(string? value) => (value ?? "").Trim().ToLowerInvariant();
        return string.Join('\u001f',
            server.ConnectionType.ToString(), Host(server.SshHost), server.SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Host(server.TcpHost), server.TcpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            server.SocketPath ?? "", server.TailscaleSsh ? "ts" : "", server.KubeContext ?? "");
    }

    /// <summary>
    /// Saves <paramref name="updated"/> over <paramref name="stored"/>. When the save changes the connection
    /// (<see cref="ConnectionOf"/>), it holds the per-host lock that onboarding and every certificate step hold, so a
    /// running onboarding never continues on another host; a busy lock refuses the save before anything is written.
    /// </summary>
    public static async Task<T> SaveAsync<T>(IMtlsClientCredentialStore? store, Models.ServerConfig? stored,
        Models.ServerConfig updated, Func<Task<T>> save)
    {
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentNullException.ThrowIfNull(save);
        if (store is null || stored is null
            || string.Equals(ConnectionOf(stored), ConnectionOf(updated), StringComparison.Ordinal))
            return await save();
        var gate = store.HostLock(stored.Id);
        if (!await gate.WaitAsync(0))
            throw new InvalidOperationException(
                $"An onboarding or certificate step for {stored.Name} is running; change its connection once it has finished.");
        try
        {
            return await save();
        }
        finally
        {
            gate.Release();
        }
    }
}
