using Microsoft.Extensions.Configuration;
using Whiskers.Modules;
using Whiskers.Services.Agent;

namespace Whiskers.Tests;

/// <summary>Shared helper for the agent-tooling tests: builds an <see cref="AgentToolRegistry"/> from the MCP
/// tool types the default-enabled modules contribute — the production tool set the registry now derives from
/// (RoadToSAP §2.3) via <see cref="IModuleRegistry"/>, instead of the old whole-assembly reflection scan.</summary>
internal static class AgentToolTestHelpers
{
    public static IReadOnlyList<Type> DefaultModuleToolTypes() =>
        ModuleCatalog.DiscoverEnabled(new ConfigurationBuilder().Build())
            .SelectMany(m => m.McpToolTypes)
            .ToList();

    public static AgentToolRegistry DefaultRegistry() => new(DefaultModuleToolTypes());
}
