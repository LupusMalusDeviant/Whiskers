using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;

namespace Whiskers.Services.Notifications;

public class EmailNotificationService : IEmailNotificationService
{
    private readonly IOptionsMonitor<EmailSettings> _settings;
    private readonly NotificationThrottler _throttler;
    private readonly ILogger<EmailNotificationService> _logger;

    public EmailNotificationService(IOptionsMonitor<EmailSettings> settings, ILogger<EmailNotificationService> logger)
    {
        _settings = settings;
        _logger = logger;
        _throttler = new NotificationThrottler(settings.CurrentValue.ThrottleMinutes);
    }

    /// <summary>R1-40: the longest one SMTP conversation (connect, TLS, auth, send, the server's final answer) may take.
    /// Its end — or the caller's — aborts the connection itself (<c>SmtpClient</c> does not watch the token in every
    /// phase), and the send is awaited until it has really stopped; <c>SmtpClient.Timeout</c> is set to it as a last
    /// bound and can end the send first (an <c>SmtpException</c>).</summary>
    internal static readonly TimeSpan DefaultSmtpDeadline = TimeSpan.FromSeconds(30);

    /// <summary>The deadline in force (tests shorten it).</summary>
    internal TimeSpan SmtpDeadline { get; init; } = DefaultSmtpDeadline;

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
        var reached = new HashSet<string>(evt.DeliveredRecipients ?? [], StringComparer.OrdinalIgnoreCase);
        var recipients = Recipients(c.To).Where(r => !reached.Contains(r)).ToList();
        if (recipients.Count > 0)
        {
            var (title, _) = NotificationFormatter.Describe(evt);
            try
            {
                await SendMailAsync(c, recipients, $"[Whiskers] {title}", NotificationFormatter.PlainText(evt), ct);
            }
            catch (SmtpFailedRecipientException ex)
            {
                // Refused all: nothing went out. Refused some: the message went to the others (SmtpClient sends to the
                // recipients it was given as soon as one is taken).
                var refused = Refused(ex);
                var took = recipients.Where(r => !refused.Contains(r)).ToList();
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
            .Select(r => new MailAddress(r).Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static HashSet<string> Refused(SmtpFailedRecipientException ex)
    {
        var failures = ex is SmtpFailedRecipientsException many && many.InnerExceptions.Length > 0
            ? many.InnerExceptions.Cast<SmtpFailedRecipientException>()
            : [ex];
        return failures.Select(f => (f.FailedRecipient ?? "").Trim().Trim('<', '>'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task SendMailAsync(EmailSettings c, IReadOnlyList<string> recipients, string subject, string body,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SmtpDeadline);
        using var msg = new MailMessage
        {
            From = new MailAddress(string.IsNullOrWhiteSpace(c.From) ? c.Username : c.From),
            Subject = subject,
            Body = body,
        };
        foreach (var to in recipients) msg.To.Add(to);

        using var client = new SmtpClient(c.Host, c.Port)
        {
            EnableSsl = c.UseSsl,
            Credentials = string.IsNullOrWhiteSpace(c.Username) ? null : new NetworkCredential(c.Username, c.Password),
            Timeout = (int)SmtpDeadline.TotalMilliseconds,
        };
        var send = client.SendMailAsync(msg, deadline.Token);
        try
        {
            // SmtpClient connects and waits for the server's final answer without looking at the token: the deadline or
            // the caller's stop closes the connection (Dispose aborts a send in progress), and the send is awaited until
            // it has ended — never left running behind the next attempt.
            using (deadline.Token.Register(static state => AbortQuietly((SmtpClient)state!), client))
                await send;
        }
        catch (Exception) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The SMTP server did not complete the send within {SmtpDeadline.TotalSeconds:0} s.");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    private static void AbortQuietly(SmtpClient client)
    {
        try { client.Dispose(); }
        catch (Exception) { /* the send ends with its own error */ }
    }
}
