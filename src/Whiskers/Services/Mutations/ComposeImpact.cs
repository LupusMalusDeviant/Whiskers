using Whiskers.Models;
using Whiskers.Services.Server;

namespace Whiskers.Services.Mutations;

/// <summary>A UI compose deployment (contract 0013, D4): the plan it ran and what the runner did. <see cref="Success"/>
/// is the runner's, so the gateway records a refused or violating run as failed (<see cref="MutationResults"/>).</summary>
public sealed record ComposeDeployResult(ComposePlan Plan, ComposeRunResult Run)
{
    public bool Success => Run.Success;
}

/// <summary>
/// UI side of a compose deployment (R1-07): the deployment is decided on its own capability, then once per existing
/// container its <see cref="ComposePlan"/> touches (nested), so a protected container whose name differs from the
/// project is met as well. The plan is computed before the first decision and executed exactly as planned.
/// </summary>
public static class ComposeImpact
{
    /// <summary>The budget of each command of a compose run (contract 0006; README "The 5-minute budget", claim RM-13).</summary>
    internal static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(5);

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
        IComposePlanner planner, IComposeRunner composeRunner, string capabilityId, object arguments,
        ComposeDeployment deployment, bool pull = true)
    {
        var deployed = await runner.RunDeploymentAsync(executor, planner, composeRunner, capabilityId, arguments, deployment, pull);
        var run = deployed.Run;
        return new CommandResult
        {
            ExitCode = run.Success ? 0 : 1, Output = run.Output,
            Error = run.Refusal ?? (run.Violations.Count > 0 ? string.Join("; ", run.Violations) : run.Error),
        };
    }

    /// <summary>
    /// <see cref="DeployAsync"/> with the plan and the runner's own result (contract 0013, D4): a page tells a refusal
    /// before <c>up</c> (<see cref="ComposeRunResult.Refusal"/>) from a violation of the re-check, shows the project, and
    /// follows the runner's steps ①–⑤ through <paramref name="progress"/>.
    /// </summary>
    public static async Task<ComposeDeployResult> RunDeploymentAsync(this IUiMutationRunner runner,
        IHostCommandExecutor executor, IComposePlanner planner, IComposeRunner composeRunner, string capabilityId,
        object arguments, ComposeDeployment deployment, bool pull = true, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(composeRunner);
        ArgumentNullException.ThrowIfNull(deployment);
        var plan = await planner.PlanAsync(deployment);
        return await runner.RunAsync(capabilityId, arguments, _ =>
        {
            ReportProject(plan);
            return runner.RunMountsAsync(plan, () => runner.RunExposuresAsync(plan, () =>
                runner.RunForEachAsync(capabilityId, plan.Containers, plan.ServerId, async () =>
                {
                    var current = await planner.PlanAsync(deployment);
                    current.EnsureDecided(MutationScope.Current);
                    // Contract 0006: write, pull, build, inspect the images, up — then the re-check after up.
                    var run = await composeRunner.RunAsync(current,
                        (command, timeout, ct) => executor.ExecuteMutationAsync(current.ServerId, command, timeout, ct),
                        new ComposeRunOptions(pull, RunBudget) { Progress = progress });
                    return new ComposeDeployResult(current, run);
                })));
        });
    }

    /// <summary>
    /// The container page's <c>.env</c> save (R1-07, R1-16, contract 0006): plans the project with the new
    /// <paramref name="envContent"/>, decides its mounts and its exposures (nested <c>compose.deploy</c>), then each
    /// affected container under <c>container.env_file_save</c> (nested, the saved one first), plans again right before
    /// writing and runs it through <paramref name="composeRunner"/> — pulling and building only what is missing (no
    /// <c>--pull</c>), with the re-check after <c>up</c>. A refusal throws <see cref="MutationRefusedException"/>.
    /// </summary>
    public static async Task<CommandResult> SaveEnvAsync(this IUiMutationRunner runner, IHostCommandExecutor executor,
        IComposePlanner planner, IComposeRunner composeRunner, ContainerInfo container, string serverId, string envContent)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(composeRunner);
        ArgumentNullException.ThrowIfNull(container);
        var plan = await planner.PlanEnvChangeAsync(container, serverId, _ => envContent);
        var affected = new List<ContainerInfo> { container };
        affected.AddRange(plan.Containers.Where(c => c.Id != container.Id));
        return await runner.RunMountsAsync(plan, () => runner.RunExposuresAsync(plan, () =>
            runner.RunForEachAsync("container.env_file_save", affected, serverId, async () =>
            {
                // Planned again right before writing: a container or an exposure that turned up meanwhile was not decided.
                var current = await planner.PlanEnvChangeAsync(container, serverId, _ => envContent);
                current.EnsureDecided(MutationScope.Current);
                var run = await composeRunner.RunAsync(current,
                    (command, timeout, ct) => executor.ExecuteMutationAsync(serverId, command, timeout, ct),
                    new ComposeRunOptions(Pull: false, TimeSpan.FromMinutes(2)));
                if (!run.Success) MutationScope.Fail("Writing .env or docker compose up failed.");
                return new CommandResult
                {
                    ExitCode = run.Success ? 0 : 1, Output = run.Output,
                    Error = run.Refusal ?? (run.Violations.Count > 0 ? string.Join("; ", run.Violations) : run.Error),
                };
            })));
    }

    /// <summary>Contract 0006: whether a plan needs the exposure decision — it has exposures, or it shares a namespace
    /// (<c>service:x</c> / <c>container:x</c>, Amendment 7).</summary>
    public static bool NeedsExposureDecision(ComposePlan plan) => plan.Exposures().Count > 0 || plan.SharesNamespaces;

    /// <summary>
    /// The arguments of the exposure decision (contract 0006): <c>exposure</c> holds the literal entries and is audited,
    /// <c>exposureShown</c> what may be shown of every entry (audited: the key, never a value; the kind only for a key
    /// from a variable), <c>exposureWithheld</c> the entries whose key came from a variable and
    /// <c>exposureValueWithheld</c> those whose value only is withheld (both payloads: the sinks keep only their length),
    /// <c>exposureClasses</c> is informational, <c>related</c> / <c>relatedWithheld</c> the containers a namespace is
    /// shared with. Whiskers sets them; a call on the MCP, agent, webhook or terminal surface may not bring them
    /// (review B1).
    /// </summary>
    public static object ExposureArguments(ComposePlan plan)
    {
        var exposures = plan.Exposures();
        var withheldShared = plan.WithheldSharedIdentities().ToHashSet(StringComparer.Ordinal);
        return new
        {
            serverId = plan.ServerId,
            exposure = ComposeExposures.Lines(exposures.Where(e => !e.Withheld)),
            // Review m1: what may be shown of every exposure (service and detail: the key, never a value; the kind only
            // for a key from a variable) — audited, so a person's decision keeps every exposure's key.
            exposureShown = string.Join('\n', exposures.Select(ComposeExposures.Shown).Distinct(StringComparer.Ordinal)),
            exposureWithheld = ComposeExposures.Lines(exposures.Where(e => e.KeyWithheld)),
            exposureValueWithheld = ComposeExposures.Lines(exposures.Where(e => e is { KeyWithheld: false, ValueWithheld: true })),
            exposureClasses = ComposeExposures.Classes(exposures, plan.ExposurePrefixes),
            related = string.Join('\n', plan.SharedIdentities().Where(i => !withheldShared.Contains(i))),
            relatedWithheld = string.Join('\n', withheldShared),
        };
    }

    /// <summary>Contract 0006: decides the plan's exposures (if any, or a shared namespace) as a nested
    /// <see cref="MountsCapability"/> decision before <paramref name="action"/>, the way <see cref="RunMountsAsync"/> does
    /// mounts. A person is not asked to confirm their own action (PC-D1): the decision is audited with every exposure.</summary>
    public static Task<T> RunExposuresAsync<T>(this IUiMutationRunner runner, ComposePlan plan, Func<Task<T>> action) =>
        NeedsExposureDecision(plan)
            ? runner.RunAsync(MountsCapability, ExposureArguments(plan), _ => action())
            : action();

    /// <summary>The read-back key of the compose project a deployment ran under (R1-37).</summary>
    public const string ProjectFact = "composeProject";

    /// <summary>R1-37: tells the <c>compose.deploy</c> verifier the project the plan resolved (the arguments carry what the
    /// caller named, which a top-level <c>name:</c> or <c>COMPOSE_PROJECT_NAME</c> may override). An interpolated name is
    /// reported as empty: its value is not to be kept, and the verifier then records the deployment as not verifiable.</summary>
    public static void ReportProject(ComposePlan plan)
    {
        MutationScope.ReadBack(ProjectFact, plan.ProjectNameSource is null ? plan.ProjectName : "");
        MutationScope.ReadBack(ServicesFact, string.Join('\n', plan.Services.Select(s => s.Name)));
    }

    /// <summary>The read-back key of the services the deployed files define, one per line (R1-37).</summary>
    public const string ServicesFact = "composeServices";

    /// <summary>The capability a plan's mounts are decided under (R1-16): a deployment on the server, which sees every
    /// mounted path and volume as something it touches.</summary>
    public const string MountsCapability = "compose.deploy";

    /// <summary>Decides the plan's mounts (if any) before <paramref name="action"/>, so a mount of the Docker socket
    /// or of Whiskers' data is decided like a protected container.</summary>
    public static Task<T> RunMountsAsync<T>(this IUiMutationRunner runner, ComposePlan plan, Func<Task<T>> action) =>
        plan.Mounts() is { Count: > 0 } mounts
            ? runner.RunAsync(MountsCapability, new { serverId = plan.ServerId, mounts = string.Join('\n', mounts) },
                _ => action())
            : action();

    /// <summary>A <see cref="ComposePlanGate"/> that decides each container of the plan under
    /// <paramref name="capabilityId"/> (nested), then runs the deployment, which plans again and checks the result
    /// against these decisions (<see cref="ComposePlan.EnsureDecided"/>).</summary>
    public static ComposePlanGate GateFor(this IUiMutationRunner runner, string capabilityId) =>
        (plan, proceed) => runner.RunMountsAsync(plan, () => runner.RunExposuresAsync(plan, () =>
            runner.RunForEachAsync(capabilityId, plan.Containers, plan.ServerId, async () =>
            {
                await proceed();
                return true;
            })));

    /// <summary>
    /// A <see cref="ComposePlanGate"/> for a deployment without a person at the keyboard (R1-08): each container of
    /// the plan is decided under <paramref name="capabilityId"/> on the authority of <paramref name="actor"/>'s owner
    /// (nested), then the deployment runs. A refusal stops it with <see cref="MutationRefusedException"/>.
    /// </summary>
    public static ComposePlanGate GateFor(this IAutomationMutationRunner runner, AutomationActor actor, string capabilityId)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(actor);
        return (plan, proceed) =>
        {
            // Contract 0006: the exposures are decided like the mounts, nested, on the owner's authority.
            Task Exposed() => NeedsExposureDecision(plan)
                ? Nested(runner, actor, ExposureArguments(plan), "The deployment's exposure changes were not allowed.",
                    () => NextAutomated(runner, actor, capabilityId, plan, proceed, 0))
                : NextAutomated(runner, actor, capabilityId, plan, proceed, 0);
            return plan.Mounts() is { Count: > 0 } mounts
                ? Nested(runner, actor, new { serverId = plan.ServerId, mounts = string.Join('\n', mounts) },
                    "The deployment's mounts were not allowed.", Exposed)
                : Exposed();
        };
    }

    /// <summary>A nested <see cref="MountsCapability"/> decision of an automation: a refusal (or an error of the decision
    /// itself) stops the deployment with <see cref="MutationRefusedException"/>; an error of the deployment is passed on.</summary>
    private static async Task Nested(IAutomationMutationRunner runner, AutomationActor actor, object arguments, string refused,
        Func<Task> inner)
    {
        var outcome = await runner.RunAsync(actor, MountsCapability, arguments, _ => inner());
        if (outcome.Error is { } error)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        if (outcome.Status != MutationStatus.Executed)
            throw new MutationRefusedException(outcome.Reason ?? refused, outcome.Status, outcome.OperationId);
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
