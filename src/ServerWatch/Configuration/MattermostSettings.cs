namespace ServerWatch.Configuration;

public class MattermostSettings
{
    public const string SectionName = "Mattermost";
    public string WebhookUrl { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string BotUsername { get; set; } = "ServerWatch";
    public string BotIconUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int ThrottleMinutes { get; set; } = 5;
}
