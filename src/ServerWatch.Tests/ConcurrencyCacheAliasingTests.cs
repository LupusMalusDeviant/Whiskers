using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models;
using ServerWatch.Services.Auth;
using ServerWatch.Services.Vault;

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

    // ---------------------------------------------------------------- MIT-2: VaultService

    private static VaultService NewVault()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY"] = "test-passphrase" })
            .Build();
        return new VaultService(config, NullLogger<VaultService>.Instance, TempStore());
    }

    [Fact]
    public async Task VaultService_GetSecret_returns_value_after_set_and_null_for_unknown()
    {
        var svc = NewVault();
        await svc.InitializeAsync();
        await svc.SetSecretAsync("api", "s3cr3t");

        Assert.Equal("s3cr3t", svc.GetSecret("api"));
        Assert.Null(svc.GetSecret("nope"));
    }

    [Fact]
    public async Task VaultService_ListSecrets_never_exposes_encrypted_values()
    {
        var svc = NewVault();
        await svc.InitializeAsync();
        await svc.SetSecretAsync("api", "s3cr3t");

        // Secret-safety: the listing carries metadata only, never the ciphertext.
        Assert.All(svc.ListSecrets(), e => Assert.Equal("", e.EncryptedValue));
    }

    [Fact]
    public async Task VaultService_reads_during_concurrent_set_delete_never_throw()
    {
        var svc = NewVault();
        await svc.InitializeAsync();
        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            var key = $"k{i}";
            tasks.Add(Task.Run(() => svc.SetSecretAsync(key, "v")));
            tasks.Add(Task.Run(() => svc.DeleteSecretAsync(key)));
            tasks.Add(Task.Run(() =>
            {
                _ = svc.ListSecrets();
                _ = svc.GetExpiringSecrets();
                _ = svc.GetSecret(key);
            }));
        }

        await Task.WhenAll(tasks); // pre-fix: unlocked reads threw InvalidOperationException mid-mutation
        _ = svc.ListSecrets();     // still usable afterwards
    }

    // ---------------------------------------------------------------- NIED-14: ServerConfigService

    private static ServerWatch.Services.ServerConfig.ServerConfigService NewServerConfig()
        => new(
            Options.Create(new DockerSettings()),
            NullLogger<ServerWatch.Services.ServerConfig.ServerConfigService>.Instance,
            TempStore());

    [Fact]
    public async Task ServerConfigService_DeleteSshKey_does_not_mutate_previously_read_reference()
    {
        var svc = NewServerConfig();
        await svc.AddServerAsync(new ServerConfig { Id = "s1", Name = "S1", SshKeyFileName = "id_rsa" });

        var before = svc.GetServer("s1")!; // reference into the cache before the change

        await svc.DeleteSshKeyAsync("s1");

        Assert.Equal("id_rsa", before.SshKeyFileName);    // previously-read reference untouched (clone)
        Assert.Null(svc.GetServer("s1")!.SshKeyFileName); // stored config actually cleared
    }

    [Fact]
    public async Task ServerConfigService_concurrent_update_and_read_never_throws()
    {
        var svc = NewServerConfig();
        await svc.AddServerAsync(new ServerConfig { Id = "s1", Name = "S1" });
        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            var n = i;
            tasks.Add(Task.Run(() => svc.UpdateServerAsync(new ServerConfig { Id = "s1", Name = $"n{n}" })));
            tasks.Add(Task.Run(() => { _ = svc.GetServers(); _ = svc.GetServer("s1"); }));
        }

        await Task.WhenAll(tasks);
        Assert.NotNull(svc.GetServer("s1"));
    }
}
