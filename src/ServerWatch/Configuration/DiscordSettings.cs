namespace ServerWatch.Configuration;

public class DiscordSettings
{
    public const string SectionName = "Discord";
    /// <summary>Discord channel "Incoming Webhook" URL.</summary>
    public string WebhookUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int ThrottleMinutes { get; set; } = 5;
}
