using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Auth;

namespace Whiskers.Tests;

// Audit TA-02: without an explicit Admin the first-run wizard counts as not completed, so dropping the last Admin
// re-opened setup on the next start. Once an Admin exists, no write path may leave the instance without one.
public sealed class RoleServiceLastAdminTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-roles-{Guid.NewGuid():N}");

    private async Task<RoleService> WithAdminAsync()
    {
        Directory.CreateDirectory(_dir);
        var roles = new RoleService(new ConfigurationBuilder().Build(), NullLogger<RoleService>.Instance,
            dataPaths: new DataPathOptions(_dir));
        await roles.InitializeAsync();
        await roles.SetRoleAsync("admin@x", AppRole.Admin);
        return roles;
    }

    [Fact]
    public async Task Removing_the_last_admin_is_refused()
    {
        var roles = await WithAdminAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => roles.RemoveRoleAsync("admin@x"));
        Assert.Equal(AppRole.Admin, roles.GetRole("admin@x"));
    }

    [Fact]
    public async Task Demoting_the_last_admin_is_refused()
    {
        var roles = await WithAdminAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => roles.SetRoleAsync("admin@x", AppRole.Operator));
        Assert.Equal(AppRole.Admin, roles.GetRole("admin@x"));
    }

    [Fact]
    public async Task Saving_a_role_table_without_an_explicit_admin_is_refused_even_with_default_admin()
    {
        var roles = await WithAdminAsync();
        // The trick from the audit: make the DEFAULT role Admin and delete the explicit rows.
        var data = new UserRoleData { DefaultRole = AppRole.Admin, Roles = new() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => roles.SaveRoleDataAsync(data));
        Assert.Equal(AppRole.Admin, roles.GetRole("admin@x"));
        Assert.Contains(roles.GetRoleData().Roles, r => r.Role == AppRole.Admin);
    }

    [Fact]
    public async Task Handing_over_admin_works_when_another_admin_remains()
    {
        var roles = await WithAdminAsync();
        await roles.SetRoleAsync("second@x", AppRole.Admin);
        await roles.RemoveRoleAsync("admin@x");
        Assert.Equal(AppRole.Admin, roles.GetRole("second@x"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }
}
