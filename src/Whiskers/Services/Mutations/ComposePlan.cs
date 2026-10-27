using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Models;
using Whiskers.Utils;
using YamlDotNet.RepresentationModel;

namespace Whiskers.Services.Mutations;

/// <summary>A compose file of a deployment: its absolute path on the host and, when Whiskers writes it, the content
/// it writes (<c>null</c> = read the file that is already there).</summary>
public sealed record ComposeFileInput(string Path, string? Content = null);

/// <summary>
/// What a compose deployment is asked to run (R1-07): the server, the compose files (or a directory whose default
/// files are used), the project directory, an explicit project name (<c>-p</c>), the <c>.env</c> it writes, if any,
/// and whether orphans are removed. <see cref="IComposePlanner"/> turns it into a <see cref="ComposePlan"/>.
/// </summary>
public sealed record ComposeDeployment(string ServerId, IReadOnlyList<ComposeFileInput> Files)
{
    /// <summary>The project directory (<c>--project-directory</c>); defaults to the first file's directory.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>An explicit project name (<c>-p</c>), e.g. the label of the project a changed <c>.env</c> belongs to.</summary>
    public string? ProjectName { get; init; }

    /// <summary>The <c>.env</c> the deployment writes into the project directory before <c>up</c>; <c>null</c> = the
    /// one that is there (if any) is used as it is.</summary>
    public string? EnvFileContent { get; init; }

    /// <summary><c>up --remove-orphans</c>: containers of the project whose service is no longer defined are removed.</summary>
    public bool RemoveOrphans { get; init; }

    /// <summary>A file Whiskers writes into <paramref name="directory"/> and deploys, nothing else from that directory.</summary>
    public static ComposeDeployment FromContent(string serverId, string directory, string content,
        string fileName = "docker-compose.yml") =>
        new(serverId, [new ComposeFileInput(ComposePaths.Join(directory, fileName), content)]) { ProjectDirectory = directory };

    /// <summary>The compose files already in <paramref name="directory"/>, found the way <c>docker compose</c> finds
    /// them there (the first default file plus the first override file).</summary>
    public static ComposeDeployment FromDirectory(string serverId, string directory) =>
        new(serverId, Array.Empty<ComposeFileInput>()) { ProjectDirectory = directory };

    /// <summary>Where the compose editor, the App Store and the MCP <c>deploy_compose</c> tool put a project.</summary>
    public static string DeploymentDirectory(string projectName) => $"/opt/deployments/{projectName}";
}

/// <summary>Decides the containers of <paramref name="plan"/> and, when every one is allowed, runs
/// <paramref name="proceed"/> (the planned deployment). Used where the plan can only be made inside the operation,
/// e.g. a Git deployment after fetching the repository (R1-07).</summary>
public delegate Task ComposePlanGate(ComposePlan plan, Func<Task> proceed);

/// <summary>Why an existing container is part of a plan.</summary>
public enum ComposeImpactKind
{
    /// <summary>A container of the project whose service the files define: <c>up</c> may recreate it.</summary>
    Recreate,

    /// <summary>A container of the project whose service the files no longer define, removed by <c>--remove-orphans</c>.</summary>
    RemoveOrphan,

    /// <summary>A container outside the project with a name the deployment wants (<c>container_name</c> or the
    /// default <c>project-service-n</c>).</summary>
    NameCollision,
}

public sealed record ComposeImpactEntry(ContainerInfo Container, ComposeImpactKind Kind);

/// <summary>A service of the merged files with the container name it gets, if one is set.</summary>
public sealed record ComposeServicePlan(string Name, string? ContainerName);

/// <summary>A compose deployment's impact could not be determined; the deployment does not run (fail closed).</summary>
public sealed class ComposePlanException(string message) : Exception(message);

/// <summary>
/// One compose deployment, resolved before anything is written (R1-07, INV-24/57): the effective project name, the
/// files and project directory, the services, and every existing container the deployment recreates, removes or
/// collides with (<see cref="Impact"/>). Decisions are made on <see cref="Impact"/>; the commands are built from the
/// same plan with an explicit <c>-p</c>, <c>--project-directory</c> and <c>-f</c>, so execution cannot pick another
/// project or other files than the ones decided on.
/// </summary>
public sealed record ComposePlan(
    string ServerId,
    string ProjectName,
    string ProjectDirectory,
    IReadOnlyList<string> Files,
    IReadOnlyList<ComposeFileInput> Writes,
    string? EnvFileContent,
    bool HasEnvFile,
    bool RemoveOrphans,
    IReadOnlyList<ComposeServicePlan> Services,
    IReadOnlyList<ComposeImpactEntry> Impact)
{
    /// <summary>The existing containers the plan touches, each once.</summary>
    public IReadOnlyList<ContainerInfo> Containers => Impact.Select(i => i.Container).ToList();

    /// <summary>Path of the project's <c>.env</c>.</summary>
    public string EnvFilePath => ComposePaths.Join(ProjectDirectory, ".env");

    /// <summary><c>docker compose</c> with the plan's project, directory and files, then <paramref name="subcommand"/>.</summary>
    public string Command(string subcommand)
    {
        var sb = new StringBuilder("docker compose -p ").Append(ShellUtils.Quote(ProjectName))
            .Append(" --project-directory ").Append(ShellUtils.Quote(ProjectDirectory));
        foreach (var file in Files)
            sb.Append(" -f ").Append(ShellUtils.Quote(file));
        if (HasEnvFile)
            sb.Append(" --env-file ").Append(ShellUtils.Quote(EnvFilePath));
        return sb.Append(' ').Append(subcommand).ToString();
    }

    public string PullCommand => Command("pull");

    public string BuildCommand => Command("build --pull");

    public string UpCommand => Command(RemoveOrphans ? "up -d --remove-orphans" : "up -d");

    /// <summary>The whole deployment as one host command: write the planned files and <c>.env</c> (exactly the
    /// content planned), optionally pull, then <c>up</c> — all with the plan's project, directory and files.</summary>
    public string DeployScript(bool pull)
    {
        var steps = new List<string> { $"mkdir -p {ShellUtils.Quote(ProjectDirectory)}" };
        foreach (var file in Writes)
            steps.Add(WriteCommand(file.Path, file.Content!));
        if (EnvFileContent is not null)
            steps.Add(WriteCommand(EnvFilePath, EnvFileContent));
        if (pull) steps.Add(PullCommand + " 2>&1");
        steps.Add(UpCommand + " 2>&1");
        return string.Join(" && ", steps);
    }

    private static string WriteCommand(string path, string content) =>
        $"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(content))}' | base64 -d > {ShellUtils.Quote(path)}";

    /// <summary>
    /// Throws unless every container of <see cref="Impact"/> was decided on this server by an enclosing gateway
    /// decision (its target, related resources or arguments, R1-07): a plan computed again at execution must not
    /// touch a container nobody decided on.
    /// </summary>
    public void EnsureDecided(MutationScope? scope)
    {
        var missing = Impact.Where(i => scope is null || !scope.Covers(ServerId, i.Container.Id, i.Container.Name))
            .Select(i => i.Container.Name).ToList();
        if (missing.Count > 0)
            throw new ComposePlanException(
                $"The deployment would touch containers that were not decided: {string.Join(", ", missing)}.");
    }

    /// <summary>The identities a decision on this plan must cover: each impacted container's id and name.</summary>
    public IReadOnlyList<string> DecidedIdentities() =>
        Impact.SelectMany(i => new[] { i.Container.Id, i.Container.Name })
            .Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
}

internal static class ComposePaths
{
    public static string Join(string directory, string file) =>
        file.StartsWith('/') ? file : directory.TrimEnd('/') + "/" + file;

    public static string DirectoryOf(string path)
    {
        var i = path.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    public static string BaseName(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }
}

/// <summary>
/// The pure part of compose planning, the way <c>docker compose</c> v2 does it: project name precedence, <c>.env</c>
/// parsing, variable interpolation, file merging and the impact on existing containers. Anything it cannot work out
/// for certain throws <see cref="ComposePlanException"/>.
/// </summary>
public static class ComposeModel
{
    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";
    public const string OneOffLabel = "com.docker.compose.oneoff";

    /// <summary>The default compose file names, in the order compose looks for them.</summary>
    public static readonly IReadOnlyList<string> DefaultFiles =
        ["compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml"];

    /// <summary>The default override file names, in the order compose looks for them.</summary>
    public static readonly IReadOnlyList<string> OverrideFiles =
        ["compose.override.yml", "compose.override.yaml", "docker-compose.override.yml", "docker-compose.override.yaml"];

    private static readonly Regex ValidProjectName = new("^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant);

    /// <summary>A directory name made into a project name the way compose does: lower case, only <c>a-z0-9_-</c>,
    /// no leading <c>_</c> or <c>-</c>.</summary>
    public static string NormalizeProjectName(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.ToLowerInvariant())
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
                sb.Append(ch);
        return sb.ToString().TrimStart('_', '-');
    }

    /// <summary>
    /// The project name compose would use: <c>-p</c>, else <c>COMPOSE_PROJECT_NAME</c> from the <c>.env</c>, else the
    /// top-level <c>name:</c> of the merged files, else the project directory's name. An explicit, configured or
    /// declared name must already be valid (compose refuses it otherwise); the directory name is normalised.
    /// </summary>
    public static string EffectiveProjectName(string? explicitName, IReadOnlyDictionary<string, string?> env,
        string? declaredName, string projectDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitName)) return Valid(explicitName, "the explicit project name");
        if (env.TryGetValue("COMPOSE_PROJECT_NAME", out var fromEnv) && !string.IsNullOrEmpty(fromEnv))
            return Valid(fromEnv, "COMPOSE_PROJECT_NAME");
        if (!string.IsNullOrWhiteSpace(declaredName)) return Valid(declaredName, "the top-level name");
        var normalized = NormalizeProjectName(ComposePaths.BaseName(projectDirectory));
        return normalized.Length > 0
            ? normalized
            : throw new ComposePlanException($"No project name can be derived from '{projectDirectory}'.");
    }

    private static string Valid(string name, string source) =>
        ValidProjectName.IsMatch(name)
            ? name
            : throw new ComposePlanException($"Invalid project name '{name}' from {source}.");

    /// <summary>Parses a <c>.env</c> file: <c>KEY=VALUE</c> lines, optional <c>export</c>, quotes, comments, and
    /// references to keys defined above. A value that refers to an unknown variable is <c>null</c> (unknown).</summary>
    public static Dictionary<string, string?> ParseEnv(string? content)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(content)) return env;
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value.IndexOf('\'', 1) is var end and > 0)
            {
                env[key] = value[1..end];
                continue;
            }
            if (value.Length >= 2 && value[0] == '"' && value.IndexOf('"', 1) is var close and > 0)
                value = value[1..close].Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0)
                value = value[..hash].TrimEnd();
            env[key] = TryInterpolate(value, env, out var result) ? result : null;
        }
        return env;
    }

    /// <summary>Interpolates <c>$VAR</c>, <c>${VAR}</c>, <c>${VAR:-d}</c>, <c>${VAR-d}</c>, <c>${VAR:+a}</c>,
    /// <c>${VAR+a}</c>, <c>${VAR:?e}</c>, <c>${VAR?e}</c> and <c>$$</c>. A variable that is not defined (or not known)
    /// and has no default throws: Whiskers cannot see the host's environment, so it cannot tell the value.</summary>
    public static string Interpolate(string value, IReadOnlyDictionary<string, string?> env, string what) =>
        TryInterpolate(value, env, out var result)
            ? result
            : throw new ComposePlanException($"{what} '{value}' uses a variable that is not set in the .env file.");

    private static bool TryInterpolate(string value, IReadOnlyDictionary<string, string?> env, out string result)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '$' || i + 1 >= value.Length) { sb.Append(value[i]); continue; }
            var next = value[i + 1];
            if (next == '$') { sb.Append('$'); i++; continue; }
            if (next == '{')
            {
                var close = value.IndexOf('}', i + 2);
                if (close < 0) { result = ""; return false; }
                var expr = value[(i + 2)..close];
                if (!TryExpand(expr, env, out var expanded)) { result = ""; return false; }
                sb.Append(expanded);
                i = close;
                continue;
            }
            if (char.IsLetter(next) || next == '_')
            {
                var j = i + 1;
                while (j < value.Length && (char.IsLetterOrDigit(value[j]) || value[j] == '_')) j++;
                if (!env.TryGetValue(value[(i + 1)..j], out var v) || v is null) { result = ""; return false; }
                sb.Append(v);
                i = j - 1;
                continue;
            }
            sb.Append('$');
        }
        result = sb.ToString();
        return true;
    }

    private static bool TryExpand(string expr, IReadOnlyDictionary<string, string?> env, out string result)
    {
        var j = 0;
        while (j < expr.Length && (char.IsLetterOrDigit(expr[j]) || expr[j] == '_')) j++;
        var name = expr[..j];
        var op = expr[j..];
        var known = env.TryGetValue(name, out var v);
        // A key whose own value could not be worked out is neither set nor unset: nothing can be said about it.
        if (known && v is null) { result = ""; return false; }
        var set = known;
        var nonEmpty = known && v!.Length > 0;
        string Arg(int skip) => op[skip..];
        if (op.Length == 0) { result = v ?? ""; return known; }
        if (op.StartsWith(":-", StringComparison.Ordinal)) return Sub(nonEmpty ? v! : Arg(2), env, out result);
        if (op.StartsWith('-')) return Sub(set ? v! : Arg(1), env, out result);
        if (op.StartsWith(":+", StringComparison.Ordinal)) return Sub(nonEmpty ? Arg(2) : "", env, out result);
        if (op.StartsWith('+')) return Sub(set ? Arg(1) : "", env, out result);
        if (op.StartsWith(":?", StringComparison.Ordinal)) { result = v ?? ""; return nonEmpty; }
        if (op.StartsWith('?')) { result = v ?? ""; return set; }
        result = "";
        return false;
    }

    private static bool Sub(string value, IReadOnlyDictionary<string, string?> env, out string result) =>
        TryInterpolate(value, env, out result);

    /// <summary>The parts of compose files a plan needs: the top-level name and each service's container name, merged
    /// over the files in order (a later file overrides). <c>include</c> and <c>extends</c> from another file are not
    /// followed and therefore refused.</summary>
    public static (string? Name, IReadOnlyList<ComposeServicePlan> Services) Merge(
        IReadOnlyList<(string Path, string Content)> files, IReadOnlyDictionary<string, string?> env)
    {
        string? name = null;
        var services = new Dictionary<string, string?>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (path, content) in files)
        {
            var root = Root(path, content);
            if (root is null) continue;
            if (root.Children.ContainsKey(new YamlScalarNode("include")))
                throw new ComposePlanException($"{path}: 'include' is not supported in a planned deployment.");
            if (Scalar(root, "name") is { } declared)
                name = Interpolate(declared, env, "The top-level name");
            if (!root.Children.TryGetValue(new YamlScalarNode("services"), out var node)) continue;
            if (node is not YamlMappingNode serviceMap)
                throw new ComposePlanException($"{path}: 'services' is not a mapping.");
            foreach (var (key, value) in serviceMap.Children)
            {
                var service = (key as YamlScalarNode)?.Value
                              ?? throw new ComposePlanException($"{path}: a service has no name.");
                var definition = value as YamlMappingNode;
                if (definition is not null && definition.Children.TryGetValue(new YamlScalarNode("extends"), out var ext)
                    && ext is YamlMappingNode extMap && Scalar(extMap, "file") is not null)
                    throw new ComposePlanException($"{path}: service '{service}' extends another file, which is not supported.");
                var containerName = definition is null ? null : ContainerName(definition, serviceMap);
                if (!services.ContainsKey(service)) order.Add(service);
                if (containerName is not null)
                    services[service] = Interpolate(containerName, env, $"The container_name of '{service}'");
                else
                    services.TryAdd(service, null);
            }
        }
        if (order.Count == 0)
            throw new ComposePlanException("The compose files define no services.");
        return (name, order.Select(s => new ComposeServicePlan(s, services[s])).ToList());
    }

    private static YamlMappingNode? Root(string path, string content)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(content));
            if (stream.Documents.Count == 0) return null;
            return stream.Documents[0].RootNode as YamlMappingNode
                   ?? throw new ComposePlanException($"{path} is not a compose mapping.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ComposePlanException($"{path} is not valid YAML: {ex.Message}");
        }
    }

    /// <summary>A service's <c>container_name</c>: its own, else one from a <c>&lt;&lt;</c> merge key or an
    /// <c>extends</c> of a service in the same file.</summary>
    private static string? ContainerName(YamlMappingNode definition, YamlMappingNode services, int depth = 0)
    {
        if (depth > 8) throw new ComposePlanException("Service 'extends' chain is too deep.");
        if (Scalar(definition, "container_name") is { } own) return own;
        if (definition.Children.TryGetValue(new YamlScalarNode("<<"), out var merge))
        {
            var sources = merge is YamlSequenceNode seq ? seq.Children : [merge];
            foreach (var source in sources.OfType<YamlMappingNode>())
                if (Scalar(source, "container_name") is { } merged) return merged;
        }
        if (definition.Children.TryGetValue(new YamlScalarNode("extends"), out var ext))
        {
            var baseName = ext is YamlScalarNode s ? s.Value : ext is YamlMappingNode m ? Scalar(m, "service") : null;
            if (baseName is not null && services.Children.TryGetValue(new YamlScalarNode(baseName), out var b)
                && b is YamlMappingNode baseDef)
                return ContainerName(baseDef, services, depth + 1);
        }
        return null;
    }

    private static string? Scalar(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s
            && !string.IsNullOrEmpty(s.Value) ? s.Value : null;

    /// <summary>
    /// The existing containers a deployment of <paramref name="services"/> as project <paramref name="project"/>
    /// touches: the project's containers of a defined service (recreated), with <paramref name="removeOrphans"/> the
    /// rest of the project (removed), and containers of other projects or none whose name the deployment wants.
    /// </summary>
    public static IReadOnlyList<ComposeImpactEntry> Impact(string project, IReadOnlyList<ComposeServicePlan> services,
        bool removeOrphans, IEnumerable<ContainerInfo> existing)
    {
        var defined = services.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var names = services.Where(s => s.ContainerName is not null).Select(s => s.ContainerName!)
            .ToHashSet(StringComparer.Ordinal);
        var impact = new List<ComposeImpactEntry>();
        foreach (var c in existing.DistinctBy(c => c.Id))
        {
            if (Label(c, ProjectLabel) == project)
            {
                var service = Label(c, ServiceLabel);
                var oneOff = string.Equals(Label(c, OneOffLabel), "True", StringComparison.OrdinalIgnoreCase);
                if (!oneOff && service is not null && defined.Contains(service))
                    impact.Add(new(c, ComposeImpactKind.Recreate));
                else if (removeOrphans)
                    impact.Add(new(c, ComposeImpactKind.RemoveOrphan));
            }
            else if (names.Contains(c.Name) || services.Any(s => IsDefaultName(c.Name, project, s.Name)))
                impact.Add(new(c, ComposeImpactKind.NameCollision));
        }
        return impact;
    }

    private static bool IsDefaultName(string name, string project, string service)
    {
        var prefix = $"{project}-{service}-";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length
               && name[prefix.Length..].All(char.IsAsciiDigit);
    }

    private static string? Label(ContainerInfo c, string key) => c.Labels.TryGetValue(key, out var v) ? v : null;
}
