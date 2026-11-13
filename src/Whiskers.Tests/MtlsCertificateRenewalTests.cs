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
        Assert.Equal(["ca", "keygen", "sign", "ca", "stage", "activate-guarded", "confirm"], host.Steps);
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
    public async Task No_renewal_starts_while_a_whiskers_guard_is_armed_on_the_host()
    {
        // R1-44 review r1: an allow-list run (or another renewal) still waits for its confirmation there.
        var host = new SimulatedFleet(_ca) { GuardUnits = "whiskers-allow-guard-r7.timer" };
        var result = await Service(host).RenewAsync("rabenhof", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("guard timer is armed", result.Message);
        Assert.Equal(["guard-armed"], host.Steps);
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

    // ---- Fable review H1: the presented certificate is the host's word ----

    [Fact]
    public async Task A_certificate_the_fleet_ca_did_not_issue_is_never_renewed()
    {
        // A compromised host presents its own certificate naming another host's IP. Nothing is signed.
        var host = new SimulatedFleet(_ca) { PresentedToday = SelfSigned("rabenhof-dockerproxy", [Ip, "100.64.0.10"]) };
        var result = await Service(host).RenewAsync("rabenhof", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not one the fleet CA issued", result.Message);
        Assert.DoesNotContain("keygen", host.Steps);
        Assert.DoesNotContain("sign", host.Steps);
    }

    [Fact]
    public async Task A_renewal_names_only_the_address_whiskers_connects_to()
    {
        // The proxy's current certificate (from the fleet CA) carries a second IP. The renewal does not carry it on.
        var host = new SimulatedFleet(_ca) { PresentedToday = Signed("rabenhof-dockerproxy", ["rabenhof"], [Ip, "100.64.0.10"]) };
        var result = await Service(host).RenewAsync("rabenhof", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var san = host.Issued!.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal([Ip], san.EnumerateIPAddresses().Select(i => i.ToString()));
    }

    [Theory]
    [InlineData("serverwatch-client")]
    [InlineData("whiskers-client-zirkus-g1")]
    public async Task A_proxy_certificate_named_like_a_client_is_never_renewed(string cn)
    {
        var host = new SimulatedFleet(_ca) { PresentedToday = Signed(cn, [], [Ip]) };
        var result = await Service(host).RenewAsync("rabenhof", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("client name", result.Message);
        Assert.DoesNotContain("sign", host.Steps);
    }

    [Theory]
    [InlineData("serverwatch-client", "rabenhof")]
    [InlineData("whiskers-client-rabenhof-g2", "rabenhof")]
    [InlineData("rabenhof-dockerproxy", "whiskers-client-zirkus-g1")]
    public void A_proxy_identity_never_carries_a_client_name(string cn, string dns)
    {
        Assert.Throws<ArgumentException>(() => new MtlsIdentity(cn, [dns], [Ip]));
    }

    private X509Certificate2 Signed(string cn, string[] dns, string[] ips)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _ca.Sign(Csr(key, cn, dns, ips), out var leaf);
        return leaf;
    }

    private static X509Certificate2 SelfSigned(string cn, string[] ips)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var i in ips) san.AddIpAddress(System.Net.IPAddress.Parse(i));
        req.CertificateExtensions.Add(san.Build());
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
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

    internal static string Csr(ECDsa key, string cn, string[] dns, string[] ips, bool asCa = false)
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

    /// <summary>A host with a running proxy plus Badwolf's step-ca, answering the scripts by what they do.</summary>
    private sealed class SimulatedFleet(TestCa ca) : IHostCommandExecutor
    {
        private readonly ECDsa _hostKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        /// <summary>The certificate the proxy presents before the renewal (default: one the fleet CA issued for it).</summary>
        public X509Certificate2 PresentedToday { get; init; } = CurrentCert(ca);
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
        /// <summary>Active Whiskers guard units on the host (null: none).</summary>
        public string? GuardUnits { get; init; }
        public List<string> Steps => _log.Select(l => l.Step).ToList();
        public string ServerOf(string step) => _log.First(l => l.Step == step).Server;

        public X509Certificate2? Issued => _issued;
        // A copy each time: the service disposes what the probe hands it, as it does with a real handshake's certificate.
        public X509Certificate2? Presented() =>
            Copy(_activated ? (ProxyComesBack ? _issued : null) : PresentedToday);

        private static X509Certificate2? Copy(X509Certificate2? cert) =>
            cert is null ? null : X509CertificateLoader.LoadCertificate(cert.RawData);

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
                _ when script.Contains("systemctl list-units") => GuardUnits is null ? "" : Step("guard-armed", GuardUnits),
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
