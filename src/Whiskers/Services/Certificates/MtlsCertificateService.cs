using Whiskers.Services.Mutations;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Certificates;

/// <summary>Issues and renews the certificate of a host's mTLS proxy. The private key is generated on the host and
/// never leaves it; only the CSR and the signed certificate travel.</summary>
public interface IMtlsCertificateService
{
    /// <summary>Onboarding: key + CSR on the host, signed by step-ca, installed before the proxy first starts.</summary>
    [MutatingOperation("certificate.issue_mtls")]
    Task<X509Certificate2> IssueForNewHostAsync(string serverId, MtlsIdentity identity, CancellationToken ct);

    /// <summary>Renews the certificate of a running proxy, keeping its identity. The host rolls back on its own if
    /// Whiskers cannot confirm the new certificate within <see cref="MtlsCertificateCommands.GuardDelay"/>.</summary>
    [MutatingOperation("certificate.renew_mtls")]
    Task<MtlsRenewalResult> RenewAsync(string serverId, CancellationToken ct);

    /// <summary>Moves one host from the shared to its own client identity (R1-44, contract 0007): issue, widen, switch,
    /// narrow, record. Every host step is guarded and confirmed through the new credential; a failed phase leaves the host
    /// in its last confirmed state, and a second call resumes from there. Admin, capability <c>mtls.client_migrate</c>.</summary>
    [MutatingOperation("mtls.client_migrate")]
    Task<MtlsClientMigrationResult> MigrateClientAsync(string serverId, IProgress<string>? progress, CancellationToken ct);

    /// <summary>A new client certificate for a migrated host: the same generation (<paramref name="rotate"/> false, the
    /// host is unchanged), or the next one (true: widen, switch, narrow — the old certificate is dead at that host
    /// afterwards). Capability <c>mtls.client_renew</c>.</summary>
    [MutatingOperation("mtls.client_renew")]
    Task<MtlsClientMigrationResult> RenewClientAsync(string serverId, bool rotate, CancellationToken ct);

    /// <summary>Deletes the shared <c>client.key</c> / <c>client.crt</c> once every mTLS host has been narrowed to its own
    /// identity with the shared credential proven refused. UI only, Admin, capability <c>mtls.shared_client_retire</c>.</summary>
    [MutatingOperation("mtls.shared_client_retire")]
    Task<MtlsRenewalResult> RetireSharedClientAsync(CancellationToken ct);

    /// <summary>Read only: whether ghostunnel runs on <paramref name="serverId"/> and which <c>--allow-cn</c> values it
    /// runs with (<c>docker inspect</c>). Never throws for an unreachable host; the result says so.</summary>
    Task<MtlsLiveAllowList> ReadLiveAllowListAsync(string serverId, CancellationToken ct);
}

/// <summary>What a host's ghostunnel runs with. <see cref="Readable"/> false: the host or its <c>.Args</c> could not be
/// read (<see cref="Error"/>); <see cref="Running"/> false: the container exists but is stopped.</summary>
public sealed record MtlsLiveAllowList(bool Readable, bool Running, IReadOnlyList<string> AllowedCommonNames, string? Error);

public sealed record MtlsRenewalResult(bool Success, string Message, DateTime? NotAfterUtc = null);

/// <summary>The outcome of a client-credential rollout step: <see cref="Phase"/> names the phase that ended the run
/// (the failed one, or "done").</summary>
public sealed record MtlsClientMigrationResult(bool Ok, string Phase, string? Error, MtlsClientIdentity? Identity);

public sealed partial class MtlsCertificateService : IMtlsCertificateService
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly IHostCommandExecutor _exec;
    private readonly IServerConfigService _servers;
    private readonly ILogger<MtlsCertificateService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>The one per-host lock of every certificate step (server certificate, client rollout) — the store's, so
    /// that server removal takes the same one (review r1 MINOR 8); a local one only without a store.</summary>
    private SemaphoreSlim HostGate(string serverId) =>
        _clientCredentials?.HostLock(serverId) ?? _locks.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));

    // Seams for tests: the real handshake and the real wait for ghostunnel to come back.
    internal Func<string, int, CancellationToken, Task<X509Certificate2?>> FetchPresented { get; set; }
    internal TimeSpan RestartGrace { get; set; } = TimeSpan.FromSeconds(8);
    internal int ConfirmAttempts { get; set; } = 6;
    internal TimeSpan ConfirmInterval { get; set; } = TimeSpan.FromSeconds(5);

    public MtlsCertificateService(IHostCommandExecutor exec, IServerConfigService servers, ILogger<MtlsCertificateService> logger,
        IMtlsClientCredentialStore? clientCredentials = null, Whiskers.Services.Docker.IDockerConnectionManager? connections = null)
    {
        _exec = exec;
        _servers = servers;
        _logger = logger;
        _clientCredentials = clientCredentials;
        _connections = connections;
        FetchPresented = (host, port, ct) => CertificateProbe.FetchServerCertificateAsync(host, port, HandshakeTimeout, ct);
        ProbeCredential = (server, ct) => MtlsClientProbe.ProbeAsync(server, HandshakeTimeout, ct);
        RunConfirmation = (server, script, ct) => MtlsClientProbe.RunConfirmationAsync(server, script, StepTimeout, ct);
        VerifyConnection = DefaultVerifyConnectionAsync;
    }

    /// <summary>
    /// The server certificate of a host being onboarded. With a client-credential store, the caller (onboarding) is
    /// expected to hold the store's <see cref="IMtlsClientCredentialStore.HostLock"/> for the whole issue (contract 0007,
    /// amendment 10; review r3 MAJOR 1). The check here is only a backstop: it refuses when the lock is free, but a
    /// semaphore cannot tell whether the caller or another step holds it. Without a store it takes the local per-host
    /// lock itself.
    /// </summary>
    public async Task<X509Certificate2> IssueForNewHostAsync(string serverId, MtlsIdentity identity, CancellationToken ct)
    {
        SemaphoreSlim? own = null;
        if (_clientCredentials is { } store)
        {
            if (store.HostLock(serverId).CurrentCount != 0)
                throw new InvalidOperationException("The server certificate of a new host is issued only under the host's certificate lock.");
        }
        else
        {
            own = HostGate(serverId);
            if (!await own.WaitAsync(0, ct))
                throw new InvalidOperationException("A certificate operation for this server is running.");
        }
        try
        {
            var cert = await IssueAndStageAsync(serverId, identity, ct);
            await RunAsync(serverId, MtlsCertificateCommands.ActivateFresh(), ct);
            return cert;
        }
        finally
        {
            own?.Release();
        }
    }

    public async Task<MtlsRenewalResult> RenewAsync(string serverId, CancellationToken ct)
    {
        var server = _servers.GetServer(serverId);
        if (server is null) return new(false, $"Server '{serverId}' not found.");
        if (!server.TcpUseTls || string.IsNullOrWhiteSpace(server.TcpHost))
            return new(false, $"{server.Name} is not connected over mTLS — there is no proxy certificate to renew.");

        var gate = HostGate(serverId);
        if (!await gate.WaitAsync(0, ct))
            return new(false, $"A renewal for {server.Name} is already running.");
        try
        {
            // No step while any Whiskers guard is armed on the host (amendment 1; review r1 NIT): an allow-list run of
            // another Whiskers process, or an earlier renewal, still waits for its confirmation. Unknown counts as armed.
            var guards = await RunAsync(serverId, MtlsAllowListCommands.ListGuardUnits(), ct);
            if (!string.IsNullOrWhiteSpace(guards.Output))
                return new(false, $"A Whiskers guard timer is armed on {server.Name} ({guards.Output.Trim()}); try again once it has fired, at most 3 minutes from its start.");

            // Keep the identity the proxy has today: its name was chosen at onboarding and need not match the
            // display name any more, and Whiskers checks the IP SAN against TcpHost on every connection.
            using var current = await FetchPresented(server.TcpHost, server.TcpPort, ct)
                ?? throw new InvalidOperationException($"{server.Name} presented no certificate.");
            var identity = IdentityOf(current);
            if (!identity.IpAddresses.Contains(server.TcpHost))
                return new(false, $"The current certificate of {server.Name} does not name {server.TcpHost}; renew it by hand.");

            using var issued = await IssueAndStageAsync(serverId, identity, ct);

            var suffix = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            await RunAsync(serverId, MtlsCertificateCommands.ActivateWithGuard(suffix), ct);
            _logger.LogInformation("Renewed proxy certificate staged on {Server}; waiting for ghostunnel to restart", server.Name);

            // From here on, do not let a cancelled caller strand the host: the guard rolls back on its own, and a
            // confirmation that still gets through is always the better outcome.
            return await ConfirmAsync(server, issued);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Proxy certificate renewal failed on {Server}", server.Name);
            return new(false, $"Renewal failed for {server.Name}: {ex.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MtlsRenewalResult> ConfirmAsync(Models.ServerConfig server, X509Certificate2 issued)
    {
        await Task.Delay(RestartGrace);
        for (var attempt = 0; attempt < ConfirmAttempts; attempt++)
        {
            try
            {
                using var presented = await FetchPresented(server.TcpHost!, server.TcpPort, CancellationToken.None);
                if (presented?.Thumbprint == issued.Thumbprint)
                {
                    var r = await _exec.ExecuteAsync(server.Id, MtlsCertificateCommands.Confirm(), StepTimeout);
                    if (r.Success)
                        return new(true, $"Proxy certificate of {server.Name} renewed; valid until {issued.NotAfter.ToUniversalTime():yyyy-MM-dd} (UTC).",
                            issued.NotAfter.ToUniversalTime());
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Confirmation attempt {Attempt} on {Server} failed: {Error}", attempt + 1, server.Name, ex.Message);
            }
            await Task.Delay(ConfirmInterval);
        }

        var rollbackAt = DateTime.UtcNow + MtlsCertificateCommands.GuardDelay;
        return new(false, $"The new certificate of {server.Name} could not be confirmed. The host restores the previous " +
                          $"certificate on its own by about {rollbackAt:HH:mm} UTC; check reachability after that.");
    }

    private async Task<X509Certificate2> IssueAndStageAsync(string serverId, MtlsIdentity identity, CancellationToken ct)
    {
        var csrB64 = (await RunAsync(serverId, MtlsCertificateCommands.GenerateKeyAndCsr(identity), ct)).Output.Trim();
        var csrKey = MtlsCertificateValidator.ValidateCsr(FromB64(csrB64, "CSR"), identity);

        var bundleB64 = (await RunAsync("local", MtlsCertificateCommands.SignCsr(csrB64, identity.CommonName), ct)).Output.Trim();
        var caB64 = (await RunAsync("local", MtlsCertificateCommands.ReadCaBundle(), ct)).Output.Trim();
        var issued = MtlsCertificateValidator.ValidateIssued(FromB64(bundleB64, "certificate"), FromB64(caB64, "CA bundle"), csrKey, identity);

        await RunAsync(serverId, MtlsCertificateCommands.StageCertificate(bundleB64, caB64), ct);
        return issued;
    }

    internal static MtlsIdentity IdentityOf(X509Certificate2 cert)
    {
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault();
        return new MtlsIdentity(
            cert.GetNameInfo(X509NameType.SimpleName, false),
            san?.EnumerateDnsNames() ?? [],
            san?.EnumerateIPAddresses().Select(i => i.ToString()) ?? []);
    }

    private async Task<CommandResult> RunAsync(string serverId, string command, CancellationToken ct)
    {
        var r = await _exec.ExecuteAsync(serverId, command, StepTimeout, ct);
        if (!r.Success)
            throw new InvalidOperationException($"step on {serverId} failed (exit {r.ExitCode}): {Trim(r.Error)}{Trim(r.Output)}");
        return r;
    }

    private static string FromB64(string b64, string what)
    {
        try { return Encoding.ASCII.GetString(Convert.FromBase64String(b64)); }
        catch (FormatException) { throw new InvalidOperationException($"The {what} did not come back as base64."); }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
