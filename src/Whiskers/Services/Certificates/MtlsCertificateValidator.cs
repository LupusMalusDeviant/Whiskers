using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Whiskers.Services.Docker;

namespace Whiskers.Services.Certificates;

/// <summary>
/// Checks what crosses the trust boundary while a proxy certificate is issued.
///
/// <para>The CSR comes from the host, and the host is exactly what the certificate is meant to vouch for. A
/// compromised host that could choose its own SANs would get a certificate for another host's IP and could
/// impersonate that proxy to Whiskers — so the CSR must ask for precisely the identity Whiskers expects, nothing
/// more. The issued certificate is checked as well before it is installed: the chain, the host name and that it
/// belongs to the key in the CSR.</para>
/// </summary>
public static class MtlsCertificateValidator
{
    /// <summary>Parses the CSR (verifying its self-signature) and returns its public key, or throws with the reason.</summary>
    public static PublicKey ValidateCsr(string csrPem, MtlsIdentity expected)
    {
        CertificateRequest req;
        try
        {
            // Extensions are loaded only to be read here; this request is never signed by .NET.
            req = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"The CSR from the host is not valid: {ex.Message}", ex);
        }

        if (req.SubjectName.Name != $"CN={expected.CommonName}")
            throw new InvalidOperationException($"The CSR asks for subject '{req.SubjectName.Name}', expected 'CN={expected.CommonName}'.");

        if (req.PublicKey.Oid.Value != "1.2.840.10045.2.1")
            throw new InvalidOperationException("The CSR does not carry an EC key.");

        foreach (var ext in req.CertificateExtensions)
        {
            if (ext is X509BasicConstraintsExtension { CertificateAuthority: true })
                throw new InvalidOperationException("The CSR asks to be a CA.");
        }

        var san = req.CertificateExtensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault()
                  ?? throw new InvalidOperationException("The CSR carries no subject alternative names.");
        RequireExactSans(san, expected, "The CSR");

        return req.PublicKey;
    }

    /// <summary>Checks the certificate step-ca issued: same key as the CSR, the expected identity, not a CA, and a
    /// chain to <paramref name="caBundlePem"/> that Whiskers' own mTLS check would accept for the host's IP.</summary>
    public static X509Certificate2 ValidateIssued(string bundlePem, string caBundlePem, PublicKey csrKey, MtlsIdentity expected)
    {
        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(bundlePem);
        if (chain.Count == 0) throw new InvalidOperationException("step-ca returned no certificate.");
        var leaf = chain[0];

        if (!leaf.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(csrKey.ExportSubjectPublicKeyInfo()))
            throw new InvalidOperationException("The issued certificate does not belong to the key in the CSR.");
        if (leaf.SubjectName.Name != $"CN={expected.CommonName}")
            throw new InvalidOperationException($"The issued certificate is for '{leaf.SubjectName.Name}'.");
        if (leaf.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority))
            throw new InvalidOperationException("The issued certificate is a CA certificate.");

        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault()
                  ?? throw new InvalidOperationException("The issued certificate carries no subject alternative names.");
        RequireExactSans(san, expected, "The issued certificate");

        var anchors = new X509Certificate2Collection();
        anchors.ImportFromPem(caBundlePem);
        using var presented = new X509Chain();
        foreach (var c in chain.Skip(1)) presented.ChainPolicy.ExtraStore.Add(c);
        if (!DockerConnectionManager.ValidateMtlsServerCert(leaf, anchors, presented, expected.IpAddresses[0]))
            throw new InvalidOperationException("The issued certificate does not chain to the CA or does not match the host.");

        return leaf;
    }

    private static void RequireExactSans(X509SubjectAlternativeNameExtension san, MtlsIdentity expected, string what)
    {
        var dns = san.EnumerateDnsNames().Order(StringComparer.Ordinal).ToList();
        var ips = san.EnumerateIPAddresses().Select(i => i.ToString()).Order(StringComparer.Ordinal).ToList();
        var wantDns = expected.DnsNames.Order(StringComparer.Ordinal).ToList();
        var wantIps = expected.IpAddresses.Order(StringComparer.Ordinal).ToList();
        if (!dns.SequenceEqual(wantDns) || !ips.SequenceEqual(wantIps))
            throw new InvalidOperationException(
                $"{what} names [{string.Join(", ", dns.Concat(ips))}], expected [{string.Join(", ", wantDns.Concat(wantIps))}].");
    }
}
