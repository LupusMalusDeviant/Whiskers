using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// R1-48 (review NIT): the app starts with the scope guard enforcing in every auth mode — disabled, local sign-in and
/// OIDC — and nothing it runs at start-up or in its first moments (initializers, hosted services, background loops)
/// writes through a guarded service outside a gateway decision: the guard would have refused it and recorded it.
/// </summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class MutationEnforcementBootTests
{
    public static TheoryData<string> AuthModes => ["disabled", "local", "oidc"];

    [Theory]
    [MemberData(nameof(AuthModes))]
    public async Task The_app_boots_enforcing_and_starts_nothing_outside_a_decision(string mode)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-enforce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var previousDataDir = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        List<KeyValuePair<string, string?>> config = mode switch
        {
            "disabled" => [.. BootSettings.AuthDisabled],
            "local" => [new("Auth:Disabled", "false")],
            _ =>
            [
                new("Auth:Disabled", "false"),
                new("Oidc:Enabled", "true"),
                new("Oidc:Authority", "https://id.example.com"),
                new("Oidc:ClientId", "whiskers"),
                new("Oidc:ClientSecret", "test-only"),
            ],
        };

        try
        {
            using var bootSettings = BootSettings.Apply(config);   // read eagerly by Program
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Development");
                    b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
                });
            using var client = factory.CreateClient();   // builds the host and runs start-up
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
            BootSettings.AssertBoot(factory.Services, config);

            var monitor = factory.Services.GetRequiredService<IMutationScopeMonitor>();
            Assert.True(monitor.Enforce);
            // Give the background loops their first pass.
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Empty(monitor.Snapshot());

            if (mode == "disabled") return;   // no wizard in that mode
            // The first-run wizard completes under enforcement through its narrow first-admin methods (option a), is
            // audited, and leaves the sign-in whitelist and the roles to the gateway from then on.
            var setup = factory.Services.GetRequiredService<Whiskers.Services.Setup.ISetupStateService>();
            var token = (await File.ReadAllTextAsync(new Whiskers.Configuration.DataPathOptions(dataDir).SetupTokenFile)).Trim();
            var done = await setup.CompleteSetupAsync(new Whiskers.Services.Setup.SetupAdminRequest
            {
                IsLocal = mode == "local", Email = "first@example.com", Password = "Sup3rSecret!!-boot", SetupToken = token,
            });
            Assert.Equal(Whiskers.Services.Setup.SetupCompletionStatus.Success, done.Status);
            var roles = factory.Services.GetRequiredService<Whiskers.Services.Auth.IRoleService>();
            Assert.Equal(Whiskers.Models.AppRole.Admin, roles.GetRole("first@example.com"));
            Assert.Empty(monitor.Snapshot());
            var audit = factory.Services.GetRequiredService<Whiskers.Services.AuditLog.IAuditLogService>();
            Assert.Contains(await audit.GetRecentAsync(50), r => r.Action == Whiskers.Services.Setup.SetupStateService.FirstAdminAction);

            var whitelist = factory.Services.GetRequiredService<Whiskers.Services.Auth.IWhitelistService>();
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => whitelist.SaveWhitelistAsync(new()));
            await Assert.ThrowsAsync<MutationScopeViolationException>(() => roles.SetRoleAsync("x@example.com", Whiskers.Models.AppRole.Admin));
            await Assert.ThrowsAsync<InvalidOperationException>(() => whitelist.AddFirstAdminAsync("x@example.com"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previousDataDir);
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
