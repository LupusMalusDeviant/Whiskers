using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-47 (TA-50, F-13: "Identity on Postgres is not tested"): the local sign-in accounts on a REAL PostgreSQL 18. The whole
/// application is booted with <c>WHISKERS_DB_PROVIDER=postgres</c>: its start migrates both contexts into the server (each
/// with its own history table), seeds the administrator there, and the sign-in, the lockout, the security stamp and the
/// deletion of an account all run through the real pipeline against the rows in PostgreSQL. Mandatory on CI
/// (<see cref="DockerRequirement"/>).
/// </summary>
[Trait("Category", DockerRequirement.Category)]
[Collection("WebAppBoot")] // the boot needs the process-wide WHISKERS_DATA_DIR
public sealed class PostgresIdentityTests : IAsyncLifetime
{
    private const string Admin = "admin@pg.test";
    private const string Password = "CorrectHorse12!!";
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private Exception? _startFailure;

    public async Task InitializeAsync() => _startFailure = await DockerRequirement.TryStartAsync(_pg);

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_startFailure is null) await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task Local_accounts_live_in_postgres_sign_in_lock_out_and_end_their_sessions_there()
    {
        DockerRequirement.SkipUnlessStarted(_startFailure);
        await BootAsync(async (factory, newClient) =>
        {
            // The start migrated both schemas into PostgreSQL, each under its own history table, and seeded the Admin.
            Assert.NotEmpty(await ScalarListAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\""));
            var identityMigrations = await ScalarListAsync("SELECT \"MigrationId\" FROM \"__IdentityMigrationsHistory\"");
            Assert.NotEmpty(identityMigrations);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }
            Assert.Equal([Admin], await ScalarListAsync("SELECT \"Email\" FROM \"AspNetUsers\""));

            // Sign-in against the PostgreSQL row; the Admin-only download answers "no such archive" (not a refusal).
            var admin = newClient();
            await SignInAsync(admin, Admin, Password);
            Assert.Equal(HttpStatusCode.NotFound, (await AdminProbeAsync(admin)).StatusCode);

            // A second account, created through the app's UserManager, lands in PostgreSQL.
            const string ops = "ops@pg.test";
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                Assert.True((await users.CreateAsync(new AppUser { UserName = ops, Email = ops }, Password)).Succeeded);
            }
            await GatewayArrange.RunAsync("config.roles",
                () => factory.Services.GetRequiredService<IRoleService>().SetRoleAsync(ops, AppRole.Admin));
            Assert.Contains(ops, await ScalarListAsync("SELECT \"Email\" FROM \"AspNetUsers\""));

            // Lockout is counted in PostgreSQL: five wrong passwords lock the account; then the right one is refused too.
            var attacker = newClient();
            for (var i = 0; i < 5; i++)
                Assert.Contains("error=invalid", await PostLoginAsync(attacker, ops, "wrong-password-" + i));
            Assert.NotEmpty(await ScalarListAsync(
                $"SELECT \"LockoutEnd\"::text FROM \"AspNetUsers\" WHERE \"Email\" = '{ops}' AND \"LockoutEnd\" > now()"));
            Assert.Contains("error=invalid", await PostLoginAsync(attacker, ops, Password));

            // Unlocked, the account signs in; a new security stamp (a password change, R1-49) ends that session, and so
            // does deleting the account — both read from PostgreSQL on the session's next request.
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                var row = (await users.FindByEmailAsync(ops))!;
                Assert.True((await users.SetLockoutEndDateAsync(row, null)).Succeeded);
                Assert.True((await users.ResetAccessFailedCountAsync(row)).Succeeded);
            }
            var session = newClient();
            await SignInAsync(session, ops, Password);
            Assert.Equal(HttpStatusCode.NotFound, (await AdminProbeAsync(session)).StatusCode);
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                Assert.True((await users.UpdateSecurityStampAsync((await users.FindByEmailAsync(ops))!)).Succeeded);
            }
            AssertSignedOut(await AdminProbeAsync(session));

            var second = newClient();
            await SignInAsync(second, ops, Password);
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                Assert.True((await users.DeleteAsync((await users.FindByEmailAsync(ops))!)).Succeeded);
            }
            Assert.DoesNotContain(ops, await ScalarListAsync("SELECT \"Email\" FROM \"AspNetUsers\""));
            AssertSignedOut(await AdminProbeAsync(second));

            // The Admin's session was never touched.
            Assert.Equal(HttpStatusCode.NotFound, (await AdminProbeAsync(admin)).StatusCode);
        });
    }

    private async Task<List<string>> ScalarListAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_pg.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<string> PostLoginAsync(HttpClient client, string email, string password)
    {
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        var response = await client.PostAsync("/login-local", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = password, ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    private static async Task SignInAsync(HttpClient client, string email, string password) =>
        Assert.DoesNotContain("error", await PostLoginAsync(client, email, password));

    private static Task<HttpResponseMessage> AdminProbeAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/backups/self/none/download");
        request.Headers.Accept.ParseAdd("text/html");
        return client.SendAsync(request);
    }

    private static void AssertSignedOut(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.OriginalString);
    }

    private async Task BootAsync(Func<WebApplicationFactory<Program>, Func<HttpClient>, Task> body)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-pg-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var pwFile = Path.Combine(dataDir, "admin-pw.txt");
        await File.WriteAllTextAsync(pwFile, Password);
        var config = new List<KeyValuePair<string, string?>>
        {
            new("WHISKERS_DATA_DIR", dataDir),
            new("WHISKERS_DB_PROVIDER", "postgres"),
            new("WHISKERS_DB_CONNECTION", _pg.GetConnectionString()),
            new("Auth:Disabled", "false"),
            new("WHISKERS_ADMIN_EMAIL", Admin),
            new("WHISKERS_ADMIN_PASSWORD_FILE", pwFile),
            new("WHISKERS_DISABLE_LOCAL_DOCKER", "true"),
        };
        config.AddRange(BootSettings.OnlyCore());
        var clients = new List<HttpClient>();
        try
        {
            using var settings = BootSettings.Apply(config);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");   // ValidateOnBuild + ValidateScopes over the PostgreSQL graph too
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
            });
            try
            {
                BootSettings.AssertBoot(factory.Services, config);   // starts the host: the migrations have run
                await body(factory, () =>
                {
                    var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                    clients.Add(client);
                    return client;
                });
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
