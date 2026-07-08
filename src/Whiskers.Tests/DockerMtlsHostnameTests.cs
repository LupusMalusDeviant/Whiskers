using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Whiskers.Services.Docker;

namespace Whiskers.Tests;

public class DockerMtlsHostnameTests
{
    // Builds a throwaway CA and a leaf certificate it signs, with the given DNS SAN.
    private static (X509Certificate2 Ca, X509Certificate2 Leaf) MakeCaAndLeaf(string sanDns)
    {
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=Test Whiskers CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        var ca = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        using var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=docker-host", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(sanDns);
        leafReq.CertificateExtensions.Add(san.Build());
        var leaf = leafReq.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(1), new byte[] { 1, 2, 3, 4 });
        return (ca, leaf);
    }

    [Fact]
    public void ChainAndHostnameMatch_Accepts()
    {
        var (ca, leaf) = MakeCaAndLeaf("docker.example.com");
        using (ca) using (leaf)
        {
            var trust = new X509Certificate2Collection(ca);
            Assert.True(DockerConnectionManager.ValidateMtlsServerCert(leaf, trust, null, "docker.example.com"));
        }
    }

    [Fact]
    public void ChainOk_ButWrongHostname_Rejects()
    {
        // NIED-13: a valid-but-wrong cert (our CA signed it, but for a different host) must be rejected.
        var (ca, leaf) = MakeCaAndLeaf("docker.example.com");
        using (ca) using (leaf)
        {
            var trust = new X509Certificate2Collection(ca);
            Assert.False(DockerConnectionManager.ValidateMtlsServerCert(leaf, trust, null, "evil.example.com"));
        }
    }

    [Fact]
    public void UntrustedChain_Rejects()
    {
        var (ca, leaf) = MakeCaAndLeaf("docker.example.com");
        using (ca) using (leaf)
        {
            var emptyTrust = new X509Certificate2Collection(); // our CA is NOT in the trust store
            Assert.False(DockerConnectionManager.ValidateMtlsServerCert(leaf, emptyTrust, null, "docker.example.com"));
        }
    }

    [Fact]
    public void EmptyExpectedHost_FailsClosed()
    {
        var (ca, leaf) = MakeCaAndLeaf("docker.example.com");
        using (ca) using (leaf)
        {
            var trust = new X509Certificate2Collection(ca);
            Assert.False(DockerConnectionManager.ValidateMtlsServerCert(leaf, trust, null, ""));
        }
    }
}
