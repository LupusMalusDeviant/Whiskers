using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Whiskers.Tests;

/// <summary>Root + intermediate, like step-ca's; signs CSRs the way <c>step certificate sign</c> does. Shared by the mTLS
/// certificate tests (R1-44 moved it out of <c>MtlsCertificateRenewalTests</c>).</summary>
internal sealed class TestCa : IDisposable
{
    private readonly ECDsa _rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _intKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public X509Certificate2 Root { get; }
    public X509Certificate2 Intermediate { get; }
    public string BundlePem => Root.ExportCertificatePem() + "\n" + Intermediate.ExportCertificatePem() + "\n";

    public TestCa()
    {
        var now = DateTimeOffset.UtcNow;
        var rootReq = new CertificateRequest("CN=Test Root", _rootKey, HashAlgorithmName.SHA256);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Root = rootReq.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        var intReq = new CertificateRequest("CN=Test Intermediate", _intKey, HashAlgorithmName.SHA256);
        intReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        intReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var intPublic = intReq.Create(Root, now.AddDays(-1), now.AddYears(5), [1, 2, 3, 4]);
        Intermediate = intPublic.CopyWithPrivateKey(_intKey);
    }

    /// <summary>Signs a CSR, keeping its extensions. <paramref name="stepLeafEku"/>: replace the extended key usage by
    /// serverAuth + clientAuth, as step's default leaf template does whatever the CSR asks. <paramref name="extraSan"/>:
    /// add a DNS SAN (to model a template that adds one).</summary>
    public string Sign(string csrPem, out X509Certificate2 leaf, bool stepLeafEku = false, string? extraSan = null)
    {
        var req = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        if (stepLeafEku)
        {
            foreach (var eku in req.CertificateExtensions.OfType<X509EnhancedKeyUsageExtension>().ToList())
                req.CertificateExtensions.Remove(eku);
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, false));
        }
        if (extraSan is not null)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(extraSan);
            req.CertificateExtensions.Add(san.Build());
        }
        var now = DateTimeOffset.UtcNow;
        leaf = req.Create(Intermediate, now.AddMinutes(-5), now.AddDays(365), RandomNumberGenerator.GetBytes(8));
        return leaf.ExportCertificatePem() + "\n" + Intermediate.ExportCertificatePem() + "\n";
    }

    /// <summary>A proxy (server) certificate with key for <paramref name="ip"/>, as onboarding issues it.</summary>
    public X509Certificate2 IssueServer(string ip)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN=host-dockerproxy", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Parse(ip));
        req.CertificateExtensions.Add(san.Build());
        var now = DateTimeOffset.UtcNow;
        using var cert = req.Create(Intermediate, now.AddMinutes(-5), now.AddDays(365), RandomNumberGenerator.GetBytes(8));
        return cert.CopyWithPrivateKey(key);
    }

    public void Dispose()
    {
        Root.Dispose(); Intermediate.Dispose(); _rootKey.Dispose(); _intKey.Dispose();
    }
}
