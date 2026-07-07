using System.IO;
using Microsoft.Extensions.Configuration;
using ServerWatch.Configuration;
using ServerWatch.Models;
using ServerWatch.Models.Agent;
using ServerWatch.Services.Agent;

namespace ServerWatch.Tests;

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
        await new AgentSettingsStore(path).SaveAsync(new AgentSettings
        {
            Enabled = true,
            Provider = "anthropic",
            Model = "claude-opus-4-8",
            Endpoint = "",
            ApiKey = "sk-secret",
            MaxToolIterations = 5,
        }, Admin);

        // Genau der Weg, den Program.cs nutzt: JSON-Datei → Config → Section "Agent" → AgentSettings.
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        var bound = config.GetSection(AgentSettings.SectionName).Get<AgentSettings>()!;

        Assert.True(bound.Enabled);
        Assert.Equal("anthropic", bound.Provider);
        Assert.Equal("claude-opus-4-8", bound.Model);
        Assert.Equal("sk-secret", bound.ApiKey);
        Assert.Equal(5, bound.MaxToolIterations);
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
}
