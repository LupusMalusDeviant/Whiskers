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
    /// Saves <paramref name="updated"/> over <paramref name="stored"/>. Every save of an existing record holds the
    /// per-host lock that onboarding and every certificate step hold, so a running onboarding never continues on
    /// another host; a busy lock refuses the save before anything is written. The lock is taken for saves that keep
    /// the connection too: the save writes the whole record, and a form read before another admin's connection change
    /// would otherwise write the old host back while an onboarding runs on the new one (Codex round 5).
    /// </summary>
    public static async Task<T> SaveAsync<T>(IMtlsClientCredentialStore? store, Models.ServerConfig? stored,
        Models.ServerConfig updated, Func<Task<T>> save)
    {
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentNullException.ThrowIfNull(save);
        if (stored is null)
            return await save();
        return await HoldHostAsync(store, stored.Id, stored.Name, save);
    }

    /// <summary>
    /// Runs <paramref name="write"/> holding the per-host lock of <paramref name="serverId"/>, or refuses before it runs
    /// while an onboarding or a certificate step holds it. For every write of a server record, such as an SSH key
    /// upload, which writes the whole record as well (Codex round 6).
    /// </summary>
    public static async Task<T> HoldHostAsync<T>(IMtlsClientCredentialStore? store, string serverId, string name,
        Func<Task<T>> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (store is null) return await write();
        var gate = store.HostLock(serverId);
        if (!await gate.WaitAsync(0))
            throw new InvalidOperationException(
                $"An onboarding or certificate step for {name} is running; save the server once it has finished.");
        try
        {
            return await write();
        }
        finally
        {
            gate.Release();
        }
    }
}
