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
    /// Decides <paramref name="capabilityId"/> with <paramref name="arguments"/>, then each container of
    /// <paramref name="plan"/>, and runs the plan (<see cref="ComposePlan.DeployScript"/>) on its server. A failed
    /// <c>docker compose</c> is reported as a failed mutation.
    /// </summary>
    public static Task<CommandResult> DeployAsync(this IUiMutationRunner runner, IHostCommandExecutor executor,
        string capabilityId, object arguments, ComposePlan plan, bool pull = true)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(plan);
        return runner.RunAsync(capabilityId, arguments,
            _ => runner.RunForEachAsync(capabilityId, plan.Containers, plan.ServerId, async () =>
            {
                plan.EnsureDecided(MutationScope.Current);
                var r = await executor.ExecuteAsync(plan.ServerId, plan.DeployScript(pull), TimeSpan.FromMinutes(5));
                if (!r.Success) MutationScope.Fail("docker compose failed.");
                return r;
            }));
    }

    /// <summary>A <see cref="ComposePlanGate"/> that decides each container of the plan under
    /// <paramref name="capabilityId"/> (nested), then runs the deployment.</summary>
    public static ComposePlanGate GateFor(this IUiMutationRunner runner, string capabilityId) =>
        (plan, proceed) => runner.RunForEachAsync(capabilityId, plan.Containers, plan.ServerId, async () =>
        {
            plan.EnsureDecided(MutationScope.Current);
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
