using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Notifications;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>
/// Follow-up (2026-11-03, review R1-54 MINOR 1): the e-mail channel's TLS path end to end, against a local SMTP server
/// whose certificate is generated here (self-signed, <see cref="CertificateRequest"/>). With TLS on, a server that does not
/// offer STARTTLS gets no message in plain text (no downgrade); port 465 speaks TLS from the first byte; the certificate is
/// validated — an untrusted one ends the send before anything is sent — and the TLS name is the configured host name, not
/// the address the guard connected to.
///
/// <para>What these tests cannot show without trusting the certificate on the machine (which a test must not do): that a
/// trusted certificate for another name is refused. That check is <see cref="SslStream"/>'s own, against the target host
/// MailKit hands it; the tests pin that this target host is the configured name (the server reads it as SNI), and that
/// the production client sets no validation callback that would skip the checks.</para>
/// </summary>
public sealed class SmtpTlsTests
{
    private const string MailHost = "mail.example.test";

    private static readonly Lazy<X509Certificate2> Certificate = new(() =>
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={MailHost}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(MailHost);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // A key the TLS stack can use on every platform (an ephemeral one is refused by SChannel).
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    });

    private static NotificationEvent Event() => new() { EventType = "auto_update_failed", ContainerName = "web", ContainerId = "web-1" };

    private static EmailNotificationService Email(RedirectingGuard guard, int port) =>
        new(new StaticOptionsMonitor<EmailSettings>(new EmailSettings
        {
            Enabled = true, Host = MailHost, Port = port, UseSsl = true, From = "whiskers@example.test", To = "ops@example.test",
            ThrottleMinutes = 0,
        }), guard, NullLogger<EmailNotificationService>.Instance)
        { SmtpDeadline = TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task With_tls_on_a_server_without_starttls_gets_no_message_in_plain_text()
    {
        await using var smtp = new TlsSmtp { OfferStartTls = false };
        var guard = new RedirectingGuard(smtp.Port);

        await Assert.ThrowsAnyAsync<Exception>(() => Email(guard, 587).DeliverAsync(Event(), CancellationToken.None));

        await smtp.ConversationEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("EHLO", smtp.Commands);
        Assert.DoesNotContain(smtp.Commands, c => c is "MAIL" or "RCPT" or "DATA" or "AUTH");
        Assert.Equal(0, smtp.Delivered);
    }

    [Fact]
    public async Task Starttls_with_an_untrusted_certificate_ends_the_send_before_anything_is_sent()
    {
        await using var smtp = new TlsSmtp { OfferStartTls = true };
        var guard = new RedirectingGuard(smtp.Port);

        await Assert.ThrowsAsync<SslHandshakeException>(() => Email(guard, 587).DeliverAsync(Event(), CancellationToken.None));

        await smtp.ConversationEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("STARTTLS", smtp.Commands);
        // The TLS name is the configured host, while the guard connected to an address.
        Assert.Equal(MailHost, smtp.ServerName);
        Assert.Equal((MailHost, 587), Assert.Single(guard.Asked));
        Assert.DoesNotContain(smtp.Commands, c => c is "MAIL" or "RCPT" or "DATA" or "AUTH");
        Assert.Equal(0, smtp.Delivered);
    }

    [Fact]
    public async Task Port_465_speaks_tls_from_the_first_byte_and_validates_the_certificate()
    {
        await using var smtp = new TlsSmtp { ImplicitTls = true };
        var guard = new RedirectingGuard(smtp.Port);

        await Assert.ThrowsAsync<SslHandshakeException>(() => Email(guard, 465).DeliverAsync(Event(), CancellationToken.None));

        await smtp.ConversationEnded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(smtp.HandshakeStarted, "the client opened with a TLS handshake, not with plain SMTP");
        Assert.Equal(MailHost, smtp.ServerName);
        Assert.Equal((MailHost, 465), Assert.Single(guard.Asked));
        Assert.Empty(smtp.Commands);
        Assert.Equal(0, smtp.Delivered);
    }

    [Fact]
    public void The_client_skips_no_certificate_check()
    {
        // MailKit validates the chain and the name itself when no callback is set; revocation is off on purpose
        // (R1-54 review round 1, as System.Net.Mail).
        using var client = EmailNotificationService.NewClient(TimeSpan.FromSeconds(5));
        Assert.Null(client.ServerCertificateValidationCallback);
        Assert.False(client.CheckCertificateRevocation);
    }

    /// <summary>The guard as the channel uses it, except that every host connects to the local test server — so the
    /// configured name (and port) reach MailKit unchanged while the socket goes to loopback.</summary>
    private sealed class RedirectingGuard(int localPort) : IOutboundTargetGuard
    {
        public List<(string Host, int Port)> Asked { get; } = [];

        public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
        {
            lock (Asked) Asked.Add((host, port));
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, localPort, ct);
                return client.GetStream();     // owns the socket from here on
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public Task<OutboundVerdict> CheckAsync(string host, int port, CancellationToken ct) => Task.FromResult(OutboundVerdict.Allow);
    }

    /// <summary>A minimal SMTP server that can offer STARTTLS or speak TLS from the first byte, with the generated
    /// certificate. Records each command verb, the TLS name the client asked for (SNI), and what it delivered.</summary>
    private sealed class TlsSmtp : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly List<string> _commands = [];
        private int _delivered;

        public bool OfferStartTls { get; init; }
        public bool ImplicitTls { get; init; }
        public int Port { get; }
        public volatile bool HandshakeStarted;
        public string? ServerName { get; private set; }
        public int Delivered => Volatile.Read(ref _delivered);
        public TaskCompletionSource ConversationEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Commands
        {
            get { lock (_commands) return [.. _commands]; }
        }

        public TlsSmtp()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await ServeAsync(client.GetStream());
            }
            catch (Exception) { /* the client went away, or the server stops */ }
            finally { ConversationEnded.TrySetResult(); }
        }

        private async Task<Stream> TlsAsync(Stream plain)
        {
            HandshakeStarted = true;
            var tls = new SslStream(plain, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, name) =>
                {
                    ServerName = name;
                    return Certificate.Value;
                },
            }, _stop.Token);
            return tls;
        }

        private async Task ServeAsync(Stream stream)
        {
            if (ImplicitTls) stream = await TlsAsync(stream);
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake ESMTP");
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var verb = line.Split(' ', ':')[0].ToUpperInvariant();
                lock (_commands) _commands.Add(verb);
                switch (verb)
                {
                    case "EHLO" when OfferStartTls && stream is not SslStream:
                        await writer.WriteLineAsync("250-fake");
                        await writer.WriteLineAsync("250 STARTTLS");
                        break;
                    case "EHLO" or "HELO": await writer.WriteLineAsync("250 fake"); break;
                    case "STARTTLS" when OfferStartTls:
                        await writer.WriteLineAsync("220 ready");
                        stream = await TlsAsync(stream);
                        reader = new StreamReader(stream, Encoding.ASCII);
                        writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                        break;
                    case "STARTTLS": await writer.WriteLineAsync("502 not implemented"); break;
                    case "DATA":
                        await writer.WriteLineAsync("354 go ahead");
                        while (await reader.ReadLineAsync(_stop.Token) is { } body && body != ".") { }
                        Interlocked.Increment(ref _delivered);
                        await writer.WriteLineAsync("250 queued");
                        break;
                    case "QUIT": await writer.WriteLineAsync("221 bye"); return;
                    default: await writer.WriteLineAsync("250 ok"); break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { /* stopped */ }
            _stop.Dispose();
        }
    }
}
