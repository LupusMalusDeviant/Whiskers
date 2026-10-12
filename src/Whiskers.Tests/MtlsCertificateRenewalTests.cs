using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.Certificates;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>C2: the proxy key is made on the host and never travels; the CSR is held to exactly the expected
/// identity before step-ca signs it; and a renewal that cannot be confirmed is rolled back by the host itself.</summary>
public sealed class MtlsCertificateRenewalTests : IDisposable
{
    private const string Ip = "100.64.0.9";
    private static readonly MtlsIdentity Identity = new("rabenhof-dockerproxy", ["rabenhof"], [Ip]);

    private readonly TestCa _ca = new();

    // ---- identity: the allow-list between certificate fields and the shell ----

    [Theory]
    [InlineData("rabenhof;reboot", "rabenhof", Ip)]
    [InlineData("rabenhof-dockerproxy", "a b", Ip)]
    [InlineData("rabenhof-dockerproxy", "rabenhof", "100.64.0.9,DNS:evil")]
    [InlineData("Rabenhof", "rabenhof", Ip)]
    [InlineData("rabenhof-dockerproxy", "$(id)", Ip)]
    public void Identity_rejects_anything_that_could_reach_the_shell(string cn, string dns, string ip)
    {
        Assert.Throws<ArgumentException>(() => new MtlsIdentity(cn, [dns], [ip]));
    }

    [Fact]
    public void Identity_needs_an_ip_san_because_whiskers_connects_by_ip()
    {
        Assert.Throws<ArgumentException>(() => new MtlsIdentity("x-dockerproxy", ["x"], []));
    }

    [Fact]
    public void The_private_key_is_generated_on_the_host_and_only_the_csr_is_printed()
    {
        var script = Decode(MtlsCertificateCommands.GenerateKeyAndCsr(Identity));
        Assert.Contains("-keyout server.key.new", script);
        Assert.Contains("-subj /CN=rabenhof-dockerproxy -addext subjectAltName=DNS:rabenhof,IP:100.64.0.9", script);
        Assert.Contains("base64 -w0 server.csr.new", script);
        Assert.DoesNotContain("base64 -w0 server.key", script);
        Assert.DoesNotContain("cat server.key", script);
    }

    [Fact]
    public void The_guard_restores_the_old_files_unless_the_renewal_was_confirmed()
    {
        var activate = Decode(MtlsCertificateCommands.ActivateWithGuard("t1"));
        // The guard is armed BEFORE the swap, and the restart is detached so this command can still return.
        Assert.True(activate.IndexOf("systemd-run --collect --unit=whiskers-cert-guard-t1 --on-active=180", StringComparison.Ordinal)
                    < activate.IndexOf("mv -f server.key.new server.key", StringComparison.Ordinal));
        Assert.Contains("--on-active=3 docker restart ghostunnel", activate);

        var guard = MtlsCertificateCommands.GuardBody();
        Assert.Contains("if [ -f .renew-ok ]", guard);
        Assert.Contains("mv -f server.key.prev server.key", guard);

        var confirm = Decode(MtlsCertificateCommands.Confirm());
        Assert.Contains("touch .renew-ok", confirm);
        Assert.Contains("rm -f server.key.prev", confirm); // the old key must not stay usable
    }

    // ---- CSR and issued certificate ----

    [Fact]
    public void A_csr_for_exactly_the_expected_identity_passes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.NotNull(MtlsCertificateValidator.ValidateCsr(Csr(key, "rabenhof-dockerproxy", ["rabenhof"], [Ip]), Identity));
    }

    [Fact]
    public void A_csr_asking_for_another_hosts_ip_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = Csr(key, "rabenhof-dockerproxy", ["rabenhof"], [Ip, "100.64.0.10"]);
        Assert.Contains("expected", Assert.Throws<InvalidOperationException>(() => MtlsCertificateValidator.ValidateCsr(csr, Identity)).Message);
    }

    [Fact]
    public void A_csr_with_another_subject_or_asking_to_be_a_ca_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidOperationException>(() =>
            MtlsCertificateValidator.ValidateCsr(Csr(key, "serverwatch-client", ["rabenhof"], [Ip]), Identity));
        Assert.Throws<InvalidOperationException>(() =>
            MtlsCertificateValidator.ValidateCsr(Csr(key, "rabenhof-dockerproxy", ["rabenhof"], [Ip], asCa: true), Identity));
    }

    [Fact]
    public void An_issued_certificate_for_a_different_key_is_refused()
    {
        using var hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csrKey = MtlsCertificateValidator.ValidateCsr(Csr(hostKey, "rabenhof-dockerproxy", ["rabenhof"], [Ip]), Identity);
        var bundle = _ca.Sign(Csr(otherKey, "rabenhof-dockerproxy", ["rabenhof"], [Ip]), out _);
        Assert.Throws<InvalidOperationException>(() => MtlsCertificateValidator.ValidateIssued(bundle, _ca.BundlePem, csrKey, Identity));
    }

    [Fact]
    public void An_issued_certificate_from_another_ca_is_refused()
    {
        using var hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = Csr(hostKey, "rabenhof-dockerproxy", ["rabenhof"], [Ip]);
        var csrKey = MtlsCertificateValidator.ValidateCsr(csr, Identity);
        using var foreign = new TestCa();
        Assert.Throws<InvalidOperationException>(() =>
            MtlsCertificateValidator.ValidateIssued(foreign.Sign(csr, out _), _ca.BundlePem, csrKey, Identity));
    }

    // ---- the whole renewal against a simulated host and step-ca ----

    [Fact]
    public async Task Renewal_keeps_the_identity_swaps_the_certificate_and_confirms_through_the_new_one()
    {
        var host = new SimulatedFleet(_ca);
        var svc = Service(host);

        var result = await svc.RenewAsync("rabenhof", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["keygen", "sign", "ca", "stage", "activate-guarded", "confirm"], host.Steps);
        Assert.Equal("local", host.ServerOf("sign"));
        Assert.Equal("rabenhof", host.ServerOf("keygen"));
    }

    [Fact]
    public async Task A_host_whose_csr_overreaches_is_never_activated()
    {
        var host = new SimulatedFleet(_ca) { CsrIps = [Ip, "100.64.0.10"] };
        var result = await Service(host).RenewAsync("rabenhof", CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("sign", host.Steps);
        Assert.DoesNotContain("activate-guarded", host.Steps);
    }

    [Fact]
    public async Task A_renewal_that_cannot_be_confirmed_leaves_the_rollback_to_the_host()
    {
        var host = new SimulatedFleet(_ca) { ProxyComesBack = false };
        var result = await Service(host).RenewAsync("rabenhof", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("restores the previous certificate on its own", result.Message);
        Assert.Contains("activate-guarded", host.Steps);
        Assert.DoesNotContain("confirm", host.Steps);
    }

    [Fact]
    public async Task A_server_without_mtls_has_nothing_to_renew()
    {
        var host = new SimulatedFleet(_ca);
        host.Server.TcpUseTls = false;
        var result = await Service(host).RenewAsync("rabenhof", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Empty(host.Steps);
    }

    private MtlsCertificateService Service(SimulatedFleet host) =>
        new(host, new FakeServerConfig(host.Server), NullLogger<MtlsCertificateService>.Instance)
        {
            FetchPresented = (_, _, _) => Task.FromResult<X509Certificate2?>(host.Presented()),
            RestartGrace = TimeSpan.Zero,
            ConfirmInterval = TimeSpan.Zero,
            ConfirmAttempts = 2,
        };

    internal static string Decode(string command)
    {
        if (!command.StartsWith("echo ", StringComparison.Ordinal)) return command;
        var b64 = command.Split(' ')[1];
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    private static string Csr(ECDsa key, string cn, string[] dns, string[] ips, bool asCa = false)
    {
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var d in dns) san.AddDnsName(d);
        foreach (var i in ips) san.AddIpAddress(System.Net.IPAddress.Parse(i));
        req.CertificateExtensions.Add(san.Build());
        if (asCa) req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return req.CreateSigningRequestPem();
    }

    public void Dispose() => _ca.Dispose();

    /// <summary>Root + intermediate, like step-ca's; signs CSRs the way <c>step certificate sign</c> does.</summary>
    private sealed class TestCa : IDisposable
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

        public string Sign(string csrPem, out X509Certificate2 leaf)
        {
            var req = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            var now = DateTimeOffset.UtcNow;
            leaf = req.Create(Intermediate, now.AddMinutes(-5), now.AddDays(365), RandomNumberGenerator.GetBytes(8));
            return leaf.ExportCertificatePem() + "\n" + Intermediate.ExportCertificatePem() + "\n";
        }

        public void Dispose()
        {
            Root.Dispose(); Intermediate.Dispose(); _rootKey.Dispose(); _intKey.Dispose();
        }
    }

    /// <summary>A host with a running proxy plus Badwolf's step-ca, answering the scripts by what they do.</summary>
    private sealed class SimulatedFleet(TestCa ca) : IHostCommandExecutor
    {
        private readonly ECDsa _hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly X509Certificate2 _current = CurrentCert(ca);
        private X509Certificate2? _issued;
        private bool _activated;
        private readonly List<(string Step, string Server)> _log = new();

        public Whiskers.Models.ServerConfig Server { get; } = new()
        {
            Id = "rabenhof", Name = "Rabenhof", ConnectionType = Whiskers.Models.ConnectionType.TCP,
            TcpHost = Ip, TcpPort = 2376, TcpUseTls = true,
        };
        public string[] CsrIps { get; init; } = [Ip];
        public bool ProxyComesBack { get; init; } = true;
        public List<string> Steps => _log.Select(l => l.Step).ToList();
        public string ServerOf(string step) => _log.First(l => l.Step == step).Server;

        public X509Certificate2? Presented() =>
            _activated ? (ProxyComesBack ? _issued : null) : _current;

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            var script = Decode(command);
            string Step(string name, string output)
            {
                _log.Add((name, serverId));
                return output;
            }

            var output = script switch
            {
                _ when script.Contains("openssl req -new") =>
                    Step("keygen", B64(Csr(_hostKey, "rabenhof-dockerproxy", ["rabenhof"], CsrIps))),
                _ when script.Contains("step certificate sign") =>
                    Step("sign", B64(SignFromScript(script))),
                _ when script.StartsWith("cat /opt/step-ca/certs/root_ca.crt", StringComparison.Ordinal) =>
                    Step("ca", B64(ca.BundlePem)),
                _ when script.Contains("echo staged") => Step("stage", "staged"),
                _ when script.Contains("whiskers-cert-guard-") => Activate(),
                _ when script.Contains("touch .renew-ok") => Step("confirm", "confirmed"),
                _ => Step("unexpected", ""),
            };
            return Task.FromResult(new CommandResult { ExitCode = 0, Output = output });

            string Activate() { _activated = true; return Step("activate-guarded", "activated"); }
        }

        private string SignFromScript(string script)
        {
            var csrB64 = script.Split('\n').First(l => l.StartsWith("echo ", StringComparison.Ordinal)).Split(' ')[1];
            var bundle = ca.Sign(Encoding.ASCII.GetString(Convert.FromBase64String(csrB64)), out var leaf);
            _issued = leaf;
            return bundle;
        }

        private static X509Certificate2 CurrentCert(TestCa ca)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ca.Sign(Csr(key, "rabenhof-dockerproxy", ["rabenhof"], [Ip]), out var leaf);
            return leaf;
        }

        private static string B64(string s) => Convert.ToBase64String(Encoding.ASCII.GetBytes(s));
    }
}
