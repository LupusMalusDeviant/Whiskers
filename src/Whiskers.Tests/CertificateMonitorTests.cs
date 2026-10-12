using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Whiskers.Models;
using Whiskers.Services.Certificates;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>C1: an expired mTLS client certificate or CA takes every SSH-free server down at once, and nothing
/// warned before it happened. The monitor announces 30, 14 and 7 days ahead and on expiry — each stage once.</summary>
public sealed class CertificateMonitorTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"certmon-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(31, null)]
    [InlineData(30, 30)]
    [InlineData(15, 30)]
    [InlineData(14, 14)]
    [InlineData(8, 14)]
    [InlineData(7, 7)]
    [InlineData(1, 7)]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    public void Stage_is_the_tightest_threshold_reached(int daysLeft, int? expected)
    {
        Assert.Equal(expected, CertificateExpiry.StageFor(Now.AddDays(daysLeft), Now));
    }

    [Fact]
    public void Each_stage_is_announced_once_and_survives_a_restart()
    {
        var path = Path.Combine(_dir, "state.json");
        var cert = Cert(Now.AddDays(20));

        Assert.Equal(30, Assert.Single(new CertificateAlertState(path).Advance([cert], Now)).Stage);
        Assert.Empty(new CertificateAlertState(path).Advance([cert], Now.AddHours(12)));   // restart: no repeat
        Assert.Equal(14, Assert.Single(new CertificateAlertState(path).Advance([cert], Now.AddDays(7))).Stage);
        Assert.Equal(0, Assert.Single(new CertificateAlertState(path).Advance([cert], Now.AddDays(21))).Stage);
        Assert.Empty(new CertificateAlertState(path).Advance([cert], Now.AddDays(22)));
    }

    [Fact]
    public void Jumping_several_stages_at_once_announces_only_the_tightest()
    {
        var state = new CertificateAlertState(Path.Combine(_dir, "state.json"));
        Assert.Equal(7, Assert.Single(state.Advance([Cert(Now.AddDays(3))], Now)).Stage);
    }

    [Fact]
    public void A_renewed_certificate_starts_over_and_the_old_entry_is_forgotten()
    {
        var path = Path.Combine(_dir, "state.json");
        var old = Cert(Now.AddDays(5), fingerprint: "OLD");
        Assert.Single(new CertificateAlertState(path).Advance([old], Now));

        var renewed = Cert(Now.AddDays(90), fingerprint: "NEW");
        Assert.Empty(new CertificateAlertState(path).Advance([renewed], Now));
        Assert.DoesNotContain("OLD", File.ReadAllText(path));
    }

    [Fact]
    public void A_corrupt_state_file_costs_one_repeat_not_the_check()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "{not json");
        Assert.Single(new CertificateAlertState(path).Advance([Cert(Now.AddDays(5))], Now));
    }

    [Fact]
    public void Expired_is_an_error_event_and_names_the_certificate()
    {
        var evt = CertificateMonitor.ToEvent(Cert(Now.AddDays(-1)), 0, Now);
        Assert.Equal("certificate_expired", evt.EventType);
        Assert.Equal("Error", NotificationFormatter.Describe(evt).Severity);
        Assert.Contains("/app/data/mtls/client.crt", evt.ImageInfo);
        Assert.Contains("expired on 2026-10-11", evt.ImageInfo);

        var soon = CertificateMonitor.ToEvent(Cert(Now.AddDays(10)), 14, Now);
        Assert.Equal("certificate_expiring", soon.EventType);
        Assert.Contains("in 10 days", soon.ImageInfo);
    }

    [Fact]
    public void Several_certificates_on_one_server_are_not_throttled_into_one()
    {
        var throttler = new NotificationThrottler(60);
        var a = new NotificationEvent { EventType = "certificate_expiring", ServerId = "s1", ImageInfo = "a" };
        var b = new NotificationEvent { EventType = "certificate_expiring", ServerId = "s1", ImageInfo = "b" };
        throttler.Record(a);
        Assert.False(throttler.IsThrottled(b, 60));
    }

    [Fact]
    public void A_ca_bundle_yields_every_certificate_in_it()
    {
        Directory.CreateDirectory(_dir);
        using var root = SelfSigned("Root", Now.AddYears(10));
        using var intermediate = SelfSigned("Intermediate", Now.AddDays(20));
        var path = Path.Combine(_dir, "ca.crt");
        File.WriteAllText(path, root.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem());

        var certs = CertificateProbe.ReadPemFile(path);
        Assert.Equal(2, certs.Count);
        Assert.Contains(certs, c => c.GetNameInfo(X509NameType.SimpleName, false) == "Intermediate");
    }

    [Fact]
    public async Task The_server_certificate_is_read_from_the_handshake_without_trusting_it()
    {
        using var serverCert = SelfSigned("proxy.test", DateTime.UtcNow.AddDays(9), withKeyForTls: true);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(client.GetStream());
            try { await ssl.AuthenticateAsServerAsync(serverCert, clientCertificateRequired: true, checkCertificateRevocation: false); }
            catch (Exception) { /* the probe aborts the handshake on purpose */ }
        });

        using var fetched = await CertificateProbe.FetchServerCertificateAsync("127.0.0.1", port, TimeSpan.FromSeconds(10), CancellationToken.None);
        await serve;
        listener.Stop();

        Assert.NotNull(fetched);
        Assert.Equal(serverCert.Thumbprint, fetched!.Thumbprint);
    }

    private static ObservedCertificate Cert(DateTime notAfter, string fingerprint = "AB12") =>
        new("mTLS client", "whiskers-client", "/app/data/mtls/client.crt", null, null, notAfter, fingerprint);

    private static X509Certificate2 SelfSigned(string cn, DateTime notAfter, bool withKeyForTls = false)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(notAfter.AddYears(-1), notAfter);
        // SslStream on Windows needs the key in a persisted form; a PFX round-trip gives it one.
        return withKeyForTls
            ? X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null)
            : cert;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }
}
