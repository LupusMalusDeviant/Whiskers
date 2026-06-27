using System.IO;
using Microsoft.Extensions.Configuration;
using ServerWatch.Configuration;
using ServerWatch.Services.Agent;

namespace ServerWatch.Tests;

public class AgentSettingsStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "sw-agentcfg-" + Guid.NewGuid().ToString("N") + ".json");

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
        });

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
        await new AgentSettingsStore(path).SaveAsync(new AgentSettings { Provider = "ollama" });

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"Agent\"", json);
        Assert.Contains("ollama", json);
    }
}
