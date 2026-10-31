using Microsoft.AspNetCore.Identity;
using Whiskers.Configuration;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

/// <summary>Unattended first-admin bootstrap for local auth (F1). If <c>WHISKERS_ADMIN_EMAIL</c> +
/// <c>WHISKERS_ADMIN_PASSWORD_FILE</c> are set and no such local user exists yet, creates one. Pairs with the
/// C5 roles.json Admin seed (same email → Admin role), so the seeded user can actually manage the instance.
/// Idempotent. A weak/invalid seed password must not brick a boot where Google/OIDC or <c>AUTH_DISABLED</c> still
/// work, so it logs the reason and returns. One exception (R1-28): while the admin does not exist yet, a configured
/// password file that is missing, unreadable or empty stops the start (<see cref="SecretFileException"/>) — once the
/// admin exists the file is no longer read, so removing it after the first seed is fine. Interactive creation is the
/// W1 wizard.</summary>
public static class LocalAdminSeeder
{
    public static async Task SeedAsync(UserManager<AppUser> users, IConfiguration configuration, ILogger logger, CancellationToken ct = default)
    {
        var email = configuration["WHISKERS_ADMIN_EMAIL"]?.Trim();
        var passwordFile = configuration["WHISKERS_ADMIN_PASSWORD_FILE"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(passwordFile))
            return; // nothing configured to seed

        try
        {
            if (await users.FindByEmailAsync(email) is not null)
                return; // idempotent — the local admin already exists (its password file may be gone by now)

            // Throws SecretFileException for a missing, unreadable or empty file; let through below.
            var password = SecretSource.ReadFile("WHISKERS_ADMIN_PASSWORD_FILE", passwordFile);

            var result = await users.CreateAsync(
                new AppUser { UserName = email, Email = email, EmailConfirmed = true }, password);
            if (result.Succeeded)
                logger.LogInformation("Seeded local admin user {Email} from WHISKERS_ADMIN_PASSWORD_FILE", email);
            else
                logger.LogWarning("Could not seed local admin user {Email} (check the password policy): {Errors}",
                    email, string.Join("; ", result.Errors.Select(e => e.Description)));
        }
        catch (Exception ex) when (ex is not SecretFileException)
        {
            // Never brick the boot — the other auth paths must still come up.
            logger.LogWarning(ex, "Local admin seed failed for {Email}", email);
        }
    }
}
