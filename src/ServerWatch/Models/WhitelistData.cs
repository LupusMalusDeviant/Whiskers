namespace ServerWatch.Models;

public class WhitelistData
{
    public bool Enabled { get; set; }
    public List<string> Emails { get; set; } = new();
}
