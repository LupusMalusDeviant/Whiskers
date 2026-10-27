using System.Text;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Server;
using Whiskers.Utils;

namespace Whiskers.Services.Mutations;

/// <summary>Turns a <see cref="ComposeDeployment"/> into a <see cref="ComposePlan"/> before anything is written
/// (R1-07). Throws <see cref="ComposePlanException"/> when the impact cannot be determined (fail closed).</summary>
public interface IComposePlanner
{
    Task<ComposePlan> PlanAsync(ComposeDeployment deployment, CancellationToken ct = default);

    /// <summary>
    /// A changed <c>.env</c> for the compose project <paramref name="container"/> belongs to, then <c>up</c>:
    /// <paramref name="newEnv"/> gets the current <c>.env</c> (<c>null</c> when there is none) and returns the new
    /// one. The project stays the container's project (its label, as <c>-p</c>), with its recorded compose files.
    /// </summary>
    Task<ComposePlan> PlanEnvChangeAsync(ContainerInfo container, string serverId, Func<string?, string> newEnv,
        CancellationToken ct = default);
}

/// <summary>
/// Reads what the deployment does not bring along from the host (the compose files of a directory, the existing
/// <c>.env</c>), works out the project the way <c>docker compose</c> does (<see cref="ComposeModel"/>) and lists the
/// server's containers, running or not, to find the ones the deployment touches. Read-only: it writes nothing.
/// </summary>
public sealed class ComposePlanner : IComposePlanner
{
    private const string Absent = "__WHISKERS_ABSENT__";
    private const string WorkingDirLabel = "com.docker.compose.project.working_dir";
    private const string ConfigFilesLabel = "com.docker.compose.project.config_files";
    private const int MaxFileChars = 2_000_000;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    private readonly IDockerService _docker;
    private readonly IHostCommandExecutor _executor;

    public ComposePlanner(IDockerService docker, IHostCommandExecutor executor)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public async Task<ComposePlan> PlanAsync(ComposeDeployment deployment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        if (string.IsNullOrWhiteSpace(deployment.ServerId))
            throw new ComposePlanException("A compose deployment needs an explicit server.");
        var serverId = deployment.ServerId;

        var directory = (deployment.ProjectDirectory
                         ?? (deployment.Files.Count > 0 ? ComposePaths.DirectoryOf(deployment.Files[0].Path) : null)
                         ?? throw new ComposePlanException("No compose file or directory given.")).TrimEnd('/');
        if (!directory.StartsWith('/'))
            throw new ComposePlanException($"Project directory '{directory}' is not absolute.");

        // The project's .env first: besides values for interpolation it carries compose's own switches
        // (COMPOSE_FILE, COMPOSE_PROJECT_NAME, COMPOSE_REMOVE_ORPHANS), which change what `up` does.
        var envContent = deployment.EnvFileContent
                         ?? await ReadAsync(serverId, ComposePaths.Join(directory, ".env"), ct);
        var env = ComposeModel.ParseEnv(envContent);
        ComposeModel.EnsureSwitchesKnown(env);

        var inputs = deployment.Files.Count > 0
            ? deployment.Files
            : ComposeModel.FilesFromEnv(env, directory) is { } configured
                ? configured
                : await DiscoverAsync(serverId, directory, ct);
        foreach (var file in inputs)
            if (!file.Path.StartsWith('/'))
                throw new ComposePlanException($"Compose file path '{file.Path}' is not absolute.");

        var files = new List<(string Path, string Content)>();
        foreach (var file in inputs)
            files.Add((file.Path, file.Content ?? await ReadAsync(serverId, file.Path, ct)
                ?? throw new ComposePlanException($"Compose file '{file.Path}' does not exist.")));

        var (declared, services) = ComposeModel.Merge(files, env);
        var project = ComposeModel.EffectiveProjectName(deployment.ProjectName, env, declared, directory);
        // COMPOSE_REMOVE_ORPHANS in the .env makes a plain `up` remove orphans: planned (and passed explicitly) too.
        var removeOrphans = deployment.RemoveOrphans || ComposeModel.RemovesOrphans(env);

        var existing = await _docker.ListContainersAsync(all: true, serverId: serverId, ct);
        var impact = ComposeModel.Impact(project, services, removeOrphans, existing);
        return new ComposePlan(serverId, project, directory, files.Select(f => f.Path).ToList(),
            inputs.Where(f => f.Content is not null).ToList(), deployment.EnvFileContent,
            HasEnvFile: envContent is not null, removeOrphans, services, impact);
    }

    public async Task<ComposePlan> PlanEnvChangeAsync(ContainerInfo container, string serverId,
        Func<string?, string> newEnv, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(newEnv);
        if (string.IsNullOrWhiteSpace(serverId))
            throw new ComposePlanException("A compose deployment needs an explicit server.");
        if (container.ComposeProject == "Standalone")
            throw new ComposePlanException($"Container '{container.Name}' is not part of a docker-compose project.");

        var directory = await WorkingDirectoryAsync(container, serverId, ct)
                        ?? throw new ComposePlanException(
                            $"The project directory of '{container.Name}' is unknown; its .env cannot be changed.");
        var files = container.Labels.TryGetValue(ConfigFilesLabel, out var recorded) && !string.IsNullOrWhiteSpace(recorded)
            ? recorded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(f => new ComposeFileInput(f)).ToList()
            : (IReadOnlyList<ComposeFileInput>)Array.Empty<ComposeFileInput>();
        var current = await ReadAsync(serverId, ComposePaths.Join(directory, ".env"), ct);
        return await PlanAsync(new ComposeDeployment(serverId, files)
        {
            ProjectDirectory = directory,
            ProjectName = container.ComposeProject,
            EnvFileContent = newEnv(current),
        }, ct);
    }

    /// <summary>The project directory compose recorded on the container, else the App Store / editor directory of
    /// its project when a compose file is there.</summary>
    private async Task<string?> WorkingDirectoryAsync(ContainerInfo container, string serverId, CancellationToken ct)
    {
        if (container.Labels.TryGetValue(WorkingDirLabel, out var wd) && !string.IsNullOrWhiteSpace(wd))
            return wd.TrimEnd('/');
        var dir = ComposeDeployment.DeploymentDirectory(container.ComposeProject);
        var file = ComposePaths.Join(dir, "docker-compose.yml");
        var check = await _executor.ExecuteAsync(serverId, $"test -f {ShellUtils.Quote(file)} && echo yes", ReadTimeout, ct);
        return check.Success && check.Output.Trim() == "yes" ? dir : null;
    }

    /// <summary>The first default compose file and the first override file in <paramref name="directory"/>, as
    /// compose picks them; the plan then names them explicitly (<c>-f</c>).</summary>
    private async Task<IReadOnlyList<ComposeFileInput>> DiscoverAsync(string serverId, string directory, CancellationToken ct)
    {
        if (!directory.StartsWith('/'))
            throw new ComposePlanException($"Project directory '{directory}' is not absolute.");
        string Loop(IEnumerable<string> names) =>
            $"for f in {string.Join(' ', names.Select(ShellUtils.Quote))}; do if [ -f \"$f\" ]; then echo \"$f\"; break; fi; done";
        var result = await _executor.ExecuteAsync(serverId,
            $"cd {ShellUtils.Quote(directory)} && {Loop(ComposeModel.DefaultFiles)} && {Loop(ComposeModel.OverrideFiles)}",
            ReadTimeout, ct);
        if (!result.Success)
            throw new ComposePlanException($"The directory '{directory}' cannot be read on the server.");
        var found = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (found.Length == 0 || !ComposeModel.DefaultFiles.Contains(found[0]))
            throw new ComposePlanException($"No compose file in '{directory}'.");
        return found.Select(f => new ComposeFileInput(ComposePaths.Join(directory, f))).ToList();
    }

    /// <summary>A file's content, or <c>null</c> when it does not exist. Any other failure throws.</summary>
    private async Task<string?> ReadAsync(string serverId, string path, CancellationToken ct)
    {
        var quoted = ShellUtils.Quote(path);
        var result = await _executor.ExecuteAsync(serverId,
            $"if [ -e {quoted} ]; then base64 -w0 < {quoted}; else echo {Absent}; fi", ReadTimeout, ct, MaxFileChars);
        if (!result.Success || result.OutputTruncated)
            throw new ComposePlanException($"'{path}' cannot be read on the server.");
        var output = result.Output.Trim();
        if (output == Absent) return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(output));
        }
        catch (FormatException)
        {
            throw new ComposePlanException($"'{path}' cannot be read on the server.");
        }
    }
}
