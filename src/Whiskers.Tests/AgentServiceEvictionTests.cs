using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Guardrails;
using Whiskers.Services.Agent.Providers;

namespace Whiskers.Tests;

public class AgentServiceEvictionTests
{
    private sealed class StubProvider : IAgentLlmProvider
    {
        public string Id => "stub";
        public async IAsyncEnumerable<AgentStreamDelta> StreamAsync(
            AgentCompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;   // never invoked in the eviction test
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class StubFactory : IAgentProviderFactory
    {
        public IReadOnlyCollection<string> SupportedProviderIds { get; } = new[] { "stub" };
        public IAgentLlmProvider Resolve(AgentSettings settings) => new StubProvider();
    }

    private sealed class StubOptions : IOptionsMonitor<AgentSettings>
    {
        public AgentSettings CurrentValue { get; } = new();
        public AgentSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AgentSettings, string?> listener) => null;
    }

    private sealed class StubCatalog : IAgentToolCatalog
    {
        public IReadOnlyList<AgentToolDefinition> GetVisibleTools(AgentContext context) => Array.Empty<AgentToolDefinition>();
    }

    private sealed class StubInvoker : IAgentToolInvoker
    {
        public Task<AgentToolResult> InvokeAsync(AgentToolCall call, AgentContext context, CancellationToken ct = default) =>
            Task.FromResult(new AgentToolResult(call.Id, "", false,
                new GuardrailDecision(GuardrailVerdict.Allow, "", Array.Empty<string>())));
    }

    private static AgentContext Ctx(string id) => new(id,
        new AgentPrincipal(AgentPrincipalKind.WebUser, "t", McpPermissionLevels.Read, null, UserEmail: "t@x"),
        AgentOrigin.WebUi, GuardrailPolicy.SafeDefault());

    [Fact]
    public async Task Oldest_sessions_are_evicted_beyond_the_cap()
    {
        var svc = new AgentService(new StubFactory(), new StubOptions(), new StubCatalog(),
            new StubInvoker(), GuardrailEngine.CreateDefault(), new AgentToolRegistry());

        var ids = new List<string>();
        for (var i = 0; i < 260; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            ids.Add(id);
            await svc.StartSessionAsync(Ctx(id));
        }

        Assert.Null(await svc.ResumeSessionAsync(ids[0]));    // früheste evicted
        Assert.NotNull(await svc.ResumeSessionAsync(ids[^1])); // jüngste vorhanden
    }
}
