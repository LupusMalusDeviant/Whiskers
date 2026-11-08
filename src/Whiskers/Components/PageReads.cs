using Whiskers.Models;

namespace Whiskers.Components;

/// <summary>
/// The role of each read a page guards itself. A read is not a capability (the capability catalog lists mutating
/// actions only, INV-13), so its role is fixed here by what it reads — one named entry per read, used only in a handler
/// that runs no capability (<c>PageRoleScanTests</c>). Every role in front of a capability comes from
/// <c>CapabilityCatalog.RoleOf</c> instead.
/// </summary>
public static class PageReads
{
    /// <summary><c>php artisan migrate:status</c> in a database container: the database tab is shown from Operator.</summary>
    public static AppRole MigrationStatus => AppRole.Operator;

    /// <summary><c>nginx -t</c> on a host: the nginx page's actions are Admin.</summary>
    public static AppRole NginxConfigTest => AppRole.Admin;

    /// <summary>A container's environment in the container diff: Operator, like the env tab of the detail page (MIT-37).</summary>
    public static AppRole ContainerEnvironment => AppRole.Operator;
}
