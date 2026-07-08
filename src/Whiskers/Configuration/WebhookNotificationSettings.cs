namespace Whiskers.Configuration;

/// <summary>Generic outbound webhook channel: POSTs a JSON event to a URL on every notification.
/// Distinct from the inbound Webhooks feature (Services/Webhooks).</summary>
public class WebhookNotificationSettings
{
    public const string SectionName = "WebhookNotification";
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int ThrottleMinutes { get; set; } = 5;
}
