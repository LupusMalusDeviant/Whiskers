namespace ServerWatch.Configuration;

public class CoolifySettings
{
    public const string SectionName = "Coolify";
    public string ApiUrl { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public bool Enabled { get; set; }
    public int PollingIntervalSeconds { get; set; } = 30;
}
