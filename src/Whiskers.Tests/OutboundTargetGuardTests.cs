using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>
/// R1-54 (INV-60): the outbound guard of the notification channels judges the address it actually connects to — its
/// own resolve-filter-connect in the HTTP handler's <c>ConnectCallback</c> and under the SMTP connection. Default-denied
/// ranges in every canonical form, a name with a public and an internal answer, a name that rebinds, redirects and
/// proxies are refused; an exception is exact (address and port, or name, port and addresses) and its removal also ends
/// the keep-alive connection it allowed. A refusal is final and visible, never carries a URL path. No test uses real
/// DNS or leaves the machine: the resolver answers from the test, and the connector reaches a local listener for a
/// "public" address while recording the address it was asked to connect to.
/// </summary>
public sealed class OutboundTargetGuardTests : IAsyncDisposable
{
    private const string Public = "93.184.215.14";

    private readonly OutboundTestDoubles.Exceptions _exceptions = new();
    private readonly OutboundTestDoubles.Resolver _resolver = new();
    private readonly OutboundTestDoubles.Connector _connector = new();
    private readonly OutboundTestDoubles.HttpServer _server = new();
    private readonly OutboundTargetGuard _guard;

    public OutboundTargetGuardTests()
    {
        _connector.Port = _server.Port;
        _guard = new OutboundTargetGuard(_exceptions, _resolver) { Connector = _connector.ConnectAsync };
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private OutboundHttp Http(IWebProxy? systemProxy = null) => new(_guard, _exceptions, systemProxy);

    private static WebhookNotificationService Webhook(OutboundHttp http, string url) =>
        new(http.CreateClient(), new StaticOptionsMonitor<WebhookNotificationSettings>(new WebhookNotificationSettings
        {
            Enabled = true, Url = url, ThrottleMinutes = 0,
        }), NullLogger<WebhookNotificationService>.Instance);

    private static NotificationEvent Failed() => new() { EventType = "auto_update_failed", ContainerName = "web" };

    // ---------------------------------------------------------------- the denied ranges, canonical forms included

    public static TheoryData<string> DeniedLiterals() =>
    [
        "127.0.0.1", "127.255.255.254", "::1", "0.0.0.0", "0.1.2.3", "::",
        "169.254.169.254", "fe80::1", "10.0.0.1", "172.16.0.1", "172.31.255.255", "192.168.1.1",
        "100.64.0.1", "100.127.255.254", "fd7a:115c:a1e0::1", "fc00::1", "fdff::1", "fec0::1",
        "240.0.0.1", "255.255.255.255", "192.0.0.8", "198.18.0.1", "198.19.255.255",
        "192.0.2.1", "198.51.100.7", "203.0.113.9", "2001:db8::1", "192.88.99.1", "2001:2::1", "2001:20::1",
        "100::1", "224.0.0.251", "239.255.255.250", "ff02::1",
        // Local-use NAT64 is denied as a whole: this one embeds 10.0.0.1 under a /64 translator.
        "64:ff9b:1:0:a:0:100:0", "64:ff9b:1::8.8.8.8",
        // Teredo is denied outright, whatever it embeds.
        "2001:0:4136:e378:8000:63bf:3fff:fdd2",
        // Canonical forms: IPv4-mapped, IPv4-compatible, NAT64 64:ff9b::/96, 6to4 — judged by the IPv4 they embed.
        "::ffff:127.0.0.1", "[::ffff:127.0.0.1]", "::ffff:169.254.169.254", "::127.0.0.1", "::10.0.0.1",
        "64:ff9b::7f00:1", "64:ff9b::a9fe:a9fe", "2002:7f00:1::1", "2002:a9fe:a9fe::1", "2002:c0a8:101::1",
        // Decimal, octal and hex IPv4 literals, also shortened.
        "2130706433", "0177.0.0.1", "0x7f.0.0.1", "0x7f000001", "127.1", "0x7f.1", "0251.0376.0251.0376", "2852039166",
        // Zone-scoped literals are judged without the zone.
        "fe80::1%eth0", "[fe80::1%25eth0]", "fe80::1%3",
    ];

    [Theory]
    [MemberData(nameof(DeniedLiterals))]
    public async Task Every_denied_form_is_refused_before_anything_is_connected(string host)
    {
        await Assert.ThrowsAsync<OutboundTargetRefusedException>(() => _guard.ConnectAsync(host, 80, CancellationToken.None));

        Assert.Empty(_connector.Snapshot());
        Assert.Empty(_resolver.Asked);   // a literal is never sent to a resolver
    }

    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("fe80::1%3")]
    [InlineData("::127.0.0.1")]
    public async Task A_name_resolving_to_a_denied_form_is_refused(string answer)
    {
        _resolver.Answer("hooks.example.com", answer);

        await Assert.ThrowsAsync<OutboundTargetRefusedException>(() => _guard.ConnectAsync("hooks.example.com", 443, CancellationToken.None));

        Assert.Empty(_connector.Snapshot());
    }

    [Theory]
    [InlineData(Public)]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("64:ff9b::808:808")]          // NAT64 of 8.8.8.8
    [InlineData("2002:808:808::1")]           // 6to4 of 8.8.8.8
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("172.32.0.1")]                // just outside 172.16/12
    [InlineData("100.128.0.1")]               // just outside 100.64/10
    public void A_public_address_is_not_denied(string address) =>
        Assert.Null(OutboundAddressPolicy.DeniedRange(IPAddress.Parse(address)));

    [Theory]
    [InlineData("2130706433", "127.0.0.1")]
    [InlineData("0177.0.0.1", "127.0.0.1")]
    [InlineData("0x7f.1", "127.0.0.1")]
    [InlineData("[::ffff:10.1.2.3]", "10.1.2.3")]
    [InlineData("64:ff9b::a00:1", "10.0.0.1")]
    [InlineData("2002:a00:1::", "10.0.0.1")]
    [InlineData("fe80::1%eth0", "fe80::1")]
    public void Literals_are_canonicalized_before_they_are_judged(string literal, string canonical)
    {
        Assert.True(OutboundAddressPolicy.TryParseLiteral(literal, out var parsed));
        Assert.Equal(IPAddress.Parse(canonical), OutboundAddressPolicy.Canonicalize(parsed));
    }

    // ---------------------------------------------------------------- the address actually connected to

    [Fact]
    public async Task A_name_with_one_public_and_one_internal_answer_is_refused_as_a_whole()
    {
        _resolver.Answer("mixed.example.com", Public, "10.0.0.1").Answer("mixed2.example.com", "127.0.0.1", Public);
        using var http = Http();

        await Assert.ThrowsAnyAsync<Exception>(() => Webhook(http, "http://mixed.example.com/hook").DeliverAsync(Failed(), CancellationToken.None));
        await Assert.ThrowsAsync<OutboundTargetRefusedException>(() => _guard.ConnectAsync("mixed2.example.com", 80, CancellationToken.None));

        Assert.Empty(_connector.Snapshot());
        Assert.Equal(0, _server.Requests);
    }

    [Fact]
    public async Task A_name_that_rebinds_between_check_and_connect_never_reaches_the_internal_address()
    {
        // First answer public, every later answer loopback. Each request opens its own connection (the server closes).
        _resolver.Answer("rebind.example.com", Public).Answer("rebind.example.com", "127.0.0.1");
        _server.Close = true;
        using var http = Http();
        var hook = Webhook(http, "http://rebind.example.com/hook");

        Assert.True(await hook.DeliverAsync(Failed(), CancellationToken.None));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => hook.DeliverAsync(Failed(), CancellationToken.None));

        Assert.NotNull(OutboundTargetRefusedException.Find(error));
        // The guard connected to the address it judged, once — never to the rebound one.
        Assert.Equal([new IPEndPoint(IPAddress.Parse(Public), 80)], _connector.Snapshot());
        Assert.Equal(1, _server.Requests);
    }

    [Fact]
    public async Task An_excepted_name_that_rebinds_to_the_metadata_address_is_refused()
    {
        _exceptions.Set(new OutboundExceptionEntry { Host = "ntfy.lan", Port = 8080, Addresses = ["10.0.0.5"] });
        _resolver.Answer("ntfy.lan", "10.0.0.5").Answer("ntfy.lan", "169.254.169.254");
        _server.Close = true;
        using var http = Http();
        var hook = Webhook(http, "http://ntfy.lan:8080/alerts");

        Assert.True(await hook.DeliverAsync(Failed(), CancellationToken.None));     // the excepted address
        var error = await Assert.ThrowsAnyAsync<Exception>(() => hook.DeliverAsync(Failed(), CancellationToken.None));

        Assert.Contains("169.254.169.254", OutboundTargetRefusedException.Find(error)!.Message);
        Assert.Equal([new IPEndPoint(IPAddress.Parse("10.0.0.5"), 8080)], _connector.Snapshot());
    }

    [Fact]
    public async Task An_exception_is_exact_in_address_and_port()
    {
        _exceptions.Set(new OutboundExceptionEntry { Host = "10.0.0.5", Port = 8080 });

        await using (await _guard.ConnectAsync("10.0.0.5", 8080, CancellationToken.None)) { }
        await Assert.ThrowsAsync<OutboundTargetRefusedException>(() => _guard.ConnectAsync("10.0.0.5", 8081, CancellationToken.None));
        await Assert.ThrowsAsync<OutboundTargetRefusedException>(() => _guard.ConnectAsync("10.0.0.6", 8080, CancellationToken.None));
        // An IP entry applies to that literal, not to a name that happens to resolve to it.
        _resolver.Answer("other.lan", "10.0.0.5");
        await Assert.ThrowsAsync<OutboundTargetRefusedException>(() => _guard.ConnectAsync("other.lan", 8080, CancellationToken.None));

        Assert.Equal([new IPEndPoint(IPAddress.Parse("10.0.0.5"), 8080)], _connector.Snapshot());
    }

    [Theory]
    [InlineData("10.0.0.0/24", 443, new string[0])]
    [InlineData("*.lan", 443, new[] { "10.0.0.5" })]
    [InlineData("10.0.0.1-10.0.0.9", 443, new string[0])]
    [InlineData("ntfy.lan", 443, new string[0])]                       // a name needs its addresses
    [InlineData("ntfy.lan", 443, new[] { "10.0.0.0/8" })]
    [InlineData("ntfy.lan", 443, new[] { "10.0.0.*" })]
    [InlineData("ntfy.lan", 0, new[] { "10.0.0.5" })]
    [InlineData("ntfy.lan", 70000, new[] { "10.0.0.5" })]
    [InlineData("10.0.0.5", 443, new[] { "10.0.0.6" })]
    [InlineData("", 443, new string[0])]
    public void Ranges_wildcards_and_incomplete_entries_are_not_accepted(string host, int port, string[] addresses) =>
        Assert.Throws<ArgumentException>(() =>
            OutboundException.Parse(new OutboundExceptionEntry { Host = host, Port = port, Addresses = [.. addresses] }));

    // ---------------------------------------------------------------- the HTTP handler: redirects, proxies, pooling

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::ffff:127.0.0.1]/admin")]
    public async Task A_redirect_is_refused_and_never_followed(string location)
    {
        _resolver.Answer("hooks.example.com", Public);
        _server.Respond = () => (302, [$"Location: {location}"]);
        using var http = Http();

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            Webhook(http, "http://hooks.example.com/services/T000/B000/XXXXSECRET").DeliverAsync(Failed(), CancellationToken.None));

        var refused = Assert.IsType<OutboundRedirectRefusedException>(OutboundTargetRefusedException.Find(error));
        Assert.DoesNotContain("XXXXSECRET", refused.Message);
        Assert.Equal(1, _server.Requests);
        Assert.Equal([new IPEndPoint(IPAddress.Parse(Public), 80)], _connector.Snapshot());
    }

    [Fact]
    public void The_inner_handler_uses_no_proxy_and_follows_no_redirect()
    {
        using var handler = OutboundHttp.NewInnerHandler(_guard);

        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task A_configured_proxy_receives_nothing_and_a_failed_direct_connection_says_so()
    {
        await using var proxy = new OutboundTestDoubles.HttpServer();
        _resolver.Answer("hooks.slack.com", Public);
        _server.Close = true;   // every request connects anew
        using var http = Http(new WebProxy($"http://127.0.0.1:{proxy.Port}"));

        Assert.True(await Webhook(http, "http://hooks.slack.com/services/T0/B0/SECRET").DeliverAsync(Failed(), CancellationToken.None));

        _connector.Fail = true;   // the direct path is gone, as on a host that may only leave through its proxy
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            Webhook(http, "http://hooks.slack.com/services/T0/B0/SECRET").DeliverAsync(Failed(), CancellationToken.None));

        var reason = Assert.IsType<OutboundProxyNotUsedException>(error);
        Assert.Contains("proxy", reason.Message);
        Assert.DoesNotContain("SECRET", reason.Message);
        Assert.Equal(0, proxy.Connections);
        Assert.Equal(1, _server.Requests);
    }

    [Fact]
    public async Task Removing_an_exception_also_ends_the_keep_alive_connection_it_allowed()
    {
        _exceptions.Set(new OutboundExceptionEntry { Host = "10.1.2.3", Port = 8080 });
        using var http = Http();
        var hook = Webhook(http, "http://10.1.2.3:8080/hook");

        Assert.True(await hook.DeliverAsync(Failed(), CancellationToken.None));
        Assert.True(await hook.DeliverAsync(Failed(), CancellationToken.None));
        Assert.Equal(1, _server.Connections);          // the second request reused the pooled connection

        _exceptions.Set();                              // the Admin removes the exception
        var error = await Assert.ThrowsAnyAsync<Exception>(() => hook.DeliverAsync(Failed(), CancellationToken.None));

        Assert.NotNull(OutboundTargetRefusedException.Find(error));
        Assert.Equal(2, _server.Requests);              // the third never went out over the old connection
    }

    // ---------------------------------------------------------------- SMTP

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task An_smtp_host_resolving_to_loopback_or_link_local_is_refused(string answer)
    {
        await using var smtp = new NotificationSmtpTests.FakeSmtp();
        _connector.Port = smtp.Port;
        _resolver.Answer("smtp.example.com", answer);
        var email = Email("smtp.example.com", 587);

        await Assert.ThrowsAsync<OutboundTargetRefusedException>(() => email.DeliverAsync(Failed(), CancellationToken.None));

        Assert.Empty(_connector.Snapshot());
        Assert.Empty(smtp.Conversations);
    }

    [Fact]
    public async Task A_loopback_smtp_literal_is_refused_until_it_is_excepted()
    {
        await using var smtp = new NotificationSmtpTests.FakeSmtp();
        _connector.Port = smtp.Port;
        var email = Email("127.0.0.1", 2525);

        await Assert.ThrowsAsync<OutboundTargetRefusedException>(() => email.DeliverAsync(Failed(), CancellationToken.None));
        Assert.Empty(smtp.Conversations);

        _exceptions.Set(new OutboundExceptionEntry { Host = "127.0.0.1", Port = 2525 });
        Assert.True(await email.DeliverAsync(Failed(), CancellationToken.None));
        Assert.Equal(1, smtp.Received("ops@example.com"));
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, Assert.Single(_connector.Snapshot()).AddressFamily);
    }

    [Theory]
    [InlineData(false, 587, MailKit.Security.SecureSocketOptions.None)]
    [InlineData(true, 587, MailKit.Security.SecureSocketOptions.StartTls)]
    [InlineData(true, 465, MailKit.Security.SecureSocketOptions.SslOnConnect)]
    public void The_tls_mode_follows_the_settings(bool useSsl, int port, MailKit.Security.SecureSocketOptions expected) =>
        Assert.Equal(expected, EmailNotificationService.TlsMode(new EmailSettings { UseSsl = useSsl, Port = port }));

    [Fact]
    public void The_smtp_client_validates_the_certificate_but_not_its_revocation_as_before()
    {
        // System.Net.Mail did not check revocation; MailKit does by default and would refuse a relay with a private CA
        // that publishes no CRL. The chain and the name are still validated (no custom callback that accepts all).
        using var client = EmailNotificationService.NewClient(TimeSpan.FromSeconds(20));
        Assert.False(client.CheckCertificateRevocation);
        Assert.Null(client.ServerCertificateValidationCallback);
        Assert.Equal(25_000, client.Timeout);
    }

    [Fact]
    public async Task With_tls_on_a_server_that_offers_no_starttls_is_refused_not_used_in_plain()
    {
        // Review round 2: TLS on means STARTTLS is required. A relay (or a man in the middle) that does not offer it must
        // not get the message in plain text.
        await using var smtp = new NotificationSmtpTests.FakeSmtp();
        _connector.Port = smtp.Port;
        _exceptions.Set(new OutboundExceptionEntry { Host = "127.0.0.1", Port = 587 });
        var email = Email("127.0.0.1", 587, useSsl: true);

        await Assert.ThrowsAnyAsync<Exception>(() => email.DeliverAsync(Failed(), CancellationToken.None));

        Assert.Equal(0, smtp.Received("ops@example.com"));
        Assert.All(smtp.Conversations, c => Assert.Empty(c));   // no recipient was ever named in plain text
    }

    [Fact]
    public async Task Implicit_tls_on_465_refuses_a_certificate_that_does_not_validate()
    {
        // Review round 2: the certificate is validated (chain and name) on the guarded socket. A self-signed one —
        // what an interceptor would present — ends the conversation before anything is sent.
        await using var tls = new SelfSignedTlsServer("smtp.example.com");
        _connector.Port = tls.Port;
        _exceptions.Set(new OutboundExceptionEntry { Host = "127.0.0.1", Port = 465 });
        var email = Email("127.0.0.1", 465, useSsl: true);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => email.DeliverAsync(Failed(), CancellationToken.None));

        Assert.Contains(Chain(error), e => e is MailKit.Security.SslHandshakeException
                                           || e is System.Security.Authentication.AuthenticationException);
        Assert.True(await tls.HandshakeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10)), "the client spoke TLS first");
        Assert.False(tls.ReceivedApplicationData);
    }

    private static IEnumerable<Exception> Chain(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            yield return x;
            if (x is AggregateException a) foreach (var i in a.InnerExceptions.SelectMany(Chain)) yield return i;
        }
    }

    /// <summary>A TLS listener with a self-signed certificate: the server side of implicit TLS, untrusted by design.</summary>
    private sealed class SelfSignedTlsServer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate;
        private readonly Task _loop;
        public int Port { get; }
        public TaskCompletionSource<bool> HandshakeAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReceivedApplicationData { get; private set; }

        public SelfSignedTlsServer(string name)
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={name}", key,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            san.AddDnsName(name);
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            _certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
                created.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12), null);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var ssl = new System.Net.Security.SslStream(client.GetStream());
                HandshakeAttempted.TrySetResult(true);
                try
                {
                    await ssl.AuthenticateAsServerAsync(_certificate);
                    await ssl.WriteAsync("220 fake ESMTP\r\n"u8.ToArray());
                    var buffer = new byte[256];
                    if (await ssl.ReadAsync(buffer) > 0) ReceivedApplicationData = true;
                }
                catch { /* the client refused the certificate */ }
            }
            catch { /* listener stopped */ }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _loop; } catch { }
            _certificate.Dispose();
        }
    }

    private EmailNotificationService Email(string host, int port, bool useSsl = false) =>
        new(new StaticOptionsMonitor<EmailSettings>(new EmailSettings
        {
            Enabled = true, Host = host, Port = port, UseSsl = useSsl, From = "whiskers@example.com", To = "ops@example.com",
            ThrottleMinutes = 0,
        }), _guard, NullLogger<EmailNotificationService>.Instance) { SmtpDeadline = TimeSpan.FromSeconds(10) };

    // ---------------------------------------------------------------- a refusal is final and visible

    [Fact]
    public async Task A_refused_delivery_is_not_retried_not_kept_and_logged_without_the_url_path()
    {
        _resolver.Answer("hooks.example.com", "169.254.169.254");
        var logs = new NotificationDeliveryTests.ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var outbox = new NotificationOutbox(new DataPathOptions(Path.Combine(Path.GetTempPath(), $"sw-r154-{Guid.NewGuid():N}")),
            NullLogger<NotificationOutbox>.Instance);
        using var http = Http();
        var composite = NotificationDeliveryTests.Composite(
            [Webhook(http, "http://hooks.example.com/services/T0/B0/PATHSECRET?token=QUERYSECRET")], outbox,
            logger: factory.CreateLogger<CompositeNotificationService>());

        await composite.SendAsync(Failed());          // critical: a failure would be kept for redelivery

        Assert.Single(_resolver.Asked);                 // one attempt, no retry
        Assert.Equal(0, outbox.Status.Pending);         // not kept
        var line = Assert.Single(logs.Lines, l => l.Contains("refused", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("hooks.example.com:80", line);
        Assert.Contains("169.254.169.254", line);
        Assert.DoesNotContain("PATHSECRET", string.Join("\n", logs.Lines));
        Assert.DoesNotContain("QUERYSECRET", string.Join("\n", logs.Lines));
    }

    [Fact]
    public async Task The_retry_helper_gives_up_on_a_refusal_at_once()
    {
        var calls = 0;
        var (ok, last) = await NotificationRetry.TrySendAsync(_ =>
        {
            calls++;
            throw new HttpRequestException("wrapped", new OutboundTargetRefusedException("refused"));
        }, maxAttempts: 3, TimeSpan.FromSeconds(5), TimeSpan.Zero);

        Assert.False(ok);
        Assert.Equal(1, calls);
        Assert.NotNull(OutboundTargetRefusedException.Find(last));
    }
}
