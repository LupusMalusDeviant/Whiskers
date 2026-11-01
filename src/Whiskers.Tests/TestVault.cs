using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

/// <summary>A started vault for tests that store a value encrypted with it (R1-31: webhook secrets, rollback snapshots).
/// One per test run: protecting and reading values changes nothing in it.</summary>
internal static class TestVault
{
    private static readonly Lazy<VaultService> Shared = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "sw-tests", "vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY"] = "test-vault-" + Guid.NewGuid().ToString("N") })
            .Build();
        var vault = new VaultService(config, NullLogger<VaultService>.Instance, Path.Combine(dir, "vault.json"));
        vault.InitializeAsync().GetAwaiter().GetResult();
        return vault;
    });

    public static VaultService Usable => Shared.Value;
}
