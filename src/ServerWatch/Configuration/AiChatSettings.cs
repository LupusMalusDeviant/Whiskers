namespace ServerWatch.Configuration;

public class AiChatSettings
{
    public const string SectionName = "AiChat";

    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public string Provider { get; set; } = "openai"; // openai or anthropic
    public bool Enabled { get; set; }
    public string? ApiUrl { get; set; } // Custom endpoint URL (optional)
}
