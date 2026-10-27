using Whiskers.Models;
using Whiskers.Services.Server;

namespace Whiskers.Services.Mutations;

/// <summary>
/// UI side of a compose deployment (R1-07): the deployment is decided on its own capability, then once per existing
/// container its <see cref="ComposePlan"/> touches (nested), so a protected container whose name differs from the
/// project is met as well. The plan is computed before the first decision and executed exactly as planned.
/// </summary>
public static class ComposeImpact
{
    /// <summary>Runs <paramref name="action"/> inside one <paramref name="capabilityId"/> decision per container
    /// (nested), so a single refusal — a protected container, a stricter override — stops the whole action.</summary>
    public static Task<T> RunForEachAsync<T>(this IUiMutationRunner runner, string capabilityId,
        IReadOnlyList<ContainerInfo> containers, string serverId, Func<Task<T>> action) =>
        Next(runner, capabilityId, containers, serverId, action, 0);

    /// <summary>
    /// Plans <paramref name="deployment"/>, decides <paramref name="capabilityId"/> with <paramref name="arguments"/>,
    /// then each container of the plan, and runs the deployment (<see cref="ComposePlan.DeployScript"/>) on its
    /// server — planned once more right before it runs, so a container that turned up in between and was not decided
    /// stops it. A failed <c>docker compose</c> is reported as a failed mutation.
    /// </summary>
    public static async Task<CommandResult> DeployAsync(this IUiMutationRunner runner, IHostCommandExecutor executor,
        IComposePlanner planner, string capabilityId, object arguments, ComposeDeployment deployment, bool pull = true)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(deployment);
        var plan = await planner.PlanAsync(deployment);
        return await runner.RunAsync(capabilityId, arguments,
            _ => runner.RunForEachAsync(capabilityId, plan.Containers, plan.ServerId, async () =>
            {
                var current = await planner.PlanAsync(deployment);
                current.EnsureDecided(MutationScope.Current);
                var r = await executor.ExecuteAsync(current.ServerId, current.DeployScript(pull), TimeSpan.FromMinutes(5));
                if (!r.Success) MutationScope.Fail("docker compose failed.");
                return r;
            }));
    }

    /// <summary>A <see cref="ComposePlanGate"/> that decides each container of the plan under
    /// <paramref name="capabilityId"/> (nested), then runs the deployment, which plans again and checks the result
    /// against these decisions (<see cref="ComposePlan.EnsureDecided"/>).</summary>
    public static ComposePlanGate GateFor(this IUiMutationRunner runner, string capabilityId) =>
        (plan, proceed) => runner.RunForEachAsync(capabilityId, plan.Containers, plan.ServerId, async () =>
        {
            await proceed();
            return true;
        });

    private static Task<T> Next<T>(IUiMutationRunner runner, string capabilityId, IReadOnlyList<ContainerInfo> containers,
        string serverId, Func<Task<T>> action, int index) =>
        index >= containers.Count
            ? action()
            : runner.RunAsync(capabilityId,
                new { containerId = containers[index].Id, name = containers[index].Name, serverId },
                _ => Next(runner, capabilityId, containers, serverId, action, index + 1));
}
