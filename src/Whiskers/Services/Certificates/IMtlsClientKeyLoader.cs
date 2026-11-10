using System.Security.Cryptography.X509Certificates;
using Whiskers.Configuration;
using Whiskers.Services.Vault;

namespace Whiskers.Services.Certificates;

/// <summary>
/// Contract 0015 Part A [mTLS layer, approved Q1]: builds the client certificate of a TCP/mTLS server with its private key
/// in memory. The certificate is read as before; the key goes through <see cref="ISealedKeyFiles"/> — a sealed key is
/// opened with the purpose of its place, a legacy plaintext key in the data directory and an operator-managed key outside
/// it are read as they are. Nothing about the server check, the CA or the host side changes.
/// </summary>
public interface IMtlsClientKeyLoader
{
    /// <summary>The client certificate with its private key, built in memory from the server's paths (cert PEM as today,
    /// key through ISealedKeyFiles), as the PKCS#12 round trip DockerConnectionManager did before. Throws with a reason
    /// (<see cref="SealedKeyException"/> for a key that cannot be opened).</summary>
    X509Certificate2 Load(Models.ServerConfig server);
}

/// <inheritdoc />
public sealed class MtlsClientKeyLoader(ISealedKeyFiles keys, DataPathOptions paths) : IMtlsClientKeyLoader
{
    public X509Certificate2 Load(Models.ServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (string.IsNullOrWhiteSpace(server.TcpClientCertPath))
            throw new InvalidOperationException($"Server '{server.Id}' names no client certificate.");

        // Without a key path the key sits in the certificate file (an operator-managed combined PEM), as before.
        if (string.IsNullOrWhiteSpace(server.TcpClientKeyPath))
        {
            using var combined = X509Certificate2.CreateFromPemFile(server.TcpClientCertPath);
            return X509CertificateLoader.LoadPkcs12(combined.Export(X509ContentType.Pkcs12), null);
        }

        var certPem = File.ReadAllText(server.TcpClientCertPath);
        using var key = keys.Read(server.TcpClientKeyPath, SealedKeyPurposes.MtlsClientKey(paths, server.TcpClientKeyPath));
        using var ephemeral = X509Certificate2.CreateFromPem(certPem, key.Pem);
        // PEM key → PKCS#12 round trip, so the private key is usable for TLS client auth on every platform.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }
}
