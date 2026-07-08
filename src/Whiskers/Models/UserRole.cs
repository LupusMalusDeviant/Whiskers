namespace Whiskers.Models;

public enum AppRole
{
    Viewer,     // Nur lesen — Dashboard, Logs, Stats
    Operator,   // Lesen + Container starten/stoppen/neustarten, Queries
    Admin       // Alles — Settings, Server, Rollen, Destructive Ops
}

public class UserRoleEntry
{
    public string Email { get; set; } = "";
    public AppRole Role { get; set; } = AppRole.Viewer;
}

public class UserRoleData
{
    public List<UserRoleEntry> Roles { get; set; } = new();
    public AppRole DefaultRole { get; set; } = AppRole.Viewer; // Role for users not in list
}
