using Microsoft.Extensions.Logging.Abstractions;
using ServerWatch.Services.Server;
using Xunit;

namespace ServerWatch.Tests;

public class SslCertificateTests
{
    // --- Model computed properties (the NIED-10 false-positive fix) ---

    [Fact]
    public void UnknownExpiry_IsNotExpiringSoon()
    {
        var cert = new SslCertificate { ExpiresAt = null };
        Assert.Null(cert.DaysUntilExpiry);
        Assert.False(cert.IsExpiringSoon);
    }

    [Fact]
    public void ExpiresIn5Days_IsExpiringSoon()
    {
        var cert = new SslCertificate { ExpiresAt = DateTime.UtcNow.AddDays(5) };
        Assert.True(cert.IsExpiringSoon);
        Assert.InRange(cert.DaysUntilExpiry!.Value, 4, 5);
    }

    [Fact]
    public void ExpiresIn90Days_IsNotExpiringSoon()
    {
        var cert = new SslCertificate { ExpiresAt = DateTime.UtcNow.AddDays(90) };
        Assert.False(cert.IsExpiringSoon);
    }

    // --- Parse: an unparseable certbot expiry must leave expiry unknown, not fire a false alarm ---

    private sealed class StubExecutor(string output) : IHostCommandExecutor
    {
        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null, CancellationToken ct = default)
            => Task.FromResult(new CommandResult { ExitCode = 0, Output = output });
    }

    [Fact]
    public async Task ListCertificates_UnparseableExpiry_LeavesExpiryUnknown()
    {
        // certbot block whose Expiry Date carries no yyyy-MM-dd token → parse fails.
        var output =
            "Certificate Name: example.com\n" +
            "  Domains: example.com www.example.com\n" +
            "  Expiry Date: N/A (INVALID: test)\n";
        var svc = new SslCertService(new StubExecutor(output), NullLogger<SslCertService>.Instance);

        var certs = await svc.ListCertificatesAsync("local");

        var cert = Assert.Single(certs);
        Assert.Null(cert.ExpiresAt);        // unknown, not DateTime.MinValue
        Assert.False(cert.IsExpiringSoon);  // and therefore NOT a false "expiring soon"
    }

    [Fact]
    public async Task ListCertificates_ValidExpiry_ParsesDate()
    {
        var output =
            "Certificate Name: example.com\n" +
            "  Domains: example.com\n" +
            "  Expiry Date: 2999-03-15 12:00:00+00:00 (VALID: 9999 days)\n";
        var svc = new SslCertService(new StubExecutor(output), NullLogger<SslCertService>.Instance);

        var certs = await svc.ListCertificatesAsync("local");

        var cert = Assert.Single(certs);
        Assert.NotNull(cert.ExpiresAt);
        Assert.Equal(new DateTime(2999, 3, 15), cert.ExpiresAt!.Value.Date);
        Assert.False(cert.IsExpiringSoon);
    }
}
