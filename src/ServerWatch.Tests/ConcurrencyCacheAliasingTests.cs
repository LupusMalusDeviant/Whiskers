using Microsoft.Extensions.Logging.Abstractions;
using ServerWatch.Models;
using ServerWatch.Services.Auth;

namespace ServerWatch.Tests;

// Regression + concurrency tests for the cache-aliasing / read-lock fixes (Bean ServerWatch-sdn3):
// MIT-1 RoleService, MIT-2 VaultService, NIED-14 ServerConfigService. Each service is pointed at a
// unique temp store path (the DI-safe optional ctor seam) so the tests are isolated.
public class ConcurrencyCacheAliasingTests
{
    private static string TempStore()
        => Path.Combine(Path.GetTempPath(), "sw-tests", Guid.NewGuid().ToString("N") + ".json");

    // ---------------------------------------------------------------- MIT-1: RoleService

    [Fact]
    public async Task RoleService_SetRole_then_mutate_input_does_not_change_persisted_state()
    {
        var svc = new RoleService(NullLogger<RoleService>.Instance, TempStore());
        var data = new UserRoleData { Roles = { new UserRoleEntry { Email = "a@x", Role = AppRole.Admin } } };
        await svc.SaveRoleDataAsync(data);

        // Mutate the caller's object after saving — must not affect the cached/enforced state.
        data.Roles.Clear();
        data.DefaultRole = AppRole.Admin;

        Assert.Equal(AppRole.Admin, svc.GetRole("a@x"));       // entry still enforced
        Assert.Equal(AppRole.Viewer, svc.GetRole("nobody@x")); // default not aliased to Admin
    }

    [Fact]
    public async Task RoleService_concurrent_GetRole_and_SetRole_never_throws()
    {
        var svc = new RoleService(NullLogger<RoleService>.Instance, TempStore());
        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            var email = $"user{i}@x";
            tasks.Add(Task.Run(() => svc.SetRoleAsync(email, AppRole.Operator)));
            tasks.Add(Task.Run(() => { for (var k = 0; k < 20; k++) _ = svc.GetRole(email); }));
        }

        await Task.WhenAll(tasks); // pre-fix: serializing the live list mid-write threw InvalidOperationException
        Assert.Equal(AppRole.Operator, svc.GetRole("user1@x"));
    }

    [Fact]
    public async Task RoleService_SaveRoleData_IO_failure_surfaces_error_and_keeps_cache_consistent()
    {
        // Point the store at an existing *directory*: the JsonFileStore ctor succeeds, but SaveAsync's
        // File.Move onto a directory throws — so the persist fails loudly and the cache is never updated
        // (SetData runs only after a successful save).
        var dir = Path.Combine(Path.GetTempPath(), "sw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var svc = new RoleService(NullLogger<RoleService>.Instance, dir);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            svc.SaveRoleDataAsync(new UserRoleData { Roles = { new UserRoleEntry { Email = "a@x", Role = AppRole.Admin } } }));

        // Cache never took the half-applied state: the user still resolves to the default.
        Assert.Equal(AppRole.Viewer, svc.GetRole("a@x"));
    }
}
