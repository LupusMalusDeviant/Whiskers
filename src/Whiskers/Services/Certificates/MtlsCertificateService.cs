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
}

public sealed record MtlsRenewalResult(bool Success, string Message, DateTime? NotAfterUtc = null);

public sealed class MtlsCertificateService : IMtlsCertificateService
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly IHostCommandExecutor _exec;
    private readonly IServerConfigService _servers;
    private readonly ILogger<MtlsCertificateService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    // Seams for tests: the real handshake and the real wait for ghostunnel to come back.
    internal Func<string, int, CancellationToken, Task<X509Certificate2?>> FetchPresented { get; set; }
    internal TimeSpan RestartGrace { get; set; } = TimeSpan.FromSeconds(8);
    internal int ConfirmAttempts { get; set; } = 6;
    internal TimeSpan ConfirmInterval { get; set; } = TimeSpan.FromSeconds(5);

    public MtlsCertificateService(IHostCommandExecutor exec, IServerConfigService servers, ILogger<MtlsCertificateService> logger)
    {
        _exec = exec;
        _servers = servers;
        _logger = logger;
        FetchPresented = (host, port, ct) => CertificateProbe.FetchServerCertificateAsync(host, port, HandshakeTimeout, ct);
    }

    public async Task<X509Certificate2> IssueForNewHostAsync(string serverId, MtlsIdentity identity, CancellationToken ct)
    {
        var cert = await IssueAndStageAsync(serverId, identity, ct);
        await RunAsync(serverId, MtlsCertificateCommands.ActivateFresh(), ct);
        return cert;
    }

    public async Task<MtlsRenewalResult> RenewAsync(string serverId, CancellationToken ct)
    {
        var server = _servers.GetServer(serverId);
        if (server is null) return new(false, $"Server '{serverId}' not found.");
        if (!server.TcpUseTls || string.IsNullOrWhiteSpace(server.TcpHost))
            return new(false, $"{server.Name} is not connected over mTLS — there is no proxy certificate to renew.");

        var gate = _locks.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
            return new(false, $"A renewal for {server.Name} is already running.");
        try
        {
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
