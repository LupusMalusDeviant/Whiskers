namespace ServerWatch.Configuration;

public class TerminalSettings
{
    public const string SectionName = "Terminal";
    public bool Enabled { get; set; } = true;
    public string DefaultShell { get; set; } = "/bin/bash";
    public int MaxSessions { get; set; } = 5;
    public int IdleTimeoutMinutes { get; set; } = 30;
}
