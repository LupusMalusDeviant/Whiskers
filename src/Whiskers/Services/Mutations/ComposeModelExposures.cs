using YamlDotNet.RepresentationModel;

namespace Whiskers.Services.Mutations;

/// <summary>A <c>network_mode</c>, <c>pid</c> or <c>ipc</c> of the form <c>service:x</c> or <c>container:x</c> the
/// plan model read (contract 0006): whether its reference came from a variable.</summary>
public sealed record ComposeSharedNamespace(string Service, string Field, string Reference, bool FromVariable);

/// <summary>Contract 0006: what the plan model reads about exposures from the compose files themselves.</summary>
public static partial class ComposeModel
{
    /// <summary>
    /// The exposures the compose files write, per service, interpolated from the <c>.env</c> (contract 0006): every row of
    /// the detection table, in the short and the long forms, through YAML merge keys and <c>extends</c> within a file. The
    /// model knows which value came from a variable — <c>docker compose config</c> no longer does — so an entry read here is
    /// shown or withheld as written. A value the plan cannot interpolate refuses the plan: a port, a network or a
    /// privilege field, any label key, and a label value under a key with an exposure prefix (a value under any other key
    /// is ignored). A relative <c>label_file</c> is refused (Amendments 10 and 11), decided after interpolation.
    /// </summary>
    public static (IReadOnlyDictionary<string, IReadOnlyList<ComposeExposure>> Exposures,
        IReadOnlyList<ComposeSharedNamespace> Shared) ExposuresOf(IReadOnlyList<(string Path, string Content)> files,
        IReadOnlyDictionary<string, string?> env, IReadOnlyList<string> prefixes)
    {
        var exposures = new Dictionary<string, List<ComposeExposure>>(StringComparer.Ordinal);
        var shared = new List<ComposeSharedNamespace>();
        var serviceNetworks = new List<(string Service, string Key)>();
        var topNetworks = new Dictionary<string, (string Name, bool External)>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            var root = Root(path, content);
            if (root is null) continue;
            if (root.Children.TryGetValue(new YamlScalarNode("networks"), out var nets) && nets is YamlMappingNode netMap)
                foreach (var (key, value) in netMap.Children)
                    if ((key as YamlScalarNode)?.Value is { } netKey)
                        topNetworks[netKey] = TopNetwork(netKey, value as YamlMappingNode, env);
            if (!root.Children.TryGetValue(new YamlScalarNode("services"), out var node) || node is not YamlMappingNode serviceMap)
                continue;
            foreach (var (key, value) in serviceMap.Children)
            {
                if ((key as YamlScalarNode)?.Value is not { } service || value is not YamlMappingNode definition) continue;
                if (!exposures.TryGetValue(service, out var list)) exposures[service] = list = [];
                foreach (var source in Sources(definition, serviceMap, 0))
                    Detect(service, source, env, prefixes, list, shared, serviceNetworks);
            }
        }
        foreach (var (service, key) in serviceNetworks)
            if (topNetworks.TryGetValue(key, out var top) && top.External && top.Name == "host")
                exposures[service].Add(new ComposeExposure(service, ExposureKind.HostNetwork, "networks: host")
                {
                    Key = "networks", Value = "host",
                });
        return (exposures.ToDictionary(e => e.Key, e => (IReadOnlyList<ComposeExposure>)Distinct(e.Value), StringComparer.Ordinal),
            shared.Distinct().ToList());
    }

    /// <summary>One entry per identity; an entry withheld in one place is withheld everywhere.</summary>
    public static IReadOnlyList<ComposeExposure> Distinct(IEnumerable<ComposeExposure> exposures) =>
        exposures.GroupBy(e => e.Identity, StringComparer.Ordinal)
            .Select(g => g.Aggregate((a, b) => a with
            {
                KeyWithheld = a.KeyWithheld || b.KeyWithheld,
                ValueWithheld = a.ValueWithheld || b.ValueWithheld,
                FromImage = a.FromImage && b.FromImage,
                Detail = a.KeyWithheld || b.KeyWithheld ? WithheldDetail(a.Kind) : a.Detail,
            }))
            .ToList();

    private static string WithheldDetail(ExposureKind kind) => $"{ExposureClasses.Noun(kind)} (withheld)";

    private static (string Name, bool External) TopNetwork(string key, YamlMappingNode? definition,
        IReadOnlyDictionary<string, string?> env)
    {
        if (definition is null) return (key, false);
        var external = definition.Children.TryGetValue(new YamlScalarNode("external"), out var ext)
                       && (ext is YamlMappingNode || (ext is YamlScalarNode s && Truthy(Interpolate(s.Value ?? "", env, $"The network '{key}'"))));
        var name = Scalar(definition, "name") is { } n ? Interpolate(n, env, $"The name of network '{key}'")
            : ext is YamlMappingNode extMap && Scalar(extMap, "name") is { } en ? Interpolate(en, env, $"The name of network '{key}'")
            : key;
        return (name, external);
    }

    private static bool Truthy(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>A service definition, the mappings it merges, and the services it extends within the same file.</summary>
    private static IEnumerable<YamlMappingNode> Sources(YamlMappingNode definition, YamlMappingNode services, int depth)
    {
        if (depth > 8) throw new ComposePlanException("Service 'extends' chain is too deep.");
        foreach (var source in Merged(definition, 0)) yield return source;
        if (!definition.Children.TryGetValue(new YamlScalarNode("extends"), out var ext)) yield break;
        var baseName = ext is YamlScalarNode e ? e.Value : ext is YamlMappingNode em ? Scalar(em, "service") : null;
        if (baseName is not null && services.Children.TryGetValue(new YamlScalarNode(baseName), out var b)
            && b is YamlMappingNode baseDef)
            foreach (var inner in Sources(baseDef, services, depth + 1))
                yield return inner;
    }

    private static void Detect(string service, YamlMappingNode source, IReadOnlyDictionary<string, string?> env,
        IReadOnlyList<string> prefixes, List<ComposeExposure> found, List<ComposeSharedNamespace> shared,
        List<(string, string)> serviceNetworks)
    {
        string Value(string raw, string what) => Interpolate(raw, env, $"{what} of '{service}'");
        void Add(ExposureKind kind, string key, string value, string detail, bool fromVariable) =>
            found.Add(new ComposeExposure(service, kind, fromVariable ? WithheldDetail(kind) : detail)
            {
                Key = key, Value = value, KeyWithheld = fromVariable, ValueWithheld = fromVariable,
            });

        foreach (var (entry, raw) in Entries(source, "ports"))
        {
            var fromVariable = ComposeExposures.HasVariable(raw);
            foreach (var (key, value) in Ports(service, entry, env))
                Add(ExposureKind.PublishedPort, key, value, $"{value}->{key}", fromVariable);
        }
        if (Scalar(source, "network_mode") is { } rawMode)
        {
            var mode = Value(rawMode, "The network_mode");
            if (mode == "host") Add(ExposureKind.HostNetwork, "network_mode", "host", "network_mode: host", ComposeExposures.HasVariable(rawMode));
            else if (IsShared(mode)) shared.Add(new(service, "network_mode", mode, ComposeExposures.HasVariable(rawMode)));
        }
        if (source.Children.TryGetValue(new YamlScalarNode("networks"), out var networks))
        {
            var keys = networks switch
            {
                YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(s => s.Value),
                YamlMappingNode map => map.Children.Keys.OfType<YamlScalarNode>().Select(s => s.Value),
                _ => [],
            };
            foreach (var key in keys.OfType<string>()) serviceNetworks.Add((service, key));
        }
        if (Scalar(source, "privileged") is { } rawPrivileged && Truthy(Value(rawPrivileged, "privileged")))
            Add(ExposureKind.Privileged, "privileged", "true", "privileged: true", ComposeExposures.HasVariable(rawPrivileged));
        foreach (var (_, raw) in Entries(source, "cap_add"))
        {
            var cap = ComposeExposures.Capability(Value(raw, "A cap_add entry"));
            Add(ExposureKind.AddedCapability, cap, "", $"cap_add: {cap}", ComposeExposures.HasVariable(raw));
        }
        foreach (var field in (string[])["pid", "ipc", "uts", "userns_mode", "cgroup"])
            if (Scalar(source, field) is { } rawNs)
            {
                var mode = Value(rawNs, $"The {field}");
                if (mode == "host") Add(ExposureKind.HostNamespace, field, "host", $"{field}: host", ComposeExposures.HasVariable(rawNs));
                else if (field is "pid" or "ipc" && IsShared(mode))
                    shared.Add(new(service, field, mode, ComposeExposures.HasVariable(rawNs)));
            }
        foreach (var (entry, raw) in Entries(source, "devices"))
        {
            var (host, value) = entry is YamlMappingNode device
                ? ComposeExposures.Device(Value(Scalar(device, "source") ?? "", "A device"),
                    Scalar(device, "target") is { } t ? Value(t, "A device") : null,
                    Scalar(device, "permissions") is { } p ? Value(p, "A device") : null)
                : ComposeExposures.DeviceShort(Value(raw, "A device"));
            if (host.Length > 0) Add(ExposureKind.Device, host, value, $"device {host}", ComposeExposures.HasVariable(raw));
        }
        foreach (var (_, raw) in Entries(source, "security_opt"))
            if (ComposeExposures.SecurityOption(Value(raw, "A security_opt entry")) is { } opt)
                Add(ExposureKind.SecurityOpt, opt.Name, opt.Value, $"security_opt {opt.Name}={opt.Value}", ComposeExposures.HasVariable(raw));
        foreach (var (_, raw) in Entries(source, "device_cgroup_rules"))
        {
            var rule = Value(raw, "A device_cgroup_rules entry").Trim();
            Add(ExposureKind.DeviceCgroupRule, rule, "", $"device_cgroup_rules {rule}", ComposeExposures.HasVariable(raw));
        }
        foreach (var (rawKey, rawValue) in Labels(source))
        {
            // Any key must be known: whether it is an exposure depends on it.
            if (!TryInterpolate(rawKey, env, out var key))
                throw new ComposePlanException($"A label key of '{service}' uses a variable that is not set in the .env file.");
            if (!prefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (!TryInterpolate(rawValue ?? "", env, out var value))
                throw new ComposePlanException($"A label of '{service}' with an exposure prefix uses a variable that is not set in the .env file.");
            var keyFromVariable = ComposeExposures.HasVariable(rawKey);
            // Contract 0013, D18: a literal traefik.enable=false is no exposure (one from a variable still is).
            if (!keyFromVariable && !ComposeExposures.HasVariable(rawValue)
                && !ComposeExposures.IsRouteLabel(key, value, prefixes)) continue;
            var valueWithheld = ComposeExposures.HasVariable(rawValue) || !ComposeExposures.ShowsLabelValue(key, value);
            found.Add(new ComposeExposure(service, ExposureKind.RouteLabel, keyFromVariable ? "label (withheld)" : key)
            {
                Key = key, Value = value, KeyWithheld = keyFromVariable, ValueWithheld = valueWithheld,
            });
        }
        // Contract 0013, D16: what names or configures a proxy or tunnel must be known — an image, entrypoint or command,
        // or a tool's environment value, from a variable that is not set refuses the plan.
        foreach (var field in (string[])["image", "entrypoint", "command", "working_dir"])
            if (source.Children.TryGetValue(new YamlScalarNode(field), out var node))
                foreach (var raw in node switch
                         {
                             YamlScalarNode s => [s.Value ?? ""],
                             YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? ""),
                             _ => Enumerable.Empty<string>(),
                         })
                    Value(raw, $"The {field}");
        if (source.Children.TryGetValue(new YamlScalarNode("environment"), out var environment))
            foreach (var (envKey, envValue) in environment switch
                     {
                         YamlMappingNode map => map.Children.Select(c => ((c.Key as YamlScalarNode)?.Value ?? "", (c.Value as YamlScalarNode)?.Value)),
                         YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? "")
                             .Select(e => e.IndexOf('=') is var eq and >= 0 ? (e[..eq], (string?)e[(eq + 1)..]) : (e, null)),
                         _ => Enumerable.Empty<(string, string?)>(),
                     })
                if (envValue is not null && (envKey.StartsWith("TS_", StringComparison.Ordinal)
                                             || envKey.StartsWith("TUNNEL_", StringComparison.Ordinal)
                                             || envKey.StartsWith("TRAEFIK_", StringComparison.Ordinal)
                                             || envKey is "HOME" or "XDG_CONFIG_HOME"))
                    Value(envValue, $"The environment value {envKey}");
        // label_file: compose reads it (its labels come through `docker compose config`); a relative path is refused,
        // decided after interpolation — the file it names depends on where compose runs (Amendments 10 and 11).
        foreach (var (_, raw) in Entries(source, "label_file"))
        {
            var file = Value(raw, "A label_file");
            if (!file.StartsWith('/'))
                throw new ComposePlanException($"A label_file of '{service}' is relative, which is not supported in a planned deployment.");
        }
    }

    private static bool IsShared(string mode) =>
        mode.StartsWith("service:", StringComparison.Ordinal) || mode.StartsWith("container:", StringComparison.Ordinal);

    /// <summary>The entries of a list field (or a single scalar), each with its raw text (a long entry's text is its
    /// mapping's scalars joined, to tell whether any came from a variable).</summary>
    private static IEnumerable<(YamlNode Entry, string Raw)> Entries(YamlMappingNode source, string field)
    {
        if (!source.Children.TryGetValue(new YamlScalarNode(field), out var node)) yield break;
        var items = node switch
        {
            YamlSequenceNode seq => seq.Children,
            YamlScalarNode s => [s],
            _ => throw new ComposePlanException($"The {field} of a service cannot be read."),
        };
        foreach (var item in items)
            switch (item)
            {
                case YamlScalarNode s when !string.IsNullOrEmpty(s.Value):
                    yield return (s, s.Value);
                    break;
                case YamlMappingNode m:
                    yield return (m, string.Join(' ', m.Children.Values.OfType<YamlScalarNode>().Select(v => v.Value)));
                    break;
                case YamlScalarNode:
                    break;
                default:
                    throw new ComposePlanException($"An entry of {field} cannot be read.");
            }
    }

    /// <summary>The labels of a source as written: list form (<c>key=value</c>, <c>key</c>) or map form.</summary>
    private static IEnumerable<(string Key, string? Value)> Labels(YamlMappingNode source)
    {
        if (!source.Children.TryGetValue(new YamlScalarNode("labels"), out var node)) yield break;
        switch (node)
        {
            case YamlSequenceNode seq:
                foreach (var item in seq.Children)
                {
                    if (item is not YamlScalarNode { Value: { Length: > 0 } text })
                        throw new ComposePlanException("A label cannot be read.");
                    var eq = text.IndexOf('=');
                    yield return eq < 0 ? (text, "") : (text[..eq], text[(eq + 1)..]);
                }
                break;
            case YamlMappingNode map:
                foreach (var (k, v) in map.Children)
                {
                    if ((k as YamlScalarNode)?.Value is not { Length: > 0 } key)
                        throw new ComposePlanException("A label cannot be read.");
                    yield return (key, v switch
                    {
                        YamlScalarNode s => s.Value ?? "",
                        _ => throw new ComposePlanException("A label value cannot be read."),
                    });
                }
                break;
            default:
                throw new ComposePlanException("The labels of a service cannot be read.");
        }
    }

    /// <summary>Most ports a single entry may publish (a range); a wider one is refused.</summary>
    private const int MaxPortRange = 1024;

    /// <summary>The published ports of one <c>ports</c> entry as (key, value), loopback left out.</summary>
    private static IEnumerable<(string Key, string Value)> Ports(string service, YamlNode entry,
        IReadOnlyDictionary<string, string?> env)
    {
        string Value(string raw) => Interpolate(raw, env, $"A port of '{service}'");
        string? hostIp, hostPort, container, protocol;
        if (entry is YamlMappingNode m)
        {
            container = Scalar(m, "target") is { } t ? Value(t) : null;
            hostPort = Scalar(m, "published") is { } p ? Value(p) : null;
            hostIp = Scalar(m, "host_ip") is { } h ? Value(h) : null;
            protocol = Scalar(m, "protocol") is { } pr ? Value(pr) : null;
        }
        else
        {
            var spec = Value(((YamlScalarNode)entry).Value ?? "").Trim();
            protocol = null;
            var slash = spec.LastIndexOf('/');
            if (slash > 0)
            {
                protocol = spec[(slash + 1)..];
                spec = spec[..slash];
            }
            hostIp = null;
            if (spec.StartsWith('['))
            {
                var close = spec.IndexOf("]:", StringComparison.Ordinal);
                if (close < 0) throw new ComposePlanException($"A port of '{service}' cannot be read.");
                hostIp = spec[1..close];
                spec = spec[(close + 2)..];
            }
            var parts = spec.Split(':');
            (hostIp, hostPort, container) = parts.Length switch
            {
                1 => (hostIp, null, parts[0]),
                2 => (hostIp, parts[0], parts[1]),
                3 => (hostIp ?? parts[0], parts[1], parts[2]),
                // An unbracketed IPv6 host address: everything before the last two parts.
                _ => (string.Join(':', parts[..^2]), parts[^2], parts[^1]),
            };
        }
        if (string.IsNullOrWhiteSpace(container)) throw new ComposePlanException($"A port of '{service}' has no target.");
        if (ComposeExposures.IsLoopback(hostIp)) yield break;
        var containers = Range(service, container);
        var hosts = string.IsNullOrWhiteSpace(hostPort) ? null : Range(service, hostPort);
        for (var i = 0; i < containers.Count; i++)
        {
            var host = hosts is null ? null : hosts.Count == containers.Count ? hosts[i] : hostPort;
            yield return (ComposeExposures.PortKey(containers[i], protocol), ComposeExposures.PortValue(hostIp, host));
        }
    }

    private static IReadOnlyList<string> Range(string service, string spec)
    {
        var dash = spec.IndexOf('-');
        if (dash < 0) return [spec.Trim()];
        if (!int.TryParse(spec[..dash], out var from) || !int.TryParse(spec[(dash + 1)..], out var to) || to < from
            || to - from >= MaxPortRange)
            throw new ComposePlanException($"A port range of '{service}' cannot be planned.");
        return Enumerable.Range(from, to - from + 1).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>The existing container a <c>container:x</c> namespace names (an exact id or name, or a unique id prefix);
    /// none or several refuse the plan. The reference is never quoted (it can come from an <c>.env</c> value).</summary>
    public static Whiskers.Models.ContainerInfo FindShared(string reference, IEnumerable<Whiskers.Models.ContainerInfo> existing)
    {
        var containers = existing.DistinctBy(c => c.Id).ToList();
        var exact = containers.Where(c => c.Id == reference || c.Name == reference || c.Name == "/" + reference).ToList();
        var found = exact.Count > 0 ? exact
            : reference.Length > 0 && reference.All(char.IsAsciiHexDigitLower)
                ? containers.Where(c => c.Id.StartsWith(reference, StringComparison.Ordinal)).ToList()
                : [];
        return found.Count == 1
            ? found[0]
            : throw new ComposePlanException(
                $"A service shares a namespace with a container that {(found.Count == 0 ? "does not exist" : "is not unique")}.");
    }

    /// <summary>Interpolates like <see cref="Interpolate"/>, without throwing.</summary>
    public static bool TryInterpolateValue(string value, IReadOnlyDictionary<string, string?> env, out string result) =>
        TryInterpolate(value, env, out result);
}
