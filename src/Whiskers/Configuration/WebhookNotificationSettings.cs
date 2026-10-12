namespace Whiskers.Configuration;

/// <summary>Generic outbound webhook channel: POSTs a JSON event to a URL on every notification.
/// Distinct from the inbound Webhooks feature (Services/Webhooks).</summary>
public class WebhookNotificationSettings
{
    public const string SectionName = "WebhookNotification";
    public string Url { get; set; } = string.Empty;
    /// <summary>Optional HMAC secret. When set, every POST carries <c>X-Webhook-Timestamp</c> +
    /// <c>X-Webhook-Signature-V2</c> (hex HMAC-SHA256 of "&lt;timestamp&gt;.&lt;body&gt;") and the GitHub-style
    /// <c>X-Hub-Signature-256</c>, so the receiver can reject forged or replayed alerts. Empty = unsigned.</summary>
    public string Secret { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int ThrottleMinutes { get; set; } = 5;
}
