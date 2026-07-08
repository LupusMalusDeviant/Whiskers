namespace Whiskers.Models;

public class ContainerNotificationPrefs
{
    public Dictionary<string, ContainerNotifEntry> Containers { get; set; } = new();
}

public class ContainerNotifEntry
{
    public bool NotifyOnDown { get; set; } = true;
    public bool NotifyOnUnhealthy { get; set; } = true;
    public bool NotifyOnOom { get; set; } = true;
    public bool NotifyOnUpdate { get; set; } = true;
    public bool Muted { get; set; } = false; // Mute all notifications for this container
}
