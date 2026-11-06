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
    private static readonly TimeSpan ConfigTimeout = TimeSpan.FromSeconds(30);

    private readonly IDockerService _docker;
    private readonly IHostCommandExecutor _executor;
    private readonly Whiskers.Services.Guardrails.IGuardrailStore? _guardrails;

    public ComposePlanner(IDockerService docker, IHostCommandExecutor executor,
        Whiskers.Services.Guardrails.IGuardrailStore? guardrails = null)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _guardrails = guardrails;
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
        var envTemplates = new Dictionary<string, string>(StringComparer.Ordinal);
        var env = ComposeModel.ParseEnv(envContent, envTemplates);
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

        var (declared, declaredSource, services) = ComposeModel.MergeWithSource(files, env, directory);
        // Contract 0006: what the files themselves expose, read before compose runs (a value the plan cannot interpolate
        // and a relative label_file refuse the plan here).
        var prefixes = ExposurePrefixes();
        var (modelExposures, modelShared) = ComposeModel.ExposuresOf(files, env, prefixes);
        var project = ComposeModel.EffectiveProjectName(deployment.ProjectName, env, declared, directory);
        // COMPOSE_REMOVE_ORPHANS in the .env makes a plain `up` remove orphans: planned (and passed explicitly) too.
        var removeOrphans = deployment.RemoveOrphans || ComposeModel.RemovesOrphans(env);

        var plan = new ComposePlan(serverId, project, directory, files.Select(f => f.Path).ToList(),
            inputs.Where(f => f.Content is not null).ToList(), deployment.EnvFileContent,
            HasEnvFile: envContent is not null, removeOrphans, services, [])
        {
            ProjectNameSource = ComposeModel.ProjectNameSource(deployment.ProjectName, env, envTemplates, declared,
                declaredSource),
        };
        // What the deployment mounts as compose itself resolves it on the server, besides the model's own reading.
        var config = await ConfigAsync(plan, ct);
        services = ComposeModel.WithEffects(services, ComposeConfigEffects.Parse(config, plan.ProjectName));
        var active = ComposeConfigEffects.ActiveServices(config, plan.ProjectName, prefixes);

        var existing = await _docker.ListContainersAsync(all: true, serverId: serverId, ct);
        // A container volumes_from takes over brings its own mounts along.
        var inherited = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var source in ComposeModel.SourceContainers(services, existing))
            inherited[source.Id] = await MountsOfContainerAsync(serverId, source.Id, ct);
        services = ComposeModel.ResolveContainers(services, existing, inherited);
        var impact = ComposeModel.Impact(project, services, removeOrphans, existing);
        var exposed = await ExposuresAsync(plan with { Services = services }, active, modelExposures, modelShared,
            existing, prefixes, ct);
        return exposed with { Impact = impact };
    }

    /// <summary>
    /// Contract 0006: the exposures of every service compose will start — the model's reading and compose's, united (an
    /// entry only compose reports is withheld); the labels of each service's image as it is on the host now (inspected
    /// locally, nothing pulled); and, through a <c>service:x</c> / <c>container:x</c> namespace, the exposures of x, to a
    /// depth of 5. An x that cannot be resolved refuses the plan. Services compose will not start expose nothing.
    /// </summary>
    private async Task<ComposePlan> ExposuresAsync(ComposePlan plan, IReadOnlyList<ComposeConfigEffects.ActiveService> active,
        IReadOnlyDictionary<string, IReadOnlyList<ComposeExposure>> model, IReadOnlyList<ComposeSharedNamespace> modelShared,
        IList<ContainerInfo> existing, IReadOnlyList<string> prefixes, CancellationToken ct)
    {
        var known = plan.Services.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        if (active.Any(a => !known.Contains(a.Name)))
            throw new ComposePlanException("docker compose resolves a service the deployment's files do not show.");
        var byName = active.ToDictionary(a => a.Name, StringComparer.Ordinal);

        // Own exposures: the model's entry where it read one, compose's (withheld) otherwise.
        var own = new Dictionary<string, IReadOnlyList<ComposeExposure>>(StringComparer.Ordinal);
        foreach (var service in active)
        {
            var read = model.GetValueOrDefault(service.Name) ?? [];
            var ids = read.Select(e => e.Identity).ToHashSet(StringComparer.Ordinal);
            own[service.Name] = ComposeModel.Distinct(read.Concat(service.Exposures.Where(e => !ids.Contains(e.Identity))));
        }

        // The images as they are on the host now: their labels count as the services' own (contract 0006, Amendment 4).
        var images = active.Select(a => a.Image).Distinct(StringComparer.Ordinal).ToList();
        var inspected = images.Count == 0
            ? new Dictionary<string, (string Id, IReadOnlyDictionary<string, string> Labels)?>()
            : await InspectImagesAsync(plan.ServerId, images, ct);

        var shared = new List<ComposeSharedContainer>();
        var inspectedContainers = new Dictionary<string, (IReadOnlyList<ComposeExposure>, IReadOnlyList<(string, string)>)>(StringComparer.Ordinal);

        async Task<IReadOnlyList<ComposeExposure>> Resolve(string service, int depth)
        {
            var result = new List<ComposeExposure>(own[service]);
            if (inspected.GetValueOrDefault(byName[service].Image) is { } image)
                result.AddRange(ComposeExposures.FromImageLabels(service, image.Labels, prefixes));
            foreach (var (field, reference) in byName[service].Shared)
            {
                var fromVariable = modelShared.FirstOrDefault(m => m.Service == service && m.Field == field) is not { } read
                                   || read.FromVariable;
                var inherited = await SharedAsync(reference, fromVariable, depth + 1);
                result.AddRange(inherited.Select(e => fromVariable
                    ? e with { Service = service, KeyWithheld = true, ValueWithheld = true, Detail = $"{ExposureClasses.Noun(e.Kind)} (withheld)" }
                    : e with { Service = service }));
            }
            return result;
        }

        async Task<IReadOnlyList<ComposeExposure>> SharedAsync(string reference, bool fromVariable, int depth)
        {
            if (depth > 5) throw new ComposePlanException("A shared namespace chain is too deep.");
            if (reference.StartsWith("service:", StringComparison.Ordinal))
            {
                var other = reference["service:".Length..];
                if (!byName.ContainsKey(other))
                    throw new ComposePlanException("A service shares the namespace of a service that does not run.");
                return await Resolve(other, depth);
            }
            var named = reference["container:".Length..];
            var container = ComposeModel.FindShared(named, existing);
            shared.Add(new ComposeSharedContainer(container.Id, container.Name.TrimStart('/'), fromVariable));
            if (!inspectedContainers.TryGetValue(container.Id, out var read))
                inspectedContainers[container.Id] = read = await InspectContainerAsync(plan.ServerId, container.Id, prefixes, ct);
            var (exposures, nested) = read;
            var result = new List<ComposeExposure>(exposures);
            foreach (var (_, id) in nested)
                result.AddRange(await SharedAsync($"container:{id}", fromVariable, depth + 1));
            return result;
        }

        var services = new List<ComposeServicePlan>();
        foreach (var s in plan.Services)
            services.Add(byName.TryGetValue(s.Name, out var a)
                ? s with { Active = true, Image = a.Image, Build = a.Build, DeclaresCgroup = a.DeclaresCgroup, Exposures = ComposeModel.Distinct(await Resolve(s.Name, 0)) }
                : s with { Active = false, Exposures = [] });
        return plan with
        {
            Services = services,
            ExposurePrefixes = prefixes,
            SharedContainers = shared.DistinctBy(c => c.Id).Select(c => shared.Where(x => x.Id == c.Id).Any(x => x.FromVariable)
                ? c with { FromVariable = true } : c).ToList(),
            SharesNamespaces = active.Any(a => a.Shared.Count > 0),
            PlannedImages = inspected.ToDictionary(i => i.Key, i => i.Value?.Id, StringComparer.Ordinal),
        };
    }

    /// <summary>The label prefixes a plan looks for: those of every stored preset (a pinned run's preset was one of them),
    /// so the rule can classify with the policy that decides (contract 0006, Amendment 2).</summary>
    private IReadOnlyList<string> ExposurePrefixes()
    {
        if (_guardrails is null) return Whiskers.Configuration.GuardrailPolicy.DefaultExposureLabelPrefixes;
        var policies = _guardrails.Config.Presets.Where(p => p?.Policy is not null).Select(p => p.Policy).Append(_guardrails.Current);
        return policies.SelectMany(p => p.EffectiveExposureLabelPrefixes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The images on the host, inspected locally (nothing is pulled): id and labels, or missing.</summary>
    private async Task<IReadOnlyDictionary<string, (string Id, IReadOnlyDictionary<string, string> Labels)?>> InspectImagesAsync(
        string serverId, IReadOnlyList<string> images, CancellationToken ct)
    {
        var result = await _executor.ExecuteAsync(serverId, ComposeExposures.InspectImagesCommand(images), ConfigTimeout, ct, MaxFileChars);
        if (!result.Success || result.OutputTruncated)
            throw new ComposePlanException("The images of the deployment could not be read on the server.");
        return ComposeExposures.ParseImages(result.Output, images);
    }

    /// <summary>A container another service shares a namespace with: its exposures and its own shared namespaces.</summary>
    private async Task<(IReadOnlyList<ComposeExposure>, IReadOnlyList<(string, string)>)> InspectContainerAsync(string serverId,
        string id, IReadOnlyList<string> prefixes, CancellationToken ct)
    {
        var result = await _executor.ExecuteAsync(serverId, ComposeExposures.InspectContainersCommand([id]), ReadTimeout, ct, MaxFileChars);
        if (!result.Success || result.OutputTruncated)
            throw new ComposePlanException("A container whose namespace is shared cannot be read.");
        var documents = ComposeExposures.ParseInspect(result.Output);
        if (documents.Count != 1) throw new ComposePlanException("A container whose namespace is shared cannot be read.");
        // Its exposures are attributed to the service that shares it; the key of a label is shown, never its value.
        return ComposeExposures.FromInspect("shared", documents[0], prefixes);
    }

    /// <summary>The deployment's mounts per service from <c>docker compose config</c> on the server (fail closed: a
    /// model compose cannot produce refuses the plan; its output is never quoted, it can hold .env values).</summary>
    private async Task<string> ConfigAsync(ComposePlan plan, CancellationToken ct)
    {
        var result = await _executor.ExecuteAsync(plan.ServerId, ComposeConfigEffects.Command(plan), ConfigTimeout, ct,
            MaxFileChars);
        if (!result.Success || result.OutputTruncated)
            throw new ComposePlanException("docker compose could not resolve the deployment on the server.");
        return result.Output;
    }

    /// <summary>The host paths and volume names a container mounts (<c>docker inspect</c>); unreadable refuses the plan.</summary>
    private async Task<IReadOnlyList<string>> MountsOfContainerAsync(string serverId, string containerId, CancellationToken ct)
    {
        var result = await _executor.ExecuteAsync(serverId,
            $"docker inspect --format '{{{{json .Mounts}}}}' {ShellUtils.Quote(containerId)}", ReadTimeout, ct, MaxFileChars);
        if (!result.Success || result.OutputTruncated)
            throw new ComposePlanException("The mounts of a container volumes are taken from cannot be read.");
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(result.Output.Trim());
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Null) return [];
            return document.RootElement.EnumerateArray()
                .SelectMany(m => new[] { Field(m, "Source"), Field(m, "Name") })
                .Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Distinct(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new ComposePlanException("The mounts of a container volumes are taken from cannot be read.");
        }

        static string? Field(System.Text.Json.JsonElement m, string name) =>
            m.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
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
