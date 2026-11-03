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

    private readonly McpCallOutcomeTests.RecordingAuditLog _audit = new();

    private SetupStateService Service(RoleService roles, WhitelistService whitelist, IServiceScopeFactory scopes,
        IConfiguration? config = null, Whiskers.Services.AuditLog.IAuditLogService? audit = null)
        => new(roles, whitelist, scopes, NullLogger<SetupStateService>.Instance, Paths(), config, audit ?? _audit);

    // ---------------- first admin outside the gateway (R1-48, option a) ----------------

    [Fact]
    public async Task The_first_admin_writes_refuse_once_an_admin_exists()
    {
        var (roles, whitelist) = await AuthAsync();
        await whitelist.AddFirstAdminAsync("boss@x");
        await roles.SetFirstAdminAsync("boss@x");
        Assert.Equal(AppRole.Admin, roles.GetRole("boss@x"));

        // From now on both are closed: nothing is written, whoever calls.
        await Assert.ThrowsAsync<InvalidOperationException>(() => roles.SetFirstAdminAsync("intruder@x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => whitelist.AddFirstAdminAsync("intruder@x"));
        Assert.Equal(AppRole.Viewer, roles.GetRole("intruder@x"));
        Assert.DoesNotContain("intruder@x", whitelist.GetWhitelist().Emails);
    }

    [Fact]
    public async Task The_first_admin_writes_refuse_an_admin_that_came_from_elsewhere()
    {
        // C5 bootstrap or a role change made the admin: the wizard's writes are closed just the same.
        var (roles, whitelist) = await AuthAsync();
        await roles.SetRoleAsync("seed@x", AppRole.Admin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => roles.SetFirstAdminAsync("other@x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => whitelist.AddFirstAdminAsync("other@x"));
        Assert.Equal(AppRole.Viewer, roles.GetRole("other@x"));
        Assert.Empty(whitelist.GetWhitelist().Emails);
    }

    [Fact]
    public async Task Concurrent_first_admin_writes_create_at_most_one_admin()
    {
        var (roles, _) = await AuthAsync();
        var attempts = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            try { await roles.SetFirstAdminAsync($"user{i}@x"); return true; }
            catch (InvalidOperationException) { return false; }
        })).ToArray();
        var won = await Task.WhenAll(attempts);

        Assert.Equal(1, won.Count(w => w));
        Assert.Single(roles.GetRoleData().Roles, r => r.Role == AppRole.Admin);
    }

    [Fact]
    public async Task Two_wizard_submits_at_once_create_at_most_one_admin()
    {
        var (roles, whitelist) = await AuthAsync();
        using var idp = IdentityProvider();
        var svc = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await svc.InitializeAsync();
        var token = Token();

        var results = await Task.WhenAll(
            Task.Run(() => svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = false, Email = "a@x", SetupToken = token })),
            Task.Run(() => svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = false, Email = "b@x", SetupToken = token })));

        Assert.Single(results, r => r.Status == SetupCompletionStatus.Success);
        Assert.Single(roles.GetRoleData().Roles, r => r.Role == AppRole.Admin);
        // The loser wrote nothing: neither a role nor a whitelist entry.
        var admin = roles.GetRoleData().Roles.Single().Email;
        Assert.Equal([admin], whitelist.GetWhitelist().Emails);
    }

    [Fact]
    public async Task The_first_admin_is_audited_and_without_its_record_nothing_is_written()
    {
        var (roles, whitelist) = await AuthAsync();
        using var idp = IdentityProvider();
        var down = new DownAudit();
        var refused = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>(), audit: down);
        await refused.InitializeAsync();

        var res = await refused.CompleteSetupAsync(new SetupAdminRequest { IsLocal = false, Email = "boss@x", SetupToken = Token() });
        Assert.Equal(SetupCompletionStatus.Failed, res.Status);
        Assert.False(roles.HasAnyRoles());
        Assert.Empty(whitelist.GetWhitelist().Emails);

        var svc = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await svc.InitializeAsync();
        var ok = await svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = false, Email = "boss@x", SetupToken = Token() });
        Assert.Equal(SetupCompletionStatus.Success, ok.Status);
        Assert.Contains((SetupStateService.FirstAdminAction, "kind=federated; authority=setup-token"), _audit.Entries);
    }

    private sealed class DownAudit : Whiskers.Services.AuditLog.IAuditLogService
    {
        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) => Task.CompletedTask;

        public Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) => Task.FromResult(false);

        public Task<List<Whiskers.Models.AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0,
            string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(new List<Whiskers.Models.AuditLogEntity>());

        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    // The generated one-time setup token, as the operator would read it from the data directory.
    private string Token() => File.ReadAllText(Paths().SetupTokenFile);

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

        var res = await svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = false, Email = "boss@x", SetupToken = Token() });
        Assert.Equal(SetupCompletionStatus.Success, res.Status);
        Assert.True(svc.IsSetupComplete);
        Assert.Equal(AppRole.Admin, roles.GetRole("boss@x"));
        Assert.Contains("boss@x", whitelist.GetWhitelist().Emails);

        var again = await svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = false, Email = "other@x", SetupToken = "irrelevant" });
        Assert.Equal(SetupCompletionStatus.AlreadyComplete, again.Status);   // race guard
    }

    [Fact]
    public async Task Complete_local_creates_identity_user_and_admin_role()
    {
        var (roles, whitelist) = await AuthAsync();
        using var idp = IdentityProvider();
        var svc = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await svc.InitializeAsync();

        var res = await svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = true, Email = "root@x", Password = "Sup3rSecret!!", SetupToken = Token() });
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

        var res = await svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = true, Email = "root@x", Password = "short", SetupToken = Token() });
        Assert.Equal(SetupCompletionStatus.Failed, res.Status);
        Assert.NotEmpty(res.Errors);
        Assert.False(svc.IsSetupComplete);
        Assert.Equal(AppRole.Viewer, roles.GetRole("root@x"));   // no admin role written
    }

    // ---------------- setup token (audit TA-02) ----------------

    [Fact]
    public async Task First_run_writes_an_owner_only_token_and_completion_requires_it()
    {
        var (roles, whitelist) = await AuthAsync();
        using var idp = IdentityProvider();
        var svc = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await svc.InitializeAsync();

        Assert.True(File.Exists(Paths().SetupTokenFile));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Paths().SetupTokenFile));
        Assert.True(svc.VerifySetupToken(Token()));

        // Reaching the wizard is not enough: no token, or a wrong one, claims nothing.
        foreach (var bad in new[] { null, "", "wrong-token-wrong-token" })
        {
            var res = await svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = false, Email = "intruder@x", SetupToken = bad });
            Assert.Equal(SetupCompletionStatus.Failed, res.Status);
        }
        Assert.False(svc.IsSetupComplete);
        Assert.Equal(AppRole.Viewer, roles.GetRole("intruder@x"));

        var ok = await svc.CompleteSetupAsync(new SetupAdminRequest { IsLocal = false, Email = "boss@x", SetupToken = Token() });
        Assert.Equal(SetupCompletionStatus.Success, ok.Status);
        Assert.False(File.Exists(Paths().SetupTokenFile));        // used up
        Assert.False(svc.VerifySetupToken("anything"));
    }

    [Fact]
    public async Task The_token_survives_a_restart_and_can_be_preset()
    {
        var (roles, whitelist) = await AuthAsync();
        using var idp = IdentityProvider();
        var first = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await first.InitializeAsync();
        var token = Token();
        var second = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>());
        await second.InitializeAsync();
        Assert.True(second.VerifySetupToken(token));               // same file, same token

        var preset = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WHISKERS_SETUP_TOKEN"] = "preset-token-from-helm" }).Build();
        var third = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>(), preset);
        await third.InitializeAsync();
        Assert.True(third.VerifySetupToken("preset-token-from-helm"));
        Assert.False(third.VerifySetupToken(token));
    }

    [Fact]
    public async Task Auth_disabled_creates_no_token()
    {
        var (roles, whitelist) = await AuthAsync();
        using var idp = IdentityProvider();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Auth:Disabled"] = "true" }).Build();
        var svc = Service(roles, whitelist, idp.GetRequiredService<IServiceScopeFactory>(), cfg);
        await svc.InitializeAsync();
        Assert.False(File.Exists(Paths().SetupTokenFile));
    }

    public void Dispose()
    {
        TestSqlite.ClearPools(_dir);
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }
}
