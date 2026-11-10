using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Docker.DotNet;
using Microsoft.Extensions.Caching.Memory;
using Whiskers.Services.Docker;

namespace Whiskers.Services.Certificates;

/// <summary>What a probe with one client credential found out.</summary>
public enum MtlsProbeOutcome
{
    /// <summary>The host's proxy admitted the credential and answered <c>GET /_ping</c> with 200.</summary>
    Admitted,
    /// <summary>The proxy refused the credential with a TLS alert (with TLS 1.3 it arrives on the first read).</summary>
    Refused,
    /// <summary>Nothing was proven either way: a connect timeout, a reset, an unexpected answer, or Whiskers did not
    /// trust the server's certificate.</summary>
    NotChecked,
}

/// <summary>
/// Proves one client credential against one host (R1-44) over a one-off connection built from exactly the credential under
/// test. The server is validated with the very check every real connection uses
/// (<see cref="DockerConnectionManager.ValidateMtlsServerCert"/>: chain to the configured CA, the host's IP or name);
/// nothing is cached and the connection pool is not touched.
/// </summary>
public static partial class MtlsClientProbe
{
    private static readonly byte[] PingRequest = Encoding.ASCII.GetBytes("GET /_ping HTTP/1.1\r\nHost: docker\r\nConnection: close\r\n\r\n");

    /// <summary>Connects with the client credential named by <paramref name="server"/>'s paths and pings Docker through
    /// the host's ghostunnel.</summary>
    public static async Task<MtlsProbeOutcome> ProbeAsync(Models.ServerConfig server, IMtlsClientKeyLoader keyLoader, TimeSpan timeout,
        CancellationToken ct)
    {
        if (!server.TcpUseTls || string.IsNullOrWhiteSpace(server.TcpHost) || string.IsNullOrWhiteSpace(server.TcpClientCertPath)
            || string.IsNullOrWhiteSpace(server.TcpClientKeyPath) || string.IsNullOrWhiteSpace(server.TcpCaCertPath))
            return MtlsProbeOutcome.NotChecked;

        X509Certificate2 clientCert;
        var anchors = new X509Certificate2Collection();
        try
        {
            // Contract 0015 [mTLS layer]: the key is opened from its sealed file in memory.
            clientCert = keyLoader.Load(server);
            anchors.ImportFromPemFile(server.TcpCaCertPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
                                       or Whiskers.Services.Vault.SealedKeyException)
        {
            return MtlsProbeOutcome.NotChecked;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var serverRejected = false;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(server.TcpHost, server.TcpPort, cts.Token);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = server.TcpHost,
                ClientCertificates = new X509CertificateCollection { clientCert },
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                {
                    var ok = ValidateServer(cert, chain, anchors, server.TcpHost);
                    serverRejected = !ok;
                    return ok;
                },
            };
            await ssl.AuthenticateAsClientAsync(options, cts.Token);
            await ssl.WriteAsync(PingRequest, cts.Token);
            await ssl.FlushAsync(cts.Token);

            var buffer = new byte[64];
            var read = 0;
            while (read < 12)
            {
                var n = await ssl.ReadAsync(buffer.AsMemory(read), cts.Token);
                if (n == 0) break;
                read += n;
            }
            var status = Encoding.ASCII.GetString(buffer, 0, read);
            return status.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) ? MtlsProbeOutcome.Admitted : MtlsProbeOutcome.NotChecked;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Whiskers' own refusal of the server certificate proves nothing about the host's allow-list.
            return serverRejected ? MtlsProbeOutcome.NotChecked : Classify(ex);
        }
        finally
        {
            clientCert.Dispose();
            foreach (var anchor in anchors) anchor.Dispose();
        }
    }

    /// <summary>
    /// Classifies a failed probe — narrowly (contract 0007 MI-8, review r1 MINOR 2): <see cref="MtlsProbeOutcome.Refused"/>
    /// only when the chain carries evidence of a received TLS alert — OpenSSL's alert reason
    /// (<c>sslv3 alert …</c>, <c>tlsv1 alert …</c>, <c>tlsv13 alert …</c>, <c>SSL_R_*_ALERT_*</c>) or an SChannel status
    /// that a received alert maps to (<see cref="AlertStatusCodes"/>). Anything else is
    /// <see cref="MtlsProbeOutcome.NotChecked"/>: a timeout, a cancellation, a socket error such as a reset, an end of
    /// stream (OpenSSL 3's "unexpected eof while reading" included), and a bare TLS failure without an alert.
    /// </summary>
    internal static MtlsProbeOutcome Classify(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException or TimeoutException or SocketException) return MtlsProbeOutcome.NotChecked;
        }
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is System.ComponentModel.Win32Exception w && AlertStatusCodes.Contains(unchecked((uint)w.NativeErrorCode)))
                return MtlsProbeOutcome.Refused;
            if (AlertMessage().IsMatch(e.Message)) return MtlsProbeOutcome.Refused;
        }
        return MtlsProbeOutcome.NotChecked;
    }

    /// <summary>SChannel statuses for a received alert: <c>SEC_E_UNTRUSTED_ROOT</c> (unknown_ca),
    /// <c>SEC_E_ILLEGAL_MESSAGE</c> (handshake_failure, certificate_required), <c>SEC_E_CERT_UNKNOWN</c>
    /// (bad_certificate, certificate_unknown), <c>SEC_E_CERT_EXPIRED</c> (certificate_expired). The server's own
    /// certificate is checked by Whiskers' callback, not by SChannel, so these come from the peer.</summary>
    internal static readonly IReadOnlySet<uint> AlertStatusCodes = new HashSet<uint> { 0x80090325, 0x80090326, 0x80090327, 0x80090328 };

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(sslv3|tlsv1|tlsv13) alert \w|SSL_R_(SSLV3|TLSV1|TLSV13)_ALERT_|received an alert:? (bad certificate|certificate required|certificate unknown|unknown ca|handshake failure|access denied)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AlertMessage();

    /// <summary>The server check of the one-off connection: exactly <see cref="DockerConnectionManager.ValidateMtlsServerCert"/>.</summary>
    internal static bool ValidateServer(X509Certificate? cert, X509Chain? chain, X509Certificate2Collection anchors, string? host)
    {
        if (cert is null) return false;
        var cert2 = cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
        return DockerConnectionManager.ValidateMtlsServerCert(cert2, anchors, chain, host);
    }

    /// <summary>
    /// Runs the confirmation script of a guarded allow-list step (<see cref="MtlsAllowListCommands.Confirm"/>) through a
    /// one-off Docker client built from the NEW credential (<see cref="DockerConnectionManager.BuildMtlsCredentials"/>,
    /// same server check), with the same one-shot host-shell helper the host executor uses — so the confirmation can only
    /// arrive when the host admits the new credential. Nothing is cached; the client is disposed afterwards.
    /// </summary>
    public static async Task<(string Output, string Error, int ExitCode)> RunConfirmationAsync(Models.ServerConfig server,
        IMtlsClientKeyLoader keyLoader,
        string confirmationScript, TimeSpan timeout, CancellationToken ct)
    {
        var credentials = DockerConnectionManager.BuildMtlsCredentials(server, keyLoader);
        using var configuration = new DockerClientConfiguration(new Uri($"https://{server.TcpHost}:{server.TcpPort}"), credentials, timeout);
        using var client = configuration.CreateClient();
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var shell = Whiskers.Services.Docker.Operations.HostShellOperations.ForOneOffClient(client, cache, server.Id);
        return await shell.RunHostShellAsync(confirmationScript, server.Id, timeout, ct);
    }
}
