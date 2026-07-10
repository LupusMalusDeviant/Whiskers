using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Whiskers.Services.Persistence;

/// <summary>A local username/password user (F1). Email is the key that links into the app's existing
/// authorization system: roles live in <c>roles.json</c> (via <c>IRoleService</c>, keyed on the email claim)
/// and access is gated by the email whitelist — so there is deliberately NO Identity role table here.</summary>
public class AppUser : IdentityUser
{
}

/// <summary>Separate EF context for local Identity users, against the SAME database as
/// <see cref="MetricsDbContext"/> (ADR-0004 dual-provider migrations; a distinct
/// <c>__IdentityMigrationsHistory</c> table keeps this context's migrations off the metrics
/// legacy-baseline path). <see cref="IdentityUserContext{TUser}"/> models the user / claims / logins /
/// tokens tables only — NO role tables, since roles are resolved from <c>roles.json</c>.</summary>
public class WhiskersIdentityDbContext : IdentityUserContext<AppUser>
{
    public WhiskersIdentityDbContext(DbContextOptions<WhiskersIdentityDbContext> options) : base(options) { }
    // No OnModelCreating override: the base IdentityUserContext models the Identity schema. Deliberately no
    // UtcDateTimeConverter convention (unlike MetricsDbContext) — Identity uses DateTimeOffset?, not DateTime.
}
