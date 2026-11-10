using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// R1-48: the scope guard refuses every undecided write in production, and tests boot the production DI graph. A
/// test that seeds state through a guarded service (an API key, a backup, a pause) does so here, inside a scope
/// for exactly the capability it exercises, which closes again before the code under test runs.
/// </summary>
internal static class GatewayArrange
{
    public static async Task<T> RunAsync<T>(string capabilityId, Func<Task<T>> arrange)
    {
        using (MutationScope.Begin(capabilityId, "test-arrange"))
            return await arrange();
    }

    public static async Task RunAsync(string capabilityId, Func<Task> arrange)
    {
        using (MutationScope.Begin(capabilityId, "test-arrange"))
            await arrange();
    }

    public static void Run(string capabilityId, Action arrange)
    {
        using (MutationScope.Begin(capabilityId, "test-arrange"))
            arrange();
    }

    /// <summary>Contract 0012 (D4 b): an operation that marks its target acts only on the decided object — the arrange
    /// scope decides <paramref name="target"/>.</summary>
    public static void Run(string capabilityId, MutationTarget target, Action arrange)
    {
        var principal = new MutationPrincipal(MutationPrincipalKind.Human, "user:test", "test",
            Whiskers.Models.McpPermissionLevels.Admin);
        using (MutationScope.Begin(capabilityId, "test-arrange", principal, new MutationRequest(capabilityId, principal,
                   MutationSurface.Ui, target, new Dictionary<string, System.Text.Json.JsonElement>())))
            arrange();
    }
}
