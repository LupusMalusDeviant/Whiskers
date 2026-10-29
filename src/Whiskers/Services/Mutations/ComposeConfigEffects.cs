using System.Text;
using System.Text.Json;
using Whiskers.Utils;

namespace Whiskers.Services.Mutations;

/// <summary>
/// What a deployment mounts, as <c>docker compose config --format json</c> resolves it on the server (R1-16): the
/// files merged, YAML anchors and <c>extends</c> followed, variables interpolated from the <c>.env</c>, relative paths
/// made absolute — compose's own model, not a copy of it. Every source is a <see cref="ComposeMount"/> per service:
/// bind sources, a volume's real name and the host device a local volume binds, the files of file-backed secrets and
/// configs, and the containers (<see cref="ComposeMount.Container"/>) or services <c>volumes_from</c> takes over.
/// Anything that cannot be read throws <see cref="ComposePlanException"/> (fail closed).
/// </summary>
public static class ComposeConfigEffects
{
    /// <summary>
    /// A host command that prints the deployment's resolved model: the planned files and <c>.env</c> that are not yet
    /// on the server go into a temporary directory (removed again), the rest is read where it is.
    /// </summary>
    public static string Command(ComposePlan plan)
    {
        var steps = new List<string> { "d=$(mktemp -d)", "trap 'rm -rf \"$d\"' EXIT" };
        var files = new List<string>();
        for (var i = 0; i < plan.Files.Count; i++)
        {
            var planned = plan.Writes.FirstOrDefault(w => w.Path == plan.Files[i]);
            if (planned?.Content is { } content)
            {
                steps.Add(Write(content, $"\"$d/{i}.yml\""));
                files.Add($"\"$d/{i}.yml\"");
            }
            else
                files.Add(ShellUtils.Quote(plan.Files[i]));
        }
        string? envFile = null;
        if (plan.EnvFileContent is not null)
        {
            steps.Add(Write(plan.EnvFileContent, "\"$d/.env\""));
            envFile = "\"$d/.env\"";
        }
        else if (plan.HasEnvFile)
            envFile = ShellUtils.Quote(plan.EnvFilePath);

        var compose = new StringBuilder("env -u COMPOSE_REMOVE_ORPHANS -u COMPOSE_IGNORE_ORPHANS docker compose -p ")
            .Append(ShellUtils.Quote(plan.ProjectName))
            .Append(" --project-directory ").Append(ShellUtils.Quote(plan.ProjectDirectory));
        foreach (var file in files) compose.Append(" -f ").Append(file);
        if (envFile is not null) compose.Append(" --env-file ").Append(envFile);
        steps.Add(compose.Append(" config --format json").ToString());
        return string.Join(" && ", steps);
    }

    private static string Write(string content, string target) =>
        $"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(content))}' | base64 -d > {target}";

    /// <summary>The mounts of each service in compose's resolved model. Every value counts as possibly coming from
    /// an <c>.env</c> (<see cref="ComposeMount.FromVariable"/>) — compose no longer says which did.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ComposeMount>> Parse(string json, string projectName)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ComposePlanException("docker compose did not return a readable model of the deployment.");
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("services", out var services)
            || services.ValueKind != JsonValueKind.Object)
            throw new ComposePlanException("docker compose did not return the deployment's services.");

        var volumes = Section(root, "volumes");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in (string[])["secret", "config"])
            foreach (var (name, def) in Section(root, kind + "s"))
                if (String(def, "file") is { } file)
                    files[$"{kind}:{name}"] = file;

        var direct = new Dictionary<string, List<ComposeMount>>(StringComparer.Ordinal);
        foreach (var service in services.EnumerateObject())
            direct[service.Name] = MountsOf(service.Value, volumes, files, projectName).ToList();

        // volumes_from another service: that service's mounts, through any chain.
        var resolved = new Dictionary<string, IReadOnlyList<ComposeMount>>(StringComparer.Ordinal);
        foreach (var name in direct.Keys)
            resolved[name] = Resolve(name, 0).Distinct().ToList();
        return resolved;

        IEnumerable<ComposeMount> Resolve(string service, int depth)
        {
            if (depth > 8) throw new ComposePlanException("A volumes_from chain is too deep.");
            foreach (var mount in direct[service])
                if (mount.Reference == "service")
                {
                    if (!direct.ContainsKey(mount.Source))
                        throw new ComposePlanException("A service takes volumes from a service that is not defined.");
                    foreach (var inherited in Resolve(mount.Source, depth + 1)) yield return inherited;
                }
                else
                    yield return mount;
        }
    }

    private static IEnumerable<ComposeMount> MountsOf(JsonElement service,
        IReadOnlyDictionary<string, JsonElement> volumes, IReadOnlyDictionary<string, string> files, string projectName)
    {
        if (service.TryGetProperty("volumes", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var volume in list.EnumerateArray())
            {
                if (String(volume, "source") is not { Length: > 0 } source) continue;   // an anonymous volume
                switch (String(volume, "type"))
                {
                    case "bind":
                        yield return Mount(source);
                        break;
                    case "volume":
                        // A volume of the file: its real name and the host device a local volume binds; else a
                        // volume created outside the project, by its name.
                        if (volumes.TryGetValue(source, out var def))
                        {
                            yield return Mount(String(def, "name") ?? $"{projectName}_{source}");
                            if (def.TryGetProperty("driver_opts", out var opts) && String(opts, "device") is { } device
                                && device.StartsWith('/'))
                                yield return Mount(device);
                        }
                        else
                            yield return Mount(source);
                        break;
                    case "tmpfs" or "npipe" or "image":
                        break;
                    default:
                        throw new ComposePlanException("A service mounts something whose kind is unknown.");
                }
            }
        if (service.TryGetProperty("volumes_from", out var from) && from.ValueKind == JsonValueKind.Array)
            foreach (var entry in from.EnumerateArray())
            {
                var reference = entry.GetString() ?? "";
                var parts = reference.Split(':');
                if (parts[0] == "container" && parts.Length > 1)
                    yield return Mount(parts[1]) with { Container = true };
                else
                    yield return Mount(parts[0] == "service" && parts.Length > 1 ? parts[1] : parts[0]) with { Reference = "service" };
            }
        foreach (var kind in (string[])["secret", "config"])
            if (service.TryGetProperty(kind + "s", out var used) && used.ValueKind == JsonValueKind.Array)
                foreach (var entry in used.EnumerateArray())
                    if ((entry.ValueKind == JsonValueKind.String ? entry.GetString() : String(entry, "source")) is { } name
                        && files.TryGetValue($"{kind}:{name}", out var file))
                        yield return Mount(file);
    }

    private static ComposeMount Mount(string source) =>
        new(source.StartsWith('/') ? Whiskers.Services.Guardrails.ProtectedResourceMatcher.NormalizePath(source) : source,
            ProjectScoped: false) { FromVariable = true };

    private static Dictionary<string, JsonElement> Section(JsonElement root, string name) =>
        root.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.Object
            ? section.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
