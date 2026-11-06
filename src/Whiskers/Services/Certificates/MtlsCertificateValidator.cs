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

    /// <summary>The clientAuth extended key usage (1.3.6.1.5.5.7.3.2).</summary>
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Checks the per-host client certificate step-ca issued (R1-44) before it is written: it belongs to the key Whiskers
    /// generated, names exactly <paramref name="expectedCommonName"/>, is no CA, carries the clientAuth usage (step's
    /// default leaf template sets serverAuth and clientAuth whatever the CSR asks; both are accepted, clientAuth is
    /// required), carries no SAN other than at most one DNS name equal to its common name, is valid now, and chains to
    /// <paramref name="caBundlePem"/>.
    /// </summary>
    public static X509Certificate2 ValidateIssuedClient(string bundlePem, string caBundlePem, byte[] subjectPublicKeyInfo,
        string expectedCommonName, DateTime? nowUtc = null)
    {
        var chain = new X509Certificate2Collection();
        try { chain.ImportFromPem(bundlePem); }
        catch (CryptographicException ex) { throw new InvalidOperationException("step-ca returned no readable certificate.", ex); }
        if (chain.Count == 0) throw new InvalidOperationException("step-ca returned no certificate.");
        var leaf = chain[0];
        var anchors = new X509Certificate2Collection();
        var handedOut = false;
        try
        {
            CheckIssuedClient(chain, leaf, anchors, caBundlePem, subjectPublicKeyInfo, expectedCommonName, nowUtc);
            handedOut = true;
            return leaf;
        }
        finally
        {
            // Everything but the returned leaf is disposed here (review r1 NIT); the leaf too when the check failed.
            foreach (var c in chain) if (!handedOut || !ReferenceEquals(c, leaf)) c.Dispose();
            foreach (var a in anchors) a.Dispose();
        }
    }

    private static void CheckIssuedClient(X509Certificate2Collection chain, X509Certificate2 leaf, X509Certificate2Collection anchors,
        string caBundlePem, byte[] subjectPublicKeyInfo, string expectedCommonName, DateTime? nowUtc)
    {
        if (!leaf.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(subjectPublicKeyInfo))
            throw new InvalidOperationException("The issued client certificate does not belong to the generated key.");
        if (leaf.SubjectName.Name != $"CN={expectedCommonName}")
            throw new InvalidOperationException($"The issued client certificate is for '{leaf.SubjectName.Name}', expected 'CN={expectedCommonName}'.");
        if (leaf.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority))
            throw new InvalidOperationException("The issued client certificate is a CA certificate.");
        // No SAN is asked for. Until it is verified on the real step-ca that its leaf template adds none, a single DNS SAN
        // equal to the common name is tolerated (contract amendment 2); anything else is refused.
        foreach (var san in leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            var dns = san.EnumerateDnsNames().ToList();
            var ips = san.EnumerateIPAddresses().ToList();
            var rawNames = CountGeneralNames(san.RawData);
            if (ips.Count > 0 || dns.Count != 1 || dns[0] != expectedCommonName || rawNames != 1)
                throw new InvalidOperationException("The issued client certificate carries subject alternative names other than its own name.");
        }
        if (leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Count() > 1)
            throw new InvalidOperationException("The issued client certificate carries more than one SAN extension.");
        var eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault()
                  ?? throw new InvalidOperationException("The issued client certificate states no extended key usage.");
        if (!eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ClientAuthOid))
            throw new InvalidOperationException("The issued client certificate is not valid for client authentication.");

        var now = nowUtc ?? DateTime.UtcNow;
        if (leaf.NotBefore.ToUniversalTime() > now.AddMinutes(5) || leaf.NotAfter.ToUniversalTime() <= now)
            throw new InvalidOperationException("The issued client certificate is not valid now.");

        anchors.ImportFromPem(caBundlePem);
        if (anchors.Count == 0) throw new InvalidOperationException("The CA bundle holds no certificate.");
        using var built = new X509Chain();
        built.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        built.ChainPolicy.CustomTrustStore.AddRange(anchors);
        built.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        foreach (var c in chain.Skip(1)) built.ChainPolicy.ExtraStore.Add(c);
        if (!built.Build(leaf))
            throw new InvalidOperationException("The issued client certificate does not chain to the fleet CA.");
    }

    /// <summary>The number of GeneralName entries in a SubjectAltName value (URIs and e-mail addresses included, which the
    /// typed enumerators do not show).</summary>
    private static int CountGeneralNames(byte[] raw)
    {
        try
        {
            var reader = new System.Formats.Asn1.AsnReader(raw, System.Formats.Asn1.AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            var count = 0;
            while (sequence.HasData)
            {
                sequence.ReadEncodedValue();
                count++;
            }
            return count;
        }
        catch (System.Formats.Asn1.AsnContentException)
        {
            return -1;
        }
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
