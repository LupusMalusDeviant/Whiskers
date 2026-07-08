namespace Whiskers.Configuration;

public class TelegramSettings
{
    public const string SectionName = "Telegram";
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int ThrottleMinutes { get; set; } = 5;
}
