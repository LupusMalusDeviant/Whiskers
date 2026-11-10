using System.Text;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;
using Whiskers.Utils;

namespace Whiskers.Services.Deployment;

/// <summary>The two deployment paths contract 0013 moved onto the compose runner (U1).</summary>
public enum DeploymentPath
{
    /// <summary>The Deploy page's form tab and MCP <c>deploy_app</c> (<c>app.deploy</c>): a file of
    /// <see cref="DeploymentCompose"/>, owned through its marker line.</summary>
    AppDeploy,

    /// <summary>The Deploy page's compose tab (<c>compose.deploy</c>): the compose editor's path.</summary>
    ComposeTab,
}

/// <summary>
/// Contract 0013, D3 and D15: the plan of a deployment path, refused before anything is written when the project
/// belongs to someone else — a compose project of that name running from another directory, (form and
/// <c>deploy_app</c> only) a <c>docker-compose.yml</c> in the directory without the <see cref="DeploymentCompose.Marker"/>,
/// or a legacy container (no compose labels, created by the old paths) with a name the deployment would use. Wraps
/// the planner, so the page's decision, the gateway's target and the run all plan with the same checks.
/// </summary>
public sealed class DeploymentPathPlanner(IComposePlanner inner, IDockerService docker, IHostCommandExecutor executor,
    DeploymentPath path) : IComposePlanner
{
    private const string Absent = "__WHISKERS_ABSENT__";
    private const string WorkingDirLabel = "com.docker.compose.project.working_dir";
    private const int MaxFileChars = 2_000_000;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    public async Task<ComposePlan> PlanAsync(ComposeDeployment deployment, CancellationToken ct = default)
    {
        var plan = await inner.PlanAsync(deployment, ct);
        var existing = await docker.ListContainersAsync(all: true, serverId: plan.ServerId, ct);

        // D3: a project of that name that compose runs from another directory is someone else's.
        foreach (var c in existing)
            if (c.Labels.TryGetValue(ComposeModel.ProjectLabel, out var project) && project == plan.ProjectName
                && c.Labels.TryGetValue(WorkingDirLabel, out var wd) && !string.IsNullOrWhiteSpace(wd)
                && wd.TrimEnd('/') != plan.ProjectDirectory.TrimEnd('/'))
                throw new ComposePlanException(
                    $"A compose project '{plan.ProjectName}' already runs from another directory; choose another name.");

        // D3: the form and deploy_app own only a directory they wrote (the marker line).
        if (path == DeploymentPath.AppDeploy)
        {
            var file = ComposePaths.Join(plan.ProjectDirectory, "docker-compose.yml");
            if (await FirstLineAsync(plan.ServerId, file, ct) is { } first && first.TrimEnd('\r') != DeploymentCompose.Marker)
                throw new ComposePlanException(
                    $"'{file}' was not written by the Deploy form or deploy_app; another path owns it. Use the compose editor.");
        }

        // D15: a container of the old paths (no compose labels) with a name this deployment would use blocks it.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in plan.Services)
        {
            names.Add(s.ContainerName ?? $"{plan.ProjectName}-{s.Name}-1");
            // The old compose tab named each container after container_name or the service.
            if (path == DeploymentPath.ComposeTab) names.Add(s.ContainerName ?? s.Name);
        }
        if (existing.FirstOrDefault(c => !c.Labels.ContainsKey(ComposeModel.ProjectLabel)
                                         && names.Contains(c.Name.TrimStart('/'))) is { } legacy)
            throw new ComposePlanException(
                $"Container '{legacy.Name.TrimStart('/')}' was created by the old Deploy page and is not part of a compose " +
                "project, but this deployment would use its name. Remove it on the container page, or choose another name.");
        return plan;
    }

    public Task<ComposePlan> PlanEnvChangeAsync(ContainerInfo container, string serverId, Func<string?, string> newEnv,
        CancellationToken ct = default) =>
        throw new NotSupportedException("A deployment path plans a deployment, not an .env change.");

    /// <summary>The first line of a host file, or <c>null</c> when it does not exist. Any other failure refuses.</summary>
    private async Task<string?> FirstLineAsync(string serverId, string path, CancellationToken ct)
    {
        var quoted = ShellUtils.Quote(path);
        var result = await executor.ExecuteAsync(serverId,
            $"if [ -e {quoted} ]; then base64 -w0 < {quoted}; else echo {Absent}; fi", ReadTimeout, ct, MaxFileChars);
        if (!result.Success || result.OutputTruncated)
            throw new ComposePlanException($"'{path}' cannot be read on the server.");
        var output = result.Output.Trim();
        if (output == Absent) return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(output)).Split('\n')[0];
        }
        catch (FormatException)
        {
            throw new ComposePlanException($"'{path}' cannot be read on the server.");
        }
    }
}
