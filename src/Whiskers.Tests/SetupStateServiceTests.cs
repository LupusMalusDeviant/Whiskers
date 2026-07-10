using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Persistence;
using Whiskers.Services.Setup;

namespace Whiskers.Tests;

/// <summary>W1 setup state: the admin-role predicate + flag reconcile, and atomic completion (federated + local
/// admin creation, idempotence, weak-password failure).</summary>
public sealed class SetupStateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-setup-{Guid.NewGuid():N}");
    public SetupStateServiceTests() => Directory.CreateDirectory(_dir);

    private static IConfiguration Empty() => new ConfigurationBuilder().Build();
    private DataPathOptions Paths() => new(_dir);

    // Real RoleService + WhitelistService on temp stores (no admin-bootstrap config).
    private async Task<(RoleService roles, WhitelistService whitelist)> AuthAsync()
    {
        var roles = new RoleService(Empty(), NullLogger<RoleService>.Instance, dataPaths: Paths());
        var whitelist = new WhitelistService(Empty(), roles, NullLogger<WhitelistService>.Instance, Paths());
        await roles.InitializeAsync();
        await whitelist.InitializeAsync();
        return (roles, whitelist);
    }

    // A ServiceProvider whose IServiceScopeFactory yields a UserManager<AppUser> over a migrated temp Identity DB.
    private ServiceProvider IdentityProvider()
    {
        var db = Path.Combine(_dir, "id.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseSqlite($"Data Source={db}", sql =>
        {
            sql.MigrationsAssembly("Whiskers.Migrations.Sqlite");
            sql.MigrationsHistoryTable("__IdentityMigrationsHistory");
        }));
        services.AddIdentityCore<AppUser>(o => { o.Password.RequiredLength = 12; o.User.RequireUniqueEmail = true; })
            .AddEntityFrameworkStores<WhiskersIdentityDbContext>();
        var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
            scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database.Migrate();
        return sp;
    }

    private SetupStateService Service(RoleService roles, WhitelistService whitelist, IServiceScopeFactory scopes)
        => new(roles, whitelist, scopes, NullLogger<SetupStateService>.Instance, Paths());

    // ---------------- predicate + reconcile ----------------

    [Fact]
    public async Task Fresh_instance_is_not_complete()
    {
        var (roles, whitelist) = await AuthAsync();
        using var idp = IdentityProvider();
        var svc = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await svc.InitializeAsync();
        Assert.False(svc.IsSetupComplete);
        Assert.False(File.Exists(Paths().SetupCompleteFlag));
    }

    [Fact]
    public async Task Existing_admin_is_complete_and_writes_flag()
    {
        var (roles, whitelist) = await AuthAsync();
        await roles.SetRoleAsync("admin@x", AppRole.Admin);      // pre-existing admin (e.g. C5 seed)
        using var idp = IdentityProvider();
        var svc = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await svc.InitializeAsync();
        Assert.True(svc.IsSetupComplete);
        Assert.True(File.Exists(Paths().SetupCompleteFlag));     // reconciled forward
    }

    [Fact]
    public async Task Stale_flag_without_admin_is_distrusted()
    {
        var (roles, whitelist) = await AuthAsync();
        await File.WriteAllTextAsync(Paths().SetupCompleteFlag, "stale");  // flag but no admin role
        using var idp = IdentityProvider();
        var svc = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await svc.InitializeAsync();
        Assert.False(svc.IsSetupComplete);
        Assert.False(File.Exists(Paths().SetupCompleteFlag));     // deleted — role is authoritative
    }

    // ---------------- completion ----------------

    [Fact]
    public async Task Complete_federated_seeds_admin_role_and_whitelist()
    {
        var (roles, whitelist) = await AuthAsync();
        using var idp = IdentityProvider();
        var svc = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await svc.InitializeAsync();

        var res = await svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = false, Email = "boss@x" });
        Assert.Equal(SetupCompletionStatus.Success, res.Status);
        Assert.True(svc.IsSetupComplete);
        Assert.Equal(AppRole.Admin, roles.GetRole("boss@x"));
        Assert.Contains("boss@x", whitelist.GetWhitelist().Emails);

        var again = await svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = false, Email = "other@x" });
        Assert.Equal(SetupCompletionStatus.AlreadyComplete, again.Status);   // race guard
    }

    [Fact]
    public async Task Complete_local_creates_identity_user_and_admin_role()
    {
        var (roles, whitelist) = await AuthAsync();
        using var idp = IdentityProvider();
        var svc = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await svc.InitializeAsync();

        var res = await svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = true, Email = "root@x", Password = "Sup3rSecret!!" });
        Assert.Equal(SetupCompletionStatus.Success, res.Status);
        Assert.Equal(AppRole.Admin, roles.GetRole("root@x"));
        using var scope = idp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
        Assert.NotNull(await users.FindByEmailAsync("root@x"));
    }

    [Fact]
    public async Task Complete_local_with_weak_password_fails_without_marking_complete()
    {
        var (roles, whitelist) = await AuthAsync();
        using var idp = IdentityProvider();
        var svc = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await svc.InitializeAsync();

        var res = await svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = true, Email = "root@x", Password = "short" });
        Assert.Equal(SetupCompletionStatus.Failed, res.Status);
        Assert.NotEmpty(res.Errors);
        Assert.False(svc.IsSetupComplete);
        Assert.Equal(AppRole.Viewer, roles.GetRole("root@x"));   // no admin role written
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }
}
