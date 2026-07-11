using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Modules;
using Whiskers.Services.Auth;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>F1 local username/password auth. Unit tests over real temp-file SQLite (Identity schema migrates,
/// coexists with MetricsDbContext on one DB via a separate history table, the admin seeder), a DI-shape guard
/// (UserManager but no RoleManager), and an end-to-end WebApplicationFactory boot proving a seeded local admin
/// can sign in through /login-local (incl. the antiforgery token under global interactive render).</summary>
[Collection("WebAppBoot")] // serialized: the end-to-end boot below needs the process-wide WHISKERS_DATA_DIR env var
public sealed class LocalAuthIdentityTests : IDisposable
{
    private readonly List<string> _files = new();
    private string NewDbPath()
    {
        var p = Path.Combine(Path.GetTempPath(), $"sw-id-{Guid.NewGuid():N}.db");
        _files.Add(p);
        return p;
    }
    private string NewPwFile(string password)
    {
        var p = Path.Combine(Path.GetTempPath(), $"sw-pw-{Guid.NewGuid():N}.txt");
        File.WriteAllText(p, password);
        _files.Add(p);
        return p;
    }

    private static IConfiguration Cfg(params (string Key, string? Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    // Same seams production uses: the Identity context pins the Sqlite migration assembly + its OWN history table.
    private static WhiskersIdentityDbContext IdCtx(string dbPath) =>
        new(new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
            .UseSqlite($"Data Source={dbPath}", sql =>
            {
                sql.MigrationsAssembly("Whiskers.Migrations.Sqlite");
                sql.MigrationsHistoryTable("__IdentityMigrationsHistory");
            }).Options);

    private static MetricsDbContext MetricsCtx(string dbPath) =>
        new(new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={dbPath}", sql => sql.MigrationsAssembly("Whiskers.Migrations.Sqlite")).Options);

    private static ServiceProvider IdentityServices(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseSqlite($"Data Source={dbPath}", sql =>
        {
            sql.MigrationsAssembly("Whiskers.Migrations.Sqlite");
            sql.MigrationsHistoryTable("__IdentityMigrationsHistory");
        }));
        services.AddIdentityCore<AppUser>(o => { o.Password.RequiredLength = 12; o.User.RequireUniqueEmail = true; })
            .AddEntityFrameworkStores<WhiskersIdentityDbContext>();
        return services.BuildServiceProvider();
    }

    // ---------------------------------------------------------------- schema + coexistence

    [Fact]
    public async Task Identity_schema_migrates_on_sqlite()
    {
        var path = NewDbPath();
        await using (var db = IdCtx(path)) await db.Database.MigrateAsync();

        await using var check = IdCtx(path);
        Assert.Contains(await check.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialIdentity"));
        Assert.Equal(0, await check.Users.CountAsync()); // AspNetUsers queryable
    }

    [Fact]
    public async Task Metrics_and_identity_coexist_on_one_db_with_independent_histories()
    {
        var path = NewDbPath();
        // Metrics first (baseline + migrate into the default __EFMigrationsHistory), then Identity on the SAME file.
        await using (var m = MetricsCtx(path))
            await DatabaseInitializer.InitializeAsync(m, NullLogger.Instance);
        await using (var id = IdCtx(path))
            await id.Database.MigrateAsync();

        await using var mCheck = MetricsCtx(path);
        Assert.Contains(await mCheck.Database.GetAppliedMigrationsAsync(), x => x.EndsWith("InitialCreate"));
        Assert.Equal(0, await mCheck.ContainerMetrics.CountAsync()); // metrics tables intact
        await using var idCheck = IdCtx(path);
        Assert.Contains(await idCheck.Database.GetAppliedMigrationsAsync(), x => x.EndsWith("InitialIdentity"));
        Assert.Equal(0, await idCheck.Users.CountAsync());           // identity tables present, separate history
    }

    // ---------------------------------------------------------------- LocalAdminSeeder

    [Fact]
    public async Task Seeder_creates_admin_from_config_then_is_idempotent()
    {
        var path = NewDbPath();
        var pwFile = NewPwFile("Str0ngPassw0rd!!");
        var cfg = Cfg(("WHISKERS_ADMIN_EMAIL", "admin@x"), ("WHISKERS_ADMIN_PASSWORD_FILE", pwFile));

        await using var sp = IdentityServices(path);
        using (var scope = sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.MigrateAsync();

        using (var scope = sp.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await LocalAdminSeeder.SeedAsync(users, cfg, NullLogger.Instance);
            var u = await users.FindByEmailAsync("admin@x");
            Assert.NotNull(u);
            Assert.True(await users.CheckPasswordAsync(u!, "Str0ngPassw0rd!!"));
        }
        using (var scope = sp.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await LocalAdminSeeder.SeedAsync(users, cfg, NullLogger.Instance); // second run
            Assert.Single(await users.Users.ToListAsync());                    // no duplicate
        }
    }

    [Fact]
    public async Task Seeder_no_ops_without_config()
    {
        var path = NewDbPath();
        await using var sp = IdentityServices(path);
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.MigrateAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        await LocalAdminSeeder.SeedAsync(users, Cfg(), NullLogger.Instance); // no admin email / password file
        Assert.Empty(await users.Users.ToListAsync());
    }

    [Fact]
    public async Task Seeder_does_not_throw_on_weak_password()
    {
        var path = NewDbPath();
        var pwFile = NewPwFile("short"); // < 12 chars → fails the policy
        await using var sp = IdentityServices(path);
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.MigrateAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        // Must not brick the boot — logs + returns, no user created.
        await LocalAdminSeeder.SeedAsync(users, Cfg(("WHISKERS_ADMIN_EMAIL", "a@x"), ("WHISKERS_ADMIN_PASSWORD_FILE", pwFile)), NullLogger.Instance);
        Assert.Empty(await users.Users.ToListAsync());
    }

    // ---------------------------------------------------------------- DI shape (no parallel role system)

    [Fact]
    public void AddIdentityCore_registers_UserManager_but_no_RoleManager()
    {
        using var sp = IdentityServices(NewDbPath());
        using var scope = sp.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<UserManager<AppUser>>());
        Assert.Null(scope.ServiceProvider.GetService<RoleManager<IdentityRole>>()); // no AddRoles → roles stay in roles.json
    }

    // ---------------------------------------------------------------- end-to-end boot login

    [Fact]
    public async Task Seeded_local_admin_can_sign_in_end_to_end()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-f1-boot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var pwFile = Path.Combine(dataDir, "admin-pw.txt");
        await File.WriteAllTextAsync(pwFile, "CorrectHorse12!!");

        // WHISKERS_DATA_DIR is read EAGERLY in Program.cs (DataPathOptions.FromConfiguration), before the
        // factory's in-memory config is layered in — it must be a real env var (see SetupWizardBootTests).
        var prev = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        var config = new List<KeyValuePair<string, string?>>
        {
            new("Auth:Disabled", "false"),
            new("WHISKERS_ADMIN_EMAIL", "admin@local.test"),
            new("WHISKERS_ADMIN_PASSWORD_FILE", pwFile),
        };
        // Lean boot: every feature module off (Core carries the auth + login surface).
        foreach (var m in ModuleCatalog.DiscoverEnabled(new ConfigurationBuilder().Build()).Where(m => m.Id != "all-in-one"))
            config.Add(new($"Features:{m.Id}:Enabled", "false"));

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // 1. GET /login → the antiforgery token is rendered (proves it works under interactive render) + the
            //    antiforgery cookie is set (carried by the client's CookieContainer).
            var page = await client.GetAsync("/login");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(token), "antiforgery token should be present in the login form");

            // 2. Wrong password → 302 /login?error=invalid, no auth cookie (token still valid — failure doesn't sign in).
            var bad = await client.PostAsync("/login-local", Form(("email", "admin@local.test"), ("password", "nope-nope-nope"), ("__RequestVerificationToken", token)));
            Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
            Assert.Contains("error=invalid", bad.Headers.Location!.OriginalString);

            // 3. Correct password → 302 / + an auth cookie.
            var ok = await client.PostAsync("/login-local", Form(("email", "admin@local.test"), ("password", "CorrectHorse12!!"), ("__RequestVerificationToken", token)));
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
            Assert.Equal("/", ok.Headers.Location!.OriginalString);
            Assert.Contains(ok.Headers, h => h.Key == "Set-Cookie" && h.Value.Any(v => v.Contains(".AspNetCore.Cookies")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static FormUrlEncodedContent Form(params (string, string)[] kv)
        => new(kv.Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in _files)
            foreach (var f in new[] { p, p + "-wal", p + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* temp */ }
    }
}
