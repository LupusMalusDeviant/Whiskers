using Microsoft.AspNetCore.Identity;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

/// <summary>Unattended first-admin bootstrap for local auth (F1). If <c>WHISKERS_ADMIN_EMAIL</c> +
/// <c>WHISKERS_ADMIN_PASSWORD_FILE</c> are set and no such local user exists yet, creates one. Pairs with the
/// C5 roles.json Admin seed (same email → Admin role), so the seeded user can actually manage the instance.
/// Idempotent; and it NEVER throws — a weak/invalid seed password must not brick a boot where Google/OIDC or
/// <c>AUTH_DISABLED</c> still work, so it logs the reason and returns. Interactive creation is the W1 wizard.</summary>
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
            if (!File.Exists(passwordFile))
            {
                logger.LogWarning("WHISKERS_ADMIN_PASSWORD_FILE set but the file does not exist ({Path}) — skipping local admin seed", passwordFile);
                return;
            }
            var password = (await File.ReadAllTextAsync(passwordFile, ct)).Trim();
            if (string.IsNullOrEmpty(password))
            {
                logger.LogWarning("WHISKERS_ADMIN_PASSWORD_FILE is empty — skipping local admin seed");
                return;
            }

            if (await users.FindByEmailAsync(email) is not null)
                return; // idempotent — the local admin already exists

            var result = await users.CreateAsync(
                new AppUser { UserName = email, Email = email, EmailConfirmed = true }, password);
            if (result.Succeeded)
                logger.LogInformation("Seeded local admin user {Email} from WHISKERS_ADMIN_PASSWORD_FILE", email);
            else
                logger.LogWarning("Could not seed local admin user {Email} (check the password policy): {Errors}",
                    email, string.Join("; ", result.Errors.Select(e => e.Description)));
        }
        catch (Exception ex)
        {
            // Never brick the boot — the other auth paths must still come up.
            logger.LogWarning(ex, "Local admin seed failed for {Email}", email);
        }
    }
}
