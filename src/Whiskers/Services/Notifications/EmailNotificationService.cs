using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Whiskers.Configuration;
using Whiskers.Models;

namespace Whiskers.Services.Notifications;

/// <summary>
/// The e-mail channel. R1-54: SMTP (MailKit) over a socket the outbound guard opened
/// (<see cref="IOutboundTargetGuard.ConnectAsync"/>), so a mail host that resolves to loopback, link-local or another
/// denied address is refused like every HTTP channel; the configured host name stays the TLS target.
/// </summary>
public class EmailNotificationService : IEmailNotificationService
{
    private readonly IOptionsMonitor<EmailSettings> _settings;
    private readonly IOutboundTargetGuard _guard;
    private readonly NotificationThrottler _throttler;
    private readonly ILogger<EmailNotificationService> _logger;

    public EmailNotificationService(IOptionsMonitor<EmailSettings> settings, IOutboundTargetGuard guard,
        ILogger<EmailNotificationService> logger)
    {
        _settings = settings;
        _guard = guard;
        _logger = logger;
        _throttler = new NotificationThrottler(settings.CurrentValue.ThrottleMinutes);
    }

    /// <summary>R1-40: the longest one SMTP conversation (connect, TLS, auth, send, the server's final answer) may take
    /// when nothing shorter applies (the test mail). A delivery is bounded by the composite's attempt deadline (20 s),
    /// which is linked in and ends it first.
    /// Its end — or the caller's — cancels every step and also closes the connection itself, and the send is awaited
    /// until it has really stopped; the client's own I/O timeout is set a little longer as a last bound.</summary>
    internal static readonly TimeSpan DefaultSmtpDeadline = TimeSpan.FromSeconds(30);

    /// <summary>The deadline in force (tests shorten it).</summary>
    internal TimeSpan SmtpDeadline { get; init; } = DefaultSmtpDeadline;

    public IReadOnlyList<OutboundEndpoint> Targets =>
        _settings.CurrentValue is { Enabled: true } c && !string.IsNullOrWhiteSpace(c.Host)
            ? [new OutboundEndpoint("smtp", c.Host.Trim().Trim('[', ']'), c.Port)]
            : [];

    /// <summary>Sends the event to every configured recipient it has not reached yet
    /// (<see cref="NotificationEvent.DeliveredRecipients"/>). When the server takes some recipients and refuses others,
    /// the ones it took are recorded on <paramref name="evt"/> before the failure is thrown: a retry or redelivery — also
    /// after a restart, through the outbox — goes to the others only, never twice to the same inbox.</summary>
    public async Task<bool> DeliverAsync(NotificationEvent evt, CancellationToken ct)
    {
        var c = _settings.CurrentValue;
        if (!c.Enabled || string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.To)) return false;
        if (!evt.IsRedelivery && evt.DeliveredRecipients is null && _throttler.IsThrottled(evt, _settings.CurrentValue.ThrottleMinutes))
            return false;
        List<string> configured;
        try { configured = Recipients(c.To); }
        catch (FormatException)
        {
            // Retrying cannot fix a configuration: said once per event, nothing kept for redelivery.
            _logger.LogError("E-mail notification {EventType} not sent: the recipient list (To) holds an invalid address",
                evt.EventType);
            return false;
        }
        var reached = new HashSet<string>(evt.DeliveredRecipients ?? [], StringComparer.OrdinalIgnoreCase);
        var recipients = configured.Where(r => !reached.Contains(r)).ToList();
        if (recipients.Count > 0)
        {
            var (title, _) = NotificationFormatter.Describe(evt);
            try
            {
                await SendMailAsync(c, recipients, $"[Whiskers] {title}", NotificationFormatter.PlainText(evt), ct);
            }
            catch (EmailRecipientsRefusedException ex)
            {
                // Refused all: nothing went out. Refused some: the message went to the others.
                var took = recipients.Where(r => !ex.Refused.Contains(r)).ToList();
                if (took.Count > 0 && took.Count < recipients.Count)
                    evt.DeliveredRecipients = [.. reached, .. took];
                throw;
            }
        }
        _throttler.Record(evt);
        _logger.LogInformation("Email notification sent: {EventType}", evt.EventType);
        return true;
    }

    public async Task SendTestAsync()
    {
        var c = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.To)) return;
        await SendMailAsync(c, Recipients(c.To), "[Whiskers] Test", "Benachrichtigungen funktionieren.", CancellationToken.None);
    }

    private static List<string> Recipients(string to) =>
        to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => new System.Net.Mail.MailAddress(r).Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Implicit TLS on 465, STARTTLS (required) otherwise when TLS is on; plain SMTP when it is off — as the
    /// channel did before R1-54, which knew STARTTLS only.</summary>
    internal static SecureSocketOptions TlsMode(EmailSettings c) =>
        !c.UseSsl ? SecureSocketOptions.None
        : c.Port == 465 ? SecureSocketOptions.SslOnConnect
        : SecureSocketOptions.StartTls;

    private async Task SendMailAsync(EmailSettings c, IReadOnlyList<string> recipients, string subject, string body,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SmtpDeadline);
        using var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(c.From) ? c.Username : c.From));
        foreach (var to in recipients) msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };

        var host = c.Host.Trim().Trim('[', ']');
        using var client = NewClient(SmtpDeadline);
        Stream? stream = null;
        try
        {
            // The guard resolves, judges and connects; MailKit talks SMTP (and TLS, with the configured name) over that
            // socket. A refusal is thrown here, before a byte is sent.
            stream = await _guard.ConnectAsync(host, c.Port, deadline.Token);
            // The deadline or the caller's stop closes the connection too, so no step can outlast it.
            using (deadline.Token.Register(static s => AbortQuietly((Stream)s!), stream))
            {
                await client.ConnectAsync(stream, host, c.Port, TlsMode(c), deadline.Token);
                if (!string.IsNullOrWhiteSpace(c.Username) && client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                    await client.AuthenticateAsync(c.Username, c.Password, deadline.Token);
                await client.SendAsync(msg, deadline.Token);
                // The server took the message: a failed goodbye changes nothing about that — not even a stop that lands
                // during QUIT, which would otherwise report a delivered message as cancelled (R1-54 review round 3).
                try { await client.DisconnectAsync(quit: true, deadline.Token); }
                catch (Exception) { /* sent; the connection is closed by the using below */ }
            }
            if (client.Refused.Count > 0)
                throw new EmailRecipientsRefusedException(client.Refused);
        }
        catch (Exception) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The SMTP server did not complete the send within {SmtpDeadline.TotalSeconds:0} s.");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync();
        }
    }

    private static void AbortQuietly(Stream stream)
    {
        try { stream.Dispose(); }
        catch (Exception) { /* the send ends with its own error */ }
    }

    /// <summary>The SMTP client as every send uses it. The certificate chain and the configured host name are checked as
    /// before; certificate revocation is not, as <c>System.Net.Mail</c> did not check it either: MailKit's default
    /// (on) would refuse a LAN or tailnet relay whose private CA publishes no CRL or OCSP responder, and silence its
    /// alerts after an upgrade (R1-54 review round 1).</summary>
    internal static RecipientTrackingSmtpClient NewClient(TimeSpan deadline) => new()
    {
        Timeout = (int)(deadline + TimeSpan.FromSeconds(5)).TotalMilliseconds,
        CheckCertificateRevocation = false,
    };

    /// <summary>Records each recipient the server refuses instead of giving up on the first, so the message still goes
    /// to the ones it took (as <c>System.Net.Mail</c> did); none taken is an <see cref="EmailRecipientsRefusedException"/>.</summary>
    internal sealed class RecipientTrackingSmtpClient : SmtpClient
    {
        public HashSet<string> Refused { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response) =>
            Refused.Add(mailbox.Address);

        protected override void OnNoRecipientsAccepted(MimeMessage message) =>
            throw new EmailRecipientsRefusedException(Refused);
    }
}

/// <summary>The SMTP server refused <see cref="Refused"/>; the message went to the other recipients, if any.</summary>
public sealed class EmailRecipientsRefusedException(IReadOnlyCollection<string> refused)
    : Exception($"The SMTP server refused {refused.Count} recipient(s).")
{
    public IReadOnlySet<string> Refused { get; } = new HashSet<string>(refused, StringComparer.OrdinalIgnoreCase);
}
