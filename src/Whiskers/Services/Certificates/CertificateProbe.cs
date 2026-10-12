using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Whiskers.Services.Certificates;

/// <summary>Reads certificates without trusting or using them: from PEM files on disk and from the handshake of
/// a TLS endpoint.</summary>
public static class CertificateProbe
{
    /// <summary>Every certificate in a PEM file — a CA file is often a bundle of root and intermediate, and the
    /// intermediate usually expires first.</summary>
    public static X509Certificate2Collection ReadPemFile(string path)
    {
        var certs = new X509Certificate2Collection();
        certs.ImportFromPemFile(path);
        return certs;
    }

    /// <summary>Fetches the certificate a TLS endpoint presents. The handshake is aborted as soon as the server's
    /// certificate arrives — the validation callback captures it and refuses — so nothing is sent, no client
    /// certificate is offered and the result is never used for trust. TLS sends the server certificate before
    /// it asks for a client certificate, so this also works against the mTLS proxies.</summary>
    public static async Task<X509Certificate2?> FetchServerCertificateAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cts.Token);

        X509Certificate2? captured = null;
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, cert, _, _) =>
        {
            if (cert is not null)
                captured = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
            return false;
        });

        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
        }
        catch (AuthenticationException) when (captured is not null)
        {
            // Expected: the callback refused on purpose once it had the certificate.
        }
        catch (IOException) when (captured is not null)
        {
            // Some stacks report the refused handshake as a reset instead.
        }

        return captured;
    }
}
