using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whiskers.Configuration;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Guardrails;
using Whiskers.Services.Agent.Providers;

namespace Whiskers.Tests;

public class AgentCompositionTests
{
    // Spiegelt die Program.cs-Registrierungen des Agent-Kerns (ohne Principal-Resolver, der
    // McpPermissionService/RoleService und damit /app/data-Schreibzugriffe zöge).
    private static ServiceProvider BuildCore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.Configure<AgentSettings>(_ => { });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        services.AddSingleton<IAgentGuardrailEngine>(GuardrailEngine.CreateDefault());
        services.AddSingleton(sp => new GuardrailStore(
            sp.GetService<ILogger<GuardrailStore>>(),
            Path.Combine(Path.GetTempPath(), "gr-smoke-test.json")));
        services.AddSingleton<IGuardrailStore>(sp => sp.GetRequiredService<GuardrailStore>());
        services.AddSingleton<IGuardrailRuleCatalog, GuardrailRuleCatalog>();
        services.AddSingleton<IAgentProviderFactory, AgentProviderFactory>();
        services.AddSingleton<IAgentToolCatalog, AgentToolCatalog>();
        services.AddSingleton<IAgentToolInvoker, AgentToolInvoker>();
        services.AddSingleton<IAgentService, AgentService>();
        services.AddSingleton<IClaudeCodeRuntime, ClaudeCodeRuntime>();

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void Core_agent_graph_resolves()
    {
        using var sp = BuildCore();
        Assert.NotNull(sp.GetRequiredService<IAgentService>());
        Assert.NotNull(sp.GetRequiredService<IAgentToolInvoker>());
        Assert.NotNull(sp.GetRequiredService<IAgentToolCatalog>());
        Assert.NotNull(sp.GetRequiredService<IGuardrailStore>());
        Assert.NotNull(sp.GetRequiredService<IGuardrailRuleCatalog>());
        Assert.NotNull(sp.GetRequiredService<IClaudeCodeRuntime>());
    }

    [Fact]
    public void Provider_factory_resolves_a_concrete_provider()
    {
        using var sp = BuildCore();
        var provider = sp.GetRequiredService<IAgentProviderFactory>()
            .Resolve(new AgentSettings { Provider = "openai" });
        Assert.Equal("openai", provider.Id);
    }

    [Fact]
    public void Rule_catalog_lists_all_builtin_rules()
    {
        using var sp = BuildCore();
        var ids = sp.GetRequiredService<IGuardrailRuleCatalog>().AvailableRules.Select(r => r.Id).ToList();
        Assert.Contains("principal-ceiling", ids);
        Assert.Contains("confirmation", ids);
        Assert.Contains("forbidden-argument", ids);
    }
}
