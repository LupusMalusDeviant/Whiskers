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
            _ => runner.RunMountsAsync(plan, () => runner.RunForEachAsync(capabilityId, plan.Containers, plan.ServerId, async () =>
            {
                var current = await planner.PlanAsync(deployment);
                current.EnsureDecided(MutationScope.Current);
                var r = await executor.ExecuteAsync(current.ServerId, current.DeployScript(pull), TimeSpan.FromMinutes(5));
                if (!r.Success) MutationScope.Fail("docker compose failed.");
                return r;
            })));
    }

    /// <summary>The capability a plan's mounts are decided under (R1-16): a deployment on the server, which sees every
    /// mounted path and volume as something it touches.</summary>
    public const string MountsCapability = "compose.deploy";

    /// <summary>Decides the plan's mounts (if any) before <paramref name="action"/>, so a mount of the Docker socket
    /// or of Whiskers' data is decided like a protected container.</summary>
    private static Task<T> RunMountsAsync<T>(this IUiMutationRunner runner, ComposePlan plan, Func<Task<T>> action) =>
        plan.Mounts() is { Count: > 0 } mounts
            ? runner.RunAsync(MountsCapability, new { serverId = plan.ServerId, mounts = string.Join('\n', mounts) },
                _ => action())
            : action();

    /// <summary>A <see cref="ComposePlanGate"/> that decides each container of the plan under
    /// <paramref name="capabilityId"/> (nested), then runs the deployment, which plans again and checks the result
    /// against these decisions (<see cref="ComposePlan.EnsureDecided"/>).</summary>
    public static ComposePlanGate GateFor(this IUiMutationRunner runner, string capabilityId) =>
        (plan, proceed) => runner.RunMountsAsync(plan, () => runner.RunForEachAsync(capabilityId, plan.Containers,
            plan.ServerId, async () =>
            {
                await proceed();
                return true;
            }));

    /// <summary>
    /// A <see cref="ComposePlanGate"/> for a deployment without a person at the keyboard (R1-08): each container of
    /// the plan is decided under <paramref name="capabilityId"/> on the authority of <paramref name="actor"/>'s owner
    /// (nested), then the deployment runs. A refusal stops it with <see cref="MutationRefusedException"/>.
    /// </summary>
    public static ComposePlanGate GateFor(this IAutomationMutationRunner runner, AutomationActor actor, string capabilityId)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(actor);
        return async (plan, proceed) =>
        {
            if (plan.Mounts() is not { Count: > 0 } mounts)
            {
                await NextAutomated(runner, actor, capabilityId, plan, proceed, 0);
                return;
            }
            var outcome = await runner.RunAsync(actor, MountsCapability,
                new { serverId = plan.ServerId, mounts = string.Join('\n', mounts) },
                _ => NextAutomated(runner, actor, capabilityId, plan, proceed, 0));
            if (outcome.Error is { } error)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            if (outcome.Status != MutationStatus.Executed)
                throw new MutationRefusedException(outcome.Reason ?? "The deployment's mounts were not allowed.",
                    outcome.Status, outcome.OperationId);
        };
    }

    private static async Task NextAutomated(IAutomationMutationRunner runner, AutomationActor actor, string capabilityId,
        ComposePlan plan, Func<Task> proceed, int index)
    {
        if (index >= plan.Containers.Count)
        {
            await proceed();
            return;
        }
        var container = plan.Containers[index];
        var outcome = await runner.RunAsync(actor, capabilityId,
            new { containerId = container.Id, name = container.Name, serverId = plan.ServerId },
            _ => NextAutomated(runner, actor, capabilityId, plan, proceed, index + 1));
        // A container that was not allowed — by policy, by its owner's authority, or because its audit intent could not
        // be written — is a refusal, whatever error the gateway kept; only an error of the deployment itself (thrown
        // further in) is passed on as it is.
        if (outcome.Status is not (MutationStatus.Executed or MutationStatus.Failed))
            throw new MutationRefusedException(outcome.Reason ?? $"'{container.Name}' was not allowed.", outcome.Status,
                outcome.OperationId);
        if (outcome.Error is { } error)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        if (outcome.Status != MutationStatus.Executed)
            throw new MutationRefusedException(outcome.Reason ?? $"'{container.Name}' was not allowed.", outcome.Status,
                outcome.OperationId);
    }

    private static Task<T> Next<T>(IUiMutationRunner runner, string capabilityId, IReadOnlyList<ContainerInfo> containers,
        string serverId, Func<Task<T>> action, int index) =>
        index >= containers.Count
            ? action()
            : runner.RunAsync(capabilityId,
                new { containerId = containers[index].Id, name = containers[index].Name, serverId },
                _ => Next(runner, capabilityId, containers, serverId, action, index + 1));
}
