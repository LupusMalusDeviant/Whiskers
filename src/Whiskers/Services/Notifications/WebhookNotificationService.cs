using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;

namespace Whiskers.Services.Notifications;

/// <summary>Generic outbound webhook: POSTs a JSON event to a configured URL on every notification.</summary>
public class WebhookNotificationService : IWebhookNotificationService
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<WebhookNotificationSettings> _settings;
    private readonly NotificationThrottler _throttler;
    private readonly ILogger<WebhookNotificationService> _logger;

    public WebhookNotificationService(HttpClient http, IOptionsMonitor<WebhookNotificationSettings> settings, ILogger<WebhookNotificationService> logger)
    {
        _http = http;
        _settings = settings;
        _logger = logger;
        _throttler = new NotificationThrottler(settings.CurrentValue.ThrottleMinutes);
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        var c = _settings.CurrentValue;
        if (!c.Enabled || string.IsNullOrWhiteSpace(c.Url)) return;
        if (_throttler.IsThrottled(evt, _settings.CurrentValue.ThrottleMinutes)) return;
        try
        {
            await PostAsync(c, evt);
            _throttler.Record(evt);
            _logger.LogInformation("Webhook notification sent: {EventType}", evt.EventType);
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to send webhook notification"); }
    }

    public async Task SendTestAsync()
    {
        var c = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(c.Url)) return;
        await PostAsync(c, new NotificationEvent { EventType = "test", ContainerName = "Whiskers", ImageInfo = "Benachrichtigungen funktionieren." });
    }

    private async Task PostAsync(WebhookNotificationSettings c, NotificationEvent evt)
    {
        var (title, severity) = NotificationFormatter.Describe(evt);
        var payload = new
        {
            @event = evt.EventType,
            title,
            detail = NotificationFormatter.Detail(evt),
            severity,
            server = evt.ServerName,
            serverId = evt.ServerId,
            container = evt.ContainerName,
            image = evt.Image,
            timestamp = evt.Timestamp,
        };
        // Serialize once and sign exactly the bytes that go on the wire.
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var request = new HttpRequestMessage(HttpMethod.Post, c.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var (name, value) in SignatureHeaders(c.Secret, body, DateTimeOffset.UtcNow))
            request.Headers.TryAddWithoutValidation(name, value);

        var resp = await _http.SendAsync(request);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>The signature headers for <paramref name="body"/>, or none when no secret is configured.
    /// V2 binds the timestamp into the MAC so a captured request cannot be replayed later; the GitHub-style
    /// header is for receivers that only know that scheme.</summary>
    internal static IReadOnlyList<(string Name, string Value)> SignatureHeaders(string? secret, byte[] body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret)) return [];

        var key = Encoding.UTF8.GetBytes(secret);
        var timestamp = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signed = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        return
        [
            ("X-Webhook-Timestamp", timestamp),
            ("X-Webhook-Signature-V2", Convert.ToHexStringLower(HMACSHA256.HashData(key, signed))),
            ("X-Hub-Signature-256", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(key, body))),
        ];
    }
}
