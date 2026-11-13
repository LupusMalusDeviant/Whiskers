namespace Whiskers.Services.Certificates;

/// <summary>
/// INV-59 (#187): whether a server already holds mTLS state that an onboarding would replace — TLS client material in
/// its record, or anything in the per-host credential store. One answer for the gateway's decision and for the
/// onboarding's re-check under the host lock. Whatever cannot be read counts as state (fail closed).
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
}
