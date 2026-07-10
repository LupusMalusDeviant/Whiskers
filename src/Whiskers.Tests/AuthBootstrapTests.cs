using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;

namespace Whiskers.Tests;

/// <summary>C5 — a fresh instance must never be admin-less or fail-open. Covers the RoleService admin
/// bootstrap (seeds an Admin role from config on FIRST run only) and the WhitelistService fail-open →
/// fail-closed switch (a disabled whitelist admits everyone only while no role exists).</summary>
public class AuthBootstrapTests
{
    private static string TempStore()
        => Path.Combine(Path.GetTempPath(), "sw-tests", Guid.NewGuid().ToString("N") + ".json");

    private static IConfiguration Cfg(params (string Key, string? Value)[] kv)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kv.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    // ---------------------------------------------------------------- RoleService admin bootstrap

    [Fact]
    public async Task Bootstrap_seeds_admin_from_WHISKERS_ADMIN_EMAIL_on_first_run()
    {
        var path = TempStore();
        var svc = new RoleService(Cfg(("WHISKERS_ADMIN_EMAIL", "admin@x")), NullLogger<RoleService>.Instance, path);
        await svc.InitializeAsync();

        Assert.True(svc.HasAnyRoles());
        Assert.Equal(AppRole.Admin, svc.GetRole("admin@x"));
        Assert.True(svc.HasExplicitRole("admin@x"));
        Assert.True(File.Exists(path)); // persisted so the admin survives a restart
    }

    [Fact]
    public async Task Bootstrap_seeds_admin_from_first_GoogleAuth_AllowedEmail_only()
    {
        var svc = new RoleService(
            Cfg(("GoogleAuth:AllowedEmails:0", "g@x"), ("GoogleAuth:AllowedEmails:1", "other@x")),
            NullLogger<RoleService>.Instance, TempStore());
        await svc.InitializeAsync();

        Assert.Equal(AppRole.Admin, svc.GetRole("g@x"));      // the documented GOOGLE_ADMIN_EMAIL = index 0
        Assert.Equal(AppRole.Viewer, svc.GetRole("other@x")); // further whitelisted emails are NOT auto-admin
    }

    [Fact]
    public async Task Bootstrap_dedupes_case_insensitively_when_both_admin_vars_match()
    {
        var svc = new RoleService(
            Cfg(("WHISKERS_ADMIN_EMAIL", "a@x"), ("GoogleAuth:AllowedEmails:0", "A@X")),
            NullLogger<RoleService>.Instance, TempStore());
        await svc.InitializeAsync();

        Assert.Single(svc.GetRoleData().Roles); // one Admin entry despite the case-different duplicate
    }

    [Fact]
    public async Task Bootstrap_never_overwrites_an_existing_roles_file()
    {
        var path = TempStore();
        // First instance persists a roles.json with one Operator (and no admin-bootstrap config).
        var seed = new RoleService(Cfg(), NullLogger<RoleService>.Instance, path);
        await seed.SetRoleAsync("op@x", AppRole.Operator);

        // Second instance sees the existing file — bootstrap must NOT run and must NOT add the admin.
        var svc = new RoleService(Cfg(("WHISKERS_ADMIN_EMAIL", "admin@x")), NullLogger<RoleService>.Instance, path);
        await svc.InitializeAsync();

        Assert.Equal(AppRole.Operator, svc.GetRole("op@x"));   // existing entry preserved
        Assert.Equal(AppRole.Viewer, svc.GetRole("admin@x"));  // NOT bootstrapped over the existing file
    }

    [Fact]
    public async Task No_admin_config_seeds_nothing_and_writes_no_file()
    {
        var path = TempStore();
        var svc = new RoleService(Cfg(), NullLogger<RoleService>.Instance, path);
        await svc.InitializeAsync();

        Assert.False(svc.HasAnyRoles());
        Assert.False(File.Exists(path)); // legacy fail-open stays: no roles, nothing persisted
    }

    // ---------------------------------------------------------------- WhitelistService fail-open → fail-closed

    private static async Task<WhitelistService> Whitelist(IRoleService roles, params (string, string?)[] cfg)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var svc = new WhitelistService(Cfg(cfg), roles, NullLogger<WhitelistService>.Instance, new DataPathOptions(dir));
        await svc.InitializeAsync();
        return svc;
    }

    [Fact]
    public async Task Disabled_whitelist_with_no_roles_is_fail_open()
    {
        var wl = await Whitelist(new FakeRoles());   // whitelist off + nothing configured
        Assert.True(wl.IsEmailAllowed("anyone@x"));  // legacy fail-open preserved for unconfigured instances
    }

    [Fact]
    public async Task Disabled_whitelist_with_roles_is_fail_closed()
    {
        var roles = new FakeRoles { AnyRoles = true };
        roles.Explicit.Add("admin@x");
        var wl = await Whitelist(roles);

        Assert.True(wl.IsEmailAllowed("admin@x"));     // role-holder admitted
        Assert.False(wl.IsEmailAllowed("stranger@x")); // everyone else denied — the C5 fix
    }

    [Fact]
    public async Task Enabled_whitelist_enforces_the_list_regardless_of_roles()
    {
        // Seeded from config → Enabled=true. The enabled branch must NOT fall through to the role check.
        var wl = await Whitelist(new FakeRoles { AnyRoles = true }, ("GoogleAuth:AllowedEmails:0", "a@x"));
        Assert.True(wl.IsEmailAllowed("a@x"));
        Assert.False(wl.IsEmailAllowed("b@x"));
    }

    private sealed class FakeRoles : IRoleService
    {
        public bool AnyRoles;
        public readonly HashSet<string> Explicit = new(StringComparer.OrdinalIgnoreCase);
        public bool HasAnyRoles() => AnyRoles;
        public bool HasExplicitRole(string? email) => email != null && Explicit.Contains(email);
        // Unused by IsEmailAllowed:
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public AppRole GetRole(string? email) => AppRole.Viewer;
        public bool HasRole(string? email, AppRole requiredRole) => false;
        public UserRoleData GetRoleData() => new();
        public Task SaveRoleDataAsync(UserRoleData data) => Task.CompletedTask;
        public Task SetRoleAsync(string email, AppRole role) => Task.CompletedTask;
        public Task RemoveRoleAsync(string email) => Task.CompletedTask;
    }
}
