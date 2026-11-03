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

    /// <summary>R1-40: the longest one SMTP conversation (connect, TLS, auth, send) may take. The cancellation linked to
    /// the caller's token is what enforces it (a <c>TimeoutException</c>); <c>SmtpClient.Timeout</c> is set to it as well
    /// and, depending on the runtime, can end the send first (an <c>SmtpException</c>).</summary>
    internal static readonly TimeSpan DefaultSmtpDeadline = TimeSpan.FromSeconds(30);

    /// <summary>The deadline in force (tests shorten it).</summary>
    internal TimeSpan SmtpDeadline { get; init; } = DefaultSmtpDeadline;

    public async Task<bool> DeliverAsync(NotificationEvent evt, CancellationToken ct)
    {
        var c = _settings.CurrentValue;
        if (!c.Enabled || string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.To)) return false;
        if (!evt.IsRedelivery && _throttler.IsThrottled(evt, _settings.CurrentValue.ThrottleMinutes)) return false;
        var (title, _) = NotificationFormatter.Describe(evt);
        await SendMailAsync(c, $"[Whiskers] {title}", NotificationFormatter.PlainText(evt), ct);
        _throttler.Record(evt);
        _logger.LogInformation("Email notification sent: {EventType}", evt.EventType);
        return true;
    }

    public async Task SendTestAsync()
    {
        var c = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.To)) return;
        await SendMailAsync(c, "[Whiskers] Test", "Benachrichtigungen funktionieren.", CancellationToken.None);
    }

    private async Task SendMailAsync(EmailSettings c, string subject, string body, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SmtpDeadline);
        using var msg = new MailMessage
        {
            From = new MailAddress(string.IsNullOrWhiteSpace(c.From) ? c.Username : c.From),
            Subject = subject,
            Body = body,
        };
        foreach (var to in c.To.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            msg.To.Add(to);

        using var client = new SmtpClient(c.Host, c.Port)
        {
            EnableSsl = c.UseSsl,
            Credentials = string.IsNullOrWhiteSpace(c.Username) ? null : new NetworkCredential(c.Username, c.Password),
            Timeout = (int)SmtpDeadline.TotalMilliseconds,
        };
        try
        {
            await client.SendMailAsync(msg, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The SMTP server did not complete the send within {SmtpDeadline.TotalSeconds:0} s.");
        }
    }
}
