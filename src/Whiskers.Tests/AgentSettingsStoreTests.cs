using System.IO;
using Microsoft.Extensions.Configuration;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;

namespace Whiskers.Tests;

public class AgentSettingsStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "sw-agentcfg-" + Guid.NewGuid().ToString("N") + ".json");

    private static AgentPrincipal Admin => new(AgentPrincipalKind.WebUser, "admin", McpPermissionLevels.Admin, null, UserEmail: "a@x");
    private static AgentPrincipal Viewer => new(AgentPrincipalKind.WebUser, "viewer", McpPermissionLevels.Read, null, UserEmail: "v@x");

    [Fact]
    public async Task Saved_file_binds_back_through_the_config_system()
    {
        var path = TempPath();
        var vaultKey = Whiskers.Services.Vault.VaultBackedSettings.NewKey("Agent", "ApiKey");
        await new AgentSettingsStore(path).SaveAsync(new AgentSettings
        {
            Enabled = true,
            Provider = "anthropic",
            Model = "claude-opus-4-8",
            Endpoint = "",
            MaxToolIterations = 5,
        }, Admin, Whiskers.Services.Vault.SecretFieldChange.Set(vaultKey));

        // Genau der Weg, den Program.cs nutzt: JSON-Datei → Config → Section "Agent" → AgentSettings.
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        var bound = config.GetSection(AgentSettings.SectionName).Get<AgentSettings>()!;

        Assert.True(bound.Enabled);
        Assert.Equal("anthropic", bound.Provider);
        Assert.Equal("claude-opus-4-8", bound.Model);
        // The key is only named (R1-30); the options read it from the vault.
        Assert.Equal("", bound.ApiKey);
        Assert.Equal(vaultKey, config["Agent:ApiKeyRef"]);
        Assert.Equal(5, bound.MaxToolIterations);
    }

    [Fact]
    public async Task A_plaintext_api_key_is_refused_and_a_later_save_keeps_the_named_key()
    {
        var path = TempPath();
        var store = new AgentSettingsStore(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAsync(new AgentSettings { ApiKey = "plain-" + Guid.NewGuid().ToString("N") }, Admin));
        Assert.False(File.Exists(path));

        var vaultKey = Whiskers.Services.Vault.VaultBackedSettings.NewKey("Agent", "ApiKey");
        await store.SaveAsync(new AgentSettings { Provider = "openai" }, Admin, Whiskers.Services.Vault.SecretFieldChange.Set(vaultKey));
        var previous = await store.SaveAsync(new AgentSettings { Provider = "anthropic" }, Admin);   // keep

        Assert.Equal(vaultKey, previous!["ApiKeyRef"]!.GetValue<string>());
        Assert.Equal(vaultKey, (await store.ReadSectionAsync())!["ApiKeyRef"]!.GetValue<string>());
    }

    [Fact]
    public async Task File_only_contains_the_Agent_section()
    {
        var path = TempPath();
        await new AgentSettingsStore(path).SaveAsync(new AgentSettings { Provider = "ollama" }, Admin);

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"Agent\"", json);
        Assert.Contains("ollama", json);
    }

    [Fact] // NIED-21.8: persisting agent settings (ApiKey + SystemPrompt injection vector) is admin-only
    public async Task Save_requires_admin()
    {
        var store = new AgentSettingsStore(TempPath());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync(new AgentSettings(), Viewer));
        await store.SaveAsync(new AgentSettings { Provider = "ollama" }, Admin); // admin succeeds, no throw
    }

    [Fact] // R1-11: concurrent saves never move each other's document; each save leaves exactly its own file behind
    public async Task Concurrent_saves_are_isolated()
    {
        var path = TempPath();
        var store = new AgentSettingsStore(path);
        var saves = Enumerable.Range(0, 32)
            .Select(i => Task.Run(() => store.SaveAsync(new AgentSettings { Provider = "p" + i }, Admin)));
        await Task.WhenAll(saves);   // none fails

        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        var bound = config.GetSection(AgentSettings.SectionName).Get<AgentSettings>()!;
        Assert.StartsWith("p", bound.Provider);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
    }

    [Fact] // R1-11: a failed save releases the store — the next save runs instead of waiting forever
    public async Task A_failed_save_does_not_block_the_next_one()
    {
        var path = TempPath();
        Directory.CreateDirectory(path);   // the target is a directory: replacing it fails
        var store = new AgentSettingsStore(path);

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(new AgentSettings(), Admin));
        var second = store.SaveAsync(new AgentSettings(), Admin);
        Assert.Same(second, await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(10))));
        await Assert.ThrowsAnyAsync<Exception>(() => second);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
    }
}
