namespace Whiskers.Configuration;

public class EmailSettings
{
    public const string SectionName = "Email";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    /// <summary>Use STARTTLS/SSL for the SMTP connection.</summary>
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    /// <summary>Recipient address(es), comma-separated.</summary>
    public string To { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int ThrottleMinutes { get; set; } = 5;
}
