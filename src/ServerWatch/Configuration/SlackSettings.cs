namespace ServerWatch.Configuration;

public class SlackSettings
{
    public const string SectionName = "Slack";
    /// <summary>Slack "Incoming Webhook" URL.</summary>
    public string WebhookUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int ThrottleMinutes { get; set; } = 5;
}
