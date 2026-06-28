using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models;

namespace ServerWatch.Services.Notifications;

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

    public async Task SendAsync(NotificationEvent evt)
    {
        var c = _settings.CurrentValue;
        if (!c.Enabled || string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.To)) return;
        if (_throttler.IsThrottled(evt.ContainerId, evt.EventType)) return;
        try
        {
            var (title, _) = NotificationFormatter.Describe(evt);
            await SendMailAsync(c, $"[ServerWatch] {title}", NotificationFormatter.PlainText(evt));
            _throttler.Record(evt.ContainerId, evt.EventType);
            _logger.LogInformation("Email notification sent: {EventType}", evt.EventType);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send email notification"); }
    }

    public async Task SendTestAsync()
    {
        var c = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.To)) return;
        await SendMailAsync(c, "[ServerWatch] Test", "Benachrichtigungen funktionieren.");
    }

    private static async Task SendMailAsync(EmailSettings c, string subject, string body)
    {
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
        };
        await client.SendMailAsync(msg);
    }
}
