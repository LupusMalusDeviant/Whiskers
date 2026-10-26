using Microsoft.AspNetCore.Identity;
using Whiskers.Services.Persistence;

namespace Whiskers.Startup;

/// <summary>Helpers for the local password login (audit TA-03): the rate-limit policy name, and an equal-cost
/// password check for unknown accounts so the response time does not reveal which emails exist.</summary>
public static class LocalLoginTiming
{
    public const string RateLimitPolicy = "local-login";

    // A real hash of a random value, created once. Verifying against it costs exactly what a real check costs.
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<AppUser>().HashPassword(new AppUser(), Guid.NewGuid().ToString("N")));

    public static void BurnPasswordCheck(UserManager<AppUser> users, string? password) =>
        users.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, password ?? "");
}
