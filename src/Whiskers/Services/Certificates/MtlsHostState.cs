using System.Text;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Whiskers.Services.Certificates;

/// <summary>
/// What <see cref="MtlsAllowListCommands.ReadHostState"/> reported about a host's proxy, checked against the preflight
/// rules of contract 0007 (amendments 2 and 3). <see cref="Problem"/> names the first reason the host may not be changed;
/// a host with a problem is refused before anything on it changes.
///
/// <para>The rules: ghostunnel and socket-proxy are one compose project from exactly one file; ghostunnel's running
/// arguments are the approved ones apart from <c>--allow-cn</c>; the running allow-list names only the shared client and
/// this host's own names; and in the file, both services equal the running containers — the image digest is among the
/// running image's repo digests, the declared environment is a subset of the running one, the port bindings and mounts
/// are equal, and ghostunnel's command equals its running arguments apart from <c>--allow-cn</c>. The <c>--allow-cn</c>
/// lines of the file are exactly ghostunnel's command items, so editing them touches nothing else.</para>
/// </summary>
public sealed record MtlsHostState(
    IReadOnlyList<string> AllowedCommonNames,
    MtlsAllowListCommands.ComposeTarget? Compose,
    IReadOnlyList<string> ForeignGuardUnits,
    string? Problem)
{
    /// <summary>The <c>--allow-cn</c> items of ghostunnel's command in the compose file (a guard that restored the file
    /// shows here before the container is recreated).</summary>
    public IReadOnlyList<string> FileAllowedCommonNames { get; init; } = [];

    /// <summary>Parses and checks the read script's output. <paramref name="permittedName"/> says which client names
    /// may appear in the running allow-list (the shared one and this host's own); <paramref name="ownRunSuffixes"/> are
    /// the guard runs of the calling rollout, whose units do not block it.</summary>
    public static MtlsHostState Parse(string output, Func<string, bool> permittedName, IReadOnlyCollection<string>? ownRunSuffixes = null)
    {
        var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);

        var units = (values.GetValueOrDefault("units") ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(u => ownRunSuffixes is null || !ownRunSuffixes.Any(s => u.Contains(s, StringComparison.Ordinal)))
            .ToList();

        try
        {
            var gu = Inspect(Decode(values.GetValueOrDefault("ghostunnel")), "ghostunnel");
            var sp = Inspect(Decode(values.GetValueOrDefault("socketproxy")), "socket-proxy");

            var args = Strings(gu, "Args");
            var allow = args.Where(IsAllow).Select(a => a[MtlsAllowListCommands.AllowCnFlag.Length..]).ToList();
            var others = args.Where(a => !IsAllow(a)).ToList();
            if (!others.SequenceEqual(MtlsAllowListCommands.ApprovedArgsWithoutAllowCn, StringComparer.Ordinal))
                return Fail(allow, units, "ghostunnel runs with arguments other than the approved ones: " + string.Join(" ", others));
            if (allow.Count == 0 || allow.Any(n => !MtlsClientIdentity.IsValidCommonName(n)) || allow.Distinct().Count() != allow.Count)
                return Fail(allow, units, "ghostunnel's --allow-cn values are not a plain list of client names.");
            if (allow.FirstOrDefault(n => !permittedName(n)) is { } foreign)
                return Fail(allow, units, $"ghostunnel admits '{foreign}', which is neither the shared client nor this host's own.");

            var labels = Labels(gu);
            var spLabels = Labels(sp);
            var files = labels.GetValueOrDefault("com.docker.compose.project.config_files") ?? "";
            if (files.Length == 0 || files.Contains(','))
                return Fail(allow, units, $"ghostunnel is not managed by exactly one compose file (label: '{files}').");
            var target = new MtlsAllowListCommands.ComposeTarget(
                labels.GetValueOrDefault("com.docker.compose.project") ?? "",
                labels.GetValueOrDefault("com.docker.compose.project.working_dir") ?? "",
                files);
            try { target = target.Checked(); }
            catch (ArgumentException) { return Fail(allow, units, $"the compose project, directory or file of ghostunnel is not one Whiskers can name ({target.Project}, {target.WorkingDir}, {target.File})."); }
            if (labels.GetValueOrDefault("com.docker.compose.service") != "ghostunnel"
                || spLabels.GetValueOrDefault("com.docker.compose.service") != "socket-proxy"
                || spLabels.GetValueOrDefault("com.docker.compose.project") != target.Project)
                return Fail(allow, units, "ghostunnel and socket-proxy are not the services 'ghostunnel' and 'socket-proxy' of one compose project.");

            var composeText = Decode(values.GetValueOrDefault("compose"));
            if (string.IsNullOrEmpty(composeText))
                return Fail(allow, units, $"the compose file {target.File} could not be read.");
            var services = Services(composeText);
            if (!services.TryGetValue("ghostunnel", out var guFile) || !services.TryGetValue("socket-proxy", out var spFile))
                return Fail(allow, units, $"{target.File} does not declare both 'ghostunnel' and 'socket-proxy'.");

            if (Compare("ghostunnel", guFile, gu, JsonStrings(Decode(values.GetValueOrDefault("ghostunneldigests"))), compareCommand: true) is { } guDiff)
                return Fail(allow, units, $"{target.File} and the running container differ: {guDiff}");
            if (Compare("socket-proxy", spFile, sp, JsonStrings(Decode(values.GetValueOrDefault("socketproxydigests"))), compareCommand: false) is { } spDiff)
                return Fail(allow, units, $"{target.File} and the running container differ: {spDiff}");

            var fileAllow = Sequence(guFile, "command").Where(IsAllow).Select(a => a[MtlsAllowListCommands.AllowCnFlag.Length..]).ToList();
            var fileAllowLines = composeText.Split('\n').Count(l => l.Contains("allow-cn", StringComparison.Ordinal));
            if (fileAllow.Count == 0 || fileAllow.Count != fileAllowLines)
                return Fail(allow, units, $"{target.File} mentions --allow-cn outside ghostunnel's command items.");

            return new MtlsHostState(allow, target, units, null) { FileAllowedCommonNames = fileAllow };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or YamlDotNet.Core.YamlException or InvalidOperationException
                                       or KeyNotFoundException or ArgumentException)
        {
            return Fail([], units, "the proxy's state could not be read: " + ex.Message);
        }
    }

    private static MtlsHostState Fail(IReadOnlyList<string> allow, IReadOnlyList<string> units, string problem) =>
        new(allow, null, units, problem);

    private static bool IsAllow(string a) => a.StartsWith(MtlsAllowListCommands.AllowCnFlag, StringComparison.Ordinal);

    private static string Decode(string? b64) =>
        string.IsNullOrEmpty(b64) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(b64));

    private static JsonElement Inspect(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        var array = doc.RootElement;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 1)
            throw new InvalidOperationException($"docker inspect {name} did not return one container.");
        return array[0].Clone();
    }

    private static List<string> Strings(JsonElement e, string property) =>
        e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];

    private static List<string> JsonStrings(string json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static Dictionary<string, string> Labels(JsonElement container) =>
        container.GetProperty("Config").TryGetProperty("Labels", out var l) && l.ValueKind == JsonValueKind.Object
            ? l.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    private static Dictionary<string, YamlMappingNode> Services(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var services = (YamlMappingNode)root.Children[new YamlScalarNode("services")];
        return services.Children.ToDictionary(c => ((YamlScalarNode)c.Key).Value!, c => (YamlMappingNode)c.Value, StringComparer.Ordinal);
    }

    private static List<string> Sequence(YamlMappingNode service, string key) =>
        service.Children.TryGetValue(new YamlScalarNode(key), out var node)
            ? node is YamlSequenceNode seq
                ? seq.Children.Select(c => ((YamlScalarNode)c).Value ?? "").ToList()
                : throw new InvalidOperationException($"'{key}' is not a list.")
            : [];

    private static string? Scalar(YamlMappingNode service, string key) =>
        service.Children.TryGetValue(new YamlScalarNode(key), out var node) ? (node as YamlScalarNode)?.Value : null;

    /// <summary>Returns what differs between a service in the file and its running container, or null.</summary>
    internal static string? Compare(string name, YamlMappingNode file, JsonElement running, IReadOnlyList<string> repoDigests, bool compareCommand)
    {
        // Image: a pinned digest must be among the running image's repo digests. A file without a digest (older hosts)
        // must name exactly the reference the container was created from (amendment 4).
        var image = Scalar(file, "image") ?? "";
        var at = image.IndexOf("@sha256:", StringComparison.Ordinal);
        if (at >= 0)
        {
            var digest = image[(at + 1)..];
            if (!repoDigests.Any(d => d.EndsWith("@" + digest, StringComparison.Ordinal)))
                return $"{name} does not run the image {image}.";
        }
        else if (image.Length == 0 || running.GetProperty("Config").GetProperty("Image").GetString() != image)
        {
            return $"{name} was not created from the image reference '{image}' the file names.";
        }

        // Environment: what the file declares is set in the container (the image adds its own, e.g. PATH).
        var runningEnv = Strings(running.GetProperty("Config"), "Env").ToHashSet(StringComparer.Ordinal);
        foreach (var entry in Environment(file))
            if (!runningEnv.Contains(entry)) return $"{name}'s environment lacks {entry.Split('=')[0]} as the file sets it.";

        // Port bindings and mounts: equal sets.
        var filePorts = Sequence(file, "ports").Select(NormalisePort).ToHashSet(StringComparer.Ordinal);
        var runningPorts = RunningPorts(running);
        if (!filePorts.SetEquals(runningPorts)) return $"{name}'s port bindings differ.";
        var fileMounts = Sequence(file, "volumes").Select(NormaliseVolume).ToHashSet(StringComparer.Ordinal);
        var runningMounts = RunningMounts(running);
        if (!fileMounts.SetEquals(runningMounts)) return $"{name}'s mounts differ.";

        if (compareCommand)
        {
            var fileCommand = Sequence(file, "command").Where(a => !IsAllow(a)).ToList();
            var runningArgs = Strings(running, "Args").Where(a => !IsAllow(a)).ToList();
            if (!fileCommand.SequenceEqual(runningArgs, StringComparer.Ordinal)) return $"{name}'s command differs.";
        }
        return null;
    }

    private static IEnumerable<string> Environment(YamlMappingNode file)
    {
        if (!file.Children.TryGetValue(new YamlScalarNode("environment"), out var node)) return [];
        return node switch
        {
            YamlMappingNode map => map.Children.Select(c => $"{((YamlScalarNode)c.Key).Value}={((YamlScalarNode)c.Value).Value}"),
            YamlSequenceNode seq => seq.Children.Select(c => ((YamlScalarNode)c).Value ?? ""),
            _ => throw new InvalidOperationException("'environment' is neither a map nor a list."),
        };
    }

    /// <summary>"[ip:]host:container[/proto]" → "ip:host->container/proto".</summary>
    internal static string NormalisePort(string spec)
    {
        var proto = "tcp";
        var slash = spec.LastIndexOf('/');
        if (slash >= 0) { proto = spec[(slash + 1)..]; spec = spec[..slash]; }
        var parts = spec.Split(':');
        return parts.Length switch
        {
            3 => $"{parts[0]}:{parts[1]}->{parts[2]}/{proto}",
            2 => $":{parts[0]}->{parts[1]}/{proto}",
            _ => throw new InvalidOperationException($"The port '{spec}' is not in the short form Whiskers compares."),
        };
    }

    private static HashSet<string> RunningPorts(JsonElement running)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!running.GetProperty("HostConfig").TryGetProperty("PortBindings", out var pb) || pb.ValueKind != JsonValueKind.Object) return set;
        foreach (var port in pb.EnumerateObject())
        {
            if (port.Value.ValueKind != JsonValueKind.Array) continue;
            var containerPort = port.Name.Split('/')[0];
            var proto = port.Name.Contains('/') ? port.Name.Split('/')[1] : "tcp";
            foreach (var b in port.Value.EnumerateArray())
                set.Add($"{b.GetProperty("HostIp").GetString()}:{b.GetProperty("HostPort").GetString()}->{containerPort}/{proto}");
        }
        return set;
    }

    /// <summary>"src:dst[:mode]" → "src:dst:ro|rw".</summary>
    internal static string NormaliseVolume(string spec)
    {
        var parts = spec.Split(':');
        if (parts.Length is < 2 or > 3) throw new InvalidOperationException($"The volume '{spec}' is not in the short form Whiskers compares.");
        var ro = parts.Length == 3 && parts[2].Split(',').Contains("ro");
        return $"{parts[0]}:{parts[1]}:{(ro ? "ro" : "rw")}";
    }

    private static HashSet<string> RunningMounts(JsonElement running)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!running.TryGetProperty("Mounts", out var mounts) || mounts.ValueKind != JsonValueKind.Array) return set;
        foreach (var m in mounts.EnumerateArray())
            set.Add($"{m.GetProperty("Source").GetString()}:{m.GetProperty("Destination").GetString()}:{(m.GetProperty("RW").GetBoolean() ? "rw" : "ro")}");
        return set;
    }
}
