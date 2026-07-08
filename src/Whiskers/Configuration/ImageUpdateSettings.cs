namespace Whiskers.Configuration;

public class ImageUpdateSettings
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalHours { get; set; } = 6;
    public bool NotifyOnUpdate { get; set; } = true;
}
