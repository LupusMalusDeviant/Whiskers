namespace ServerWatch.Configuration;

public class NtfySettings
{
    public const string SectionName = "Ntfy";
    /// <summary>ntfy server base URL (public ntfy.sh or a self-hosted instance).</summary>
    public string ServerUrl { get; set; } = "https://ntfy.sh";
    public string Topic { get; set; } = string.Empty;
    /// <summary>Optional access token for protected topics (sent as a Bearer header).</summary>
    public string Token { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int ThrottleMinutes { get; set; } = 5;
}
