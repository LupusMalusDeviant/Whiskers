using System.Text.Json;
using System.Text.RegularExpressions;

namespace Whiskers.Services.Mutations;

/// <summary>How a planned service is reachable or powerful beyond its own network namespace (contract 0006).</summary>
public enum ExposureKind
{
    PublishedPort,
    HostNetwork,
    Privileged,
    AddedCapability,
    HostNamespace,
    RouteLabel,
    Device,
    SecurityOpt,
    DeviceCgroupRule,
}

/// <summary>
/// One way a planned service is reachable or powerful beyond its own network namespace (contract 0006). It is compared and
/// bound on <c>(service, kind, key, value)</c> (<see cref="Identity"/>); <see cref="Detail"/> is what may be shown. A part
/// that came from a variable — or a label value outside the allow-list of shapes that are not secret — is
/// <see cref="Withheld"/>: compared and bound like the others, never written anywhere.
/// </summary>
/// <param name="Detail">What may be shown: <c>0.0.0.0:8080-&gt;80/tcp</c>, <c>network_mode: host</c>,
/// <c>cap_add: NET_ADMIN</c>, <c>pid: host</c>, a label's key (never its value), or <c>label (withheld)</c>.</param>
public sealed record ComposeExposure(string Service, ExposureKind Kind, string Detail)
{
    /// <summary>The comparison key (contract 0006, Amendment 7 and 8): the label key, <c>&lt;port&gt;/&lt;proto&gt;</c>,
    /// <c>network_mode</c>, the namespace, the capability, the device's host path, the option name or the rule.</summary>
    public string Key { get; init; } = "";

    /// <summary>The comparison value (may be withheld): the label value, <c>&lt;hostIp&gt;:&lt;hostPort&gt;</c>, …</summary>
    public string Value { get; init; } = "";

    /// <summary>The key came from a variable: it is never shown, not even the key.</summary>
    public bool KeyWithheld { get; init; }

    /// <summary>The value came (wholly or partly) from a variable, or is not of a shape that is safe to show.</summary>
    public bool ValueWithheld { get; init; }

    /// <summary>Some part is withheld: the entry travels in <c>exposureWithheld</c> and the target's withheld set.</summary>
    public bool Withheld => KeyWithheld || ValueWithheld;

    /// <summary>The image a <see cref="ExposureKind.RouteLabel"/> came from (its own label, not the compose file's).</summary>
    public bool FromImage { get; init; }

    /// <summary>What it is compared and bound on: <c>&lt;service&gt;: &lt;Kind&gt; &lt;key&gt;=&lt;value&gt;</c>. Holds
    /// withheld values: never written, only compared, hashed and handed to the rules.</summary>
    public string Identity => ComposeExposures.Identity(Service, Kind, Key, Value);

    /// <summary><c>&lt;service&gt;: &lt;detail&gt;</c>, safe to show.</summary>
    public string Display => $"{Service}: {Detail}";
}

/// <summary>The exposure classes (contract 0006): what an exposure means for who can reach the service.</summary>
public static class ExposureClasses
{
    /// <summary>Reachable from the internet: a Funnel label, a Traefik router, caddy-docker-proxy, or a prefix the operator
    /// added (failing closed).</summary>
    public const string Public = "public";

    /// <summary>Powers of the host: its network, privileged mode, capabilities, namespaces, devices, unconfined profiles.</summary>
    public const string Host = "host";

    /// <summary>A published port, or a DockTail service label (tailnet only).</summary>
    public const string Published = "published";

    /// <summary>The class of an exposure under <paramref name="prefixes"/> (the deciding policy's), or <c>null</c> when it
    /// is a label none of them matches (no exposure under that policy). Label keys match case-insensitively.</summary>
    public static string? Of(ExposureKind kind, string key, IReadOnlyList<string> prefixes) => kind switch
    {
        ExposureKind.PublishedPort => Published,
        ExposureKind.RouteLabel => LabelClass(key, prefixes),
        _ => Host,
    };

    private static string? LabelClass(string key, IReadOnlyList<string> prefixes)
    {
        var matching = prefixes.Where(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0) return null;
        // docktail.service. serves on the tailnet only; every other built-in prefix is a public route, and a prefix the
        // operator added is public too (fail closed).
        return matching.All(p => string.Equals(p, "docktail.service.", StringComparison.OrdinalIgnoreCase))
            ? Published
            : Public;
    }

    /// <summary>The noun a reason uses for a kind when its detail is withheld.</summary>
    public static string Noun(ExposureKind kind) => kind switch
    {
        ExposureKind.PublishedPort => "published port",
        ExposureKind.HostNetwork => "host network",
        ExposureKind.Privileged => "privileged mode",
        ExposureKind.AddedCapability => "added capability",
        ExposureKind.HostNamespace => "host namespace",
        ExposureKind.RouteLabel => "label",
        ExposureKind.Device => "device",
        ExposureKind.SecurityOpt => "security option",
        ExposureKind.DeviceCgroupRule => "device cgroup rule",
        _ => "exposure",
    };
}

/// <summary>Helpers shared by the planner, the resolver, the rule and the re-check (contract 0006).</summary>
public static class ComposeExposures
{
    /// <summary>The comparison and binding identity of an exposure.</summary>
    public static string Identity(string service, ExposureKind kind, string key, string value) =>
        $"{service}: {kind} {key}={value}";

    /// <summary>Reads an <see cref="Identity"/> back; <c>null</c> when it is not one.</summary>
    public static (string Service, ExposureKind Kind, string Key, string Value)? Parse(string identity)
    {
        var colon = identity.IndexOf(": ", StringComparison.Ordinal);
        if (colon <= 0) return null;
        var rest = identity[(colon + 2)..];
        var space = rest.IndexOf(' ');
        if (space <= 0 || !Enum.TryParse<ExposureKind>(rest[..space], ignoreCase: false, out var kind)
            || !Enum.IsDefined(kind)) return null;
        var pair = rest[(space + 1)..];
        var eq = pair.IndexOf('=');
        return eq < 0 ? null : (identity[..colon], kind, pair[..eq], pair[(eq + 1)..]);
    }

    /// <summary>The identity a re-check compares on: the host network is one row whatever named it
    /// (<c>network_mode: host</c> or an external network called <c>host</c>).</summary>
    public static string ComparisonKey(string identity) =>
        Parse(identity) is { Kind: ExposureKind.HostNetwork } p
            ? Identity(p.Service, ExposureKind.HostNetwork, "network_mode", "host")
            : identity;

    /// <summary>What a reason or a notification may say about an identity: its service, its kind and its key only — or the
    /// kind alone when <paramref name="withheld"/>.</summary>
    public static string Describe(string identity, bool withheld)
    {
        if (Parse(identity) is not { } p) return "an exposure (withheld)";
        if (withheld) return $"{p.Service}: {ExposureClasses.Noun(p.Kind)} (withheld)";
        return p.Kind switch
        {
            ExposureKind.PublishedPort => $"{p.Service}: port {p.Value}->{p.Key}",
            ExposureKind.HostNetwork => $"{p.Service}: {p.Key}: host",
            ExposureKind.Privileged => $"{p.Service}: privileged: true",
            ExposureKind.AddedCapability => $"{p.Service}: cap_add: {p.Key}",
            ExposureKind.HostNamespace => $"{p.Service}: {p.Key}: host",
            ExposureKind.RouteLabel => $"{p.Service}: label {p.Key}",
            ExposureKind.Device => $"{p.Service}: device {p.Key}",
            ExposureKind.SecurityOpt => $"{p.Service}: security_opt {p.Key}={p.Value}",
            ExposureKind.DeviceCgroupRule => $"{p.Service}: device_cgroup_rules {p.Key}",
            _ => $"{p.Service}: {p.Kind}",
        };
    }

    /// <summary>The identities of <paramref name="exposures"/> as one line each.</summary>
    public static string Lines(IEnumerable<ComposeExposure> exposures) =>
        string.Join('\n', exposures.Select(e => e.Identity).Distinct(StringComparer.Ordinal));

    /// <summary>The classes present among <paramref name="exposures"/> under <paramref name="prefixes"/>, comma-separated.</summary>
    public static string Classes(IEnumerable<ComposeExposure> exposures, IReadOnlyList<string> prefixes) =>
        string.Join(',', exposures.Select(e => ExposureClasses.Of(e.Kind, e.Key, prefixes)).OfType<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    // ------------------------------------------------------------------------------------------- normalisation

    /// <summary>A capability as compared: upper case, without <c>CAP_</c>.</summary>
    public static string Capability(string value)
    {
        var upper = value.Trim().ToUpperInvariant();
        return upper.StartsWith("CAP_", StringComparison.Ordinal) ? upper[4..] : upper;
    }

    /// <summary>Whether a published port's host address is loopback (the exception of the detection table).</summary>
    public static bool IsLoopback(string? hostIp) =>
        hostIp?.Trim().Trim('[', ']') is "127.0.0.1" or "::1";

    /// <summary>A published port's value: the host address (empty is <c>0.0.0.0</c>) and the host port (empty or 0 is
    /// ephemeral: <c>*</c>).</summary>
    public static string PortValue(string? hostIp, string? hostPort)
    {
        var ip = hostIp?.Trim().Trim('[', ']');
        var port = hostPort?.Trim();
        return $"{(string.IsNullOrEmpty(ip) ? "0.0.0.0" : ip)}:{(string.IsNullOrEmpty(port) || port == "0" ? "*" : port)}";
    }

    /// <summary>A published port's key: <c>&lt;containerPort&gt;/&lt;proto&gt;</c> (tcp when none is given).</summary>
    public static string PortKey(string containerPort, string? protocol) =>
        $"{containerPort.Trim()}/{(string.IsNullOrWhiteSpace(protocol) ? "tcp" : protocol.Trim().ToLowerInvariant())}";

    /// <summary>A security option that is an exposure (<c>seccomp</c> / <c>apparmor</c> unconfined, <c>label=disable</c>,
    /// <c>systempaths=unconfined</c>) as its name and value, or <c>null</c> for any other option.</summary>
    public static (string Name, string Value)? SecurityOption(string option)
    {
        var text = option.Trim();
        var at = text.IndexOfAny([':', '=']);
        if (at <= 0) return null;
        var name = text[..at].Trim().ToLowerInvariant();
        var value = text[(at + 1)..].Trim();
        return (name, value.ToLowerInvariant()) switch
        {
            ("seccomp", "unconfined") or ("apparmor", "unconfined") or ("label", "disable") or ("systempaths", "unconfined")
                => (name, value.ToLowerInvariant()),
            _ => null,
        };
    }

    /// <summary>A device as compared: its host path, and its container path with permissions (<c>rwm</c> by default).</summary>
    public static (string Host, string Value) Device(string host, string? container, string? permissions)
    {
        var h = host.Trim();
        var c = string.IsNullOrWhiteSpace(container) ? h : container.Trim();
        return (h, $"{c}:{(string.IsNullOrWhiteSpace(permissions) ? "rwm" : permissions.Trim())}");
    }

    /// <summary>A device in the short form <c>host[:container[:permissions]]</c> (a CDI name is its own key).</summary>
    public static (string Host, string Value) DeviceShort(string spec)
    {
        var parts = spec.Trim().Split(':');
        return parts.Length switch
        {
            1 => Device(parts[0], null, null),
            2 => IsPermissions(parts[1]) ? Device(parts[0], null, parts[1]) : Device(parts[0], parts[1], null),
            _ => Device(parts[0], parts[1], parts[2]),
        };
    }

    private static bool IsPermissions(string value) => value.Length is > 0 and <= 3 && value.All(c => c is 'r' or 'w' or 'm');

    // ------------------------------------------------------------------------------------------- label values

    /// <summary>
    /// Whether a label value written literally in a compose file may be shown (contract 0006, Amendments 5–7): only
    /// <c>true</c>/<c>false</c>, a port number, or a Traefik <c>rule</c> whose every matcher is <c>Host</c>,
    /// <c>HostSNI</c>, <c>HostRegexp</c> or <c>Method</c>, combined only with <c>&amp;&amp;</c>, <c>||</c>, <c>!</c> and
    /// parentheses. Anything else — and every value from a variable, which the caller handles — is withheld.
    /// </summary>
    public static bool ShowsLabelValue(string key, string value)
    {
        var v = value.Trim();
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase))
            return true;
        if (v.Length is > 0 and <= 5 && v.All(char.IsAsciiDigit)) return true;
        return key.StartsWith("traefik.", StringComparison.OrdinalIgnoreCase)
               && key.EndsWith(".rule", StringComparison.OrdinalIgnoreCase)
               && IsShownTraefikRule(v);
    }

    private static readonly HashSet<string> ShownMatchers = new(StringComparer.Ordinal) { "Host", "HostSNI", "HostRegexp", "Method" };

    /// <summary>A Traefik rule made only of the shown matchers and the boolean operators; anything that does not parse
    /// is not shown.</summary>
    internal static bool IsShownTraefikRule(string rule)
    {
        var i = 0;
        var depth = 0;
        var expectOperand = true;   // after start, an operator or '(' or '!': a matcher, '(' or '!' must follow
        var any = false;
        while (true)
        {
            while (i < rule.Length && char.IsWhiteSpace(rule[i])) i++;
            if (i >= rule.Length) break;
            var c = rule[i];
            if (expectOperand)
            {
                if (c == '!') { i++; continue; }
                if (c == '(') { depth++; i++; continue; }
                if (!char.IsAsciiLetter(c)) return false;
                var start = i;
                while (i < rule.Length && char.IsAsciiLetterOrDigit(rule[i])) i++;
                if (!ShownMatchers.Contains(rule[start..i])) return false;
                while (i < rule.Length && char.IsWhiteSpace(rule[i])) i++;
                if (i >= rule.Length || rule[i] != '(') return false;
                i++;
                // One or more quoted arguments separated by commas.
                while (true)
                {
                    while (i < rule.Length && char.IsWhiteSpace(rule[i])) i++;
                    if (i >= rule.Length || rule[i] is not ('`' or '"')) return false;
                    var quote = rule[i++];
                    var close = rule.IndexOf(quote, i);
                    if (close < 0) return false;
                    i = close + 1;
                    while (i < rule.Length && char.IsWhiteSpace(rule[i])) i++;
                    if (i < rule.Length && rule[i] == ',') { i++; continue; }
                    if (i < rule.Length && rule[i] == ')') { i++; break; }
                    return false;
                }
                any = true;
                expectOperand = false;
                continue;
            }
            if (c == ')')
            {
                if (--depth < 0) return false;
                i++;
                continue;
            }
            if (i + 1 < rule.Length && ((c == '&' && rule[i + 1] == '&') || (c == '|' && rule[i + 1] == '|')))
            {
                i += 2;
                expectOperand = true;
                continue;
            }
            return false;
        }
        return any && depth == 0 && !expectOperand;
    }

    // ------------------------------------------------------------------------------------------- docker compose config

    /// <summary>
    /// The exposures of one service in compose's resolved model (<c>docker compose config --format json</c>): compose
    /// resolved merging, <c>extends</c>, <c>label_file</c> and interpolation itself. Compose no longer says which value came
    /// from a variable, so every entry is withheld here; the planner keeps an entry the plan model read itself as the model
    /// read it.
    /// </summary>
    public static IReadOnlyList<ComposeExposure> FromConfig(string service, JsonElement definition,
        IReadOnlyDictionary<string, JsonElement> topNetworks, IReadOnlyList<string> prefixes)
    {
        var found = new List<ComposeExposure>();
        void Add(ExposureKind kind, string key, string value, string detail) =>
            found.Add(new ComposeExposure(service, kind, detail) { Key = key, Value = value, KeyWithheld = true, ValueWithheld = true });

        if (Property(definition, "labels") is { ValueKind: JsonValueKind.Object } labels)
            foreach (var label in labels.EnumerateObject())
                if (prefixes.Any(p => label.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    Add(ExposureKind.RouteLabel, label.Name, label.Value.ValueKind == JsonValueKind.String ? label.Value.GetString() ?? "" : label.Value.GetRawText(), "label (withheld)");
        if (Property(definition, "ports") is { ValueKind: JsonValueKind.Array } ports)
            foreach (var port in ports.EnumerateArray())
            {
                var target = Text(port, "target");
                if (string.IsNullOrEmpty(target)) throw new ComposePlanException("docker compose resolves a port without a target.");
                var hostIp = Text(port, "host_ip");
                if (IsLoopback(hostIp)) continue;
                var key = PortKey(target, Text(port, "protocol"));
                var value = PortValue(hostIp, Text(port, "published"));
                Add(ExposureKind.PublishedPort, key, value, $"{key} (withheld)");
            }
        if (Text(definition, "network_mode") is { } networkMode && networkMode.Equals("host", StringComparison.Ordinal))
            Add(ExposureKind.HostNetwork, "network_mode", "host", "network_mode: host");
        if (Property(definition, "networks") is { ValueKind: JsonValueKind.Object } networks)
            foreach (var network in networks.EnumerateObject())
                if (IsExternalHostNetwork(network.Name, topNetworks))
                    Add(ExposureKind.HostNetwork, "networks", "host", "networks: host");
        if (Property(definition, "privileged") is { ValueKind: JsonValueKind.True })
            Add(ExposureKind.Privileged, "privileged", "true", "privileged: true");
        foreach (var cap in Strings(definition, "cap_add"))
            Add(ExposureKind.AddedCapability, Capability(cap), "", $"cap_add: {Capability(cap)}");
        foreach (var ns in (string[])["pid", "ipc", "uts", "userns_mode", "cgroup"])
            if (Text(definition, ns) is { } mode && mode.Equals("host", StringComparison.Ordinal))
                Add(ExposureKind.HostNamespace, ns, "host", $"{ns}: host");
        if (Property(definition, "devices") is { ValueKind: JsonValueKind.Array } devices)
            foreach (var device in devices.EnumerateArray())
            {
                var (host, value) = device.ValueKind == JsonValueKind.String
                    ? DeviceShort(device.GetString() ?? "")
                    : Device(Text(device, "source") ?? "", Text(device, "target"), Text(device, "permissions"));
                if (host.Length > 0) Add(ExposureKind.Device, host, value, $"device {host}");
            }
        foreach (var option in Strings(definition, "security_opt"))
            if (SecurityOption(option) is { } opt)
                Add(ExposureKind.SecurityOpt, opt.Name, opt.Value, $"security_opt {opt.Name}={opt.Value}");
        foreach (var rule in Strings(definition, "device_cgroup_rules"))
            Add(ExposureKind.DeviceCgroupRule, rule.Trim(), "", "device_cgroup_rules (withheld)");
        return found;
    }

    /// <summary>A service network that is the external network <c>host</c>.</summary>
    public static bool IsExternalHostNetwork(string key, IReadOnlyDictionary<string, JsonElement> topNetworks)
    {
        if (!topNetworks.TryGetValue(key, out var def) || def.ValueKind != JsonValueKind.Object) return false;
        var external = Property(def, "external") is { } ext
                       && (ext.ValueKind == JsonValueKind.True || ext.ValueKind == JsonValueKind.Object);
        var name = Text(def, "name")
                   ?? (Property(def, "external") is { ValueKind: JsonValueKind.Object } e ? Text(e, "name") : null)
                   ?? key;
        return external && name == "host";
    }

    /// <summary>The <c>network_mode</c> / <c>pid</c> / <c>ipc</c> of a service that shares another one's namespace
    /// (<c>service:x</c> or <c>container:x</c>), from compose's resolved model.</summary>
    public static IEnumerable<(string Field, string Reference)> SharedNamespaces(JsonElement definition)
    {
        foreach (var field in (string[])["network_mode", "pid", "ipc"])
            if (Text(definition, field) is { } value
                && (value.StartsWith("service:", StringComparison.Ordinal) || value.StartsWith("container:", StringComparison.Ordinal)))
                yield return (field, value);
    }

    // ------------------------------------------------------------------------------------------- docker inspect

    /// <summary>
    /// The exposures of a running container (<c>docker inspect</c>), by the detection table: <c>Config.Labels</c> under
    /// <paramref name="prefixes"/>, and <c>HostConfig</c> <c>NetworkMode</c>, <c>Privileged</c>, <c>CapAdd</c>,
    /// <c>PidMode</c>, <c>IpcMode</c>, <c>UTSMode</c>, <c>UsernsMode</c>, <c>CgroupnsMode</c>, <c>Devices</c>,
    /// <c>SecurityOpt</c>, <c>DeviceCgroupRules</c> and <c>PortBindings</c> (loopback dropped). Label values are withheld;
    /// the container's namespace references (<c>container:&lt;id&gt;</c>) are returned on their own.
    /// </summary>
    public static (IReadOnlyList<ComposeExposure> Exposures, IReadOnlyList<(string Field, string ContainerId)> Shared)
        FromInspect(string service, JsonElement container, IReadOnlyList<string> prefixes)
    {
        var found = new List<ComposeExposure>();
        var shared = new List<(string, string)>();
        void Add(ExposureKind kind, string key, string value, string detail, bool withheldValue = false) =>
            found.Add(new ComposeExposure(service, kind, detail) { Key = key, Value = value, ValueWithheld = withheldValue });

        if (Property(container, "Config") is { ValueKind: JsonValueKind.Object } config
            && Property(config, "Labels") is { ValueKind: JsonValueKind.Object } labels)
            foreach (var label in labels.EnumerateObject())
                if (prefixes.Any(p => label.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    Add(ExposureKind.RouteLabel, label.Name, label.Value.GetString() ?? "", label.Name, withheldValue: true);
        if (Property(container, "HostConfig") is not { ValueKind: JsonValueKind.Object } host)
            throw new ComposePlanException("A container's configuration cannot be read.");
        var networkMode = Text(host, "NetworkMode") ?? "";
        if (networkMode == "host") Add(ExposureKind.HostNetwork, "network_mode", "host", "network_mode: host");
        if (Property(host, "Privileged") is { ValueKind: JsonValueKind.True })
            Add(ExposureKind.Privileged, "privileged", "true", "privileged: true");
        foreach (var cap in Strings(host, "CapAdd"))
            Add(ExposureKind.AddedCapability, Capability(cap), "", $"cap_add: {Capability(cap)}");
        foreach (var (field, name) in (ReadOnlySpan<(string, string)>)[("PidMode", "pid"), ("IpcMode", "ipc"),
                     ("UTSMode", "uts"), ("UsernsMode", "userns_mode"), ("CgroupnsMode", "cgroup")])
            if (Text(host, field) == "host") Add(ExposureKind.HostNamespace, name, "host", $"{name}: host");
        foreach (var (field, name) in (ReadOnlySpan<(string, string)>)[("NetworkMode", "network_mode"), ("PidMode", "pid"),
                     ("IpcMode", "ipc")])
            if (Text(host, field) is { } mode && mode.StartsWith("container:", StringComparison.Ordinal))
                shared.Add((name, mode["container:".Length..]));
        if (Property(host, "Devices") is { ValueKind: JsonValueKind.Array } devices)
            foreach (var device in devices.EnumerateArray())
            {
                var (path, value) = Device(Text(device, "PathOnHost") ?? "", Text(device, "PathInContainer"),
                    Text(device, "CgroupPermissions"));
                if (path.Length > 0) Add(ExposureKind.Device, path, value, $"device {path}");
            }
        foreach (var option in Strings(host, "SecurityOpt"))
            if (SecurityOption(option) is { } opt)
                Add(ExposureKind.SecurityOpt, opt.Name, opt.Value, $"security_opt {opt.Name}={opt.Value}");
        foreach (var rule in Strings(host, "DeviceCgroupRules"))
            Add(ExposureKind.DeviceCgroupRule, rule.Trim(), "", "device_cgroup_rules (withheld)", withheldValue: true);
        if (Property(host, "PortBindings") is { ValueKind: JsonValueKind.Object } bindings)
            foreach (var binding in bindings.EnumerateObject())
            {
                var slash = binding.Name.IndexOf('/');
                var key = slash > 0 ? PortKey(binding.Name[..slash], binding.Name[(slash + 1)..]) : PortKey(binding.Name, null);
                if (binding.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var b in binding.Value.EnumerateArray())
                {
                    var ip = Text(b, "HostIp");
                    if (IsLoopback(ip)) continue;
                    var value = PortValue(ip, Text(b, "HostPort"));
                    Add(ExposureKind.PublishedPort, key, value, $"{value}->{key}");
                }
            }
        return (found, shared);
    }

    /// <summary>The identity of a container in an inspect document, its name without the leading slash, and its
    /// compose service.</summary>
    public static (string Id, string Name, string? Service) IdentityOf(JsonElement container) =>
        (Text(container, "Id") ?? "", (Text(container, "Name") ?? "").TrimStart('/'),
            Property(container, "Config") is { ValueKind: JsonValueKind.Object } config
            && Property(config, "Labels") is { ValueKind: JsonValueKind.Object } labels
            && labels.TryGetProperty(ComposeModel.ServiceLabel, out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null);

    /// <summary><c>docker inspect</c> of <paramref name="ids"/>: one JSON array.</summary>
    public static string InspectContainersCommand(IEnumerable<string> ids) =>
        "docker inspect " + string.Join(' ', ids.Select(Whiskers.Utils.ShellUtils.Quote));

    /// <summary>Parses the output of <see cref="InspectContainersCommand"/>; anything unreadable throws.</summary>
    public static IReadOnlyList<JsonElement> ParseInspect(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ComposePlanException("docker inspect did not return a list.");
            return document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch (JsonException)
        {
            throw new ComposePlanException("docker inspect did not return a readable document.");
        }
    }

    // ------------------------------------------------------------------------------------------- images

    /// <summary>The marker <see cref="InspectImagesCommand"/> prints for an image that is not on the host.</summary>
    public const string MissingImage = "MISSING";

    /// <summary>One line per image: <c>&lt;ref&gt;\t&lt;id&gt;\t&lt;labels as JSON&gt;</c>, or <c>&lt;ref&gt;\tMISSING</c>.
    /// Local only: nothing is pulled.</summary>
    public static string InspectImagesCommand(IEnumerable<string> images) =>
        "for i in " + string.Join(' ', images.Select(Whiskers.Utils.ShellUtils.Quote)) +
        "; do o=$(docker image inspect --format '{{.Id}}{{\"\\t\"}}{{json .Config.Labels}}' \"$i\" 2>/dev/null)" +
        " && printf '%s\\t%s\\n' \"$i\" \"$o\" || printf '%s\\t" + MissingImage + "\\n' \"$i\"; done";

    /// <summary>What <see cref="InspectImagesCommand"/> printed, per image: its id and labels, or <c>null</c> when it is
    /// missing. An image without a line, or a line that cannot be read, throws (fail closed).</summary>
    public static IReadOnlyDictionary<string, (string Id, IReadOnlyDictionary<string, string> Labels)?> ParseImages(
        string output, IReadOnlyCollection<string> images)
    {
        var result = new Dictionary<string, (string, IReadOnlyDictionary<string, string>)?>(StringComparer.Ordinal);
        foreach (var line in output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 3);
            if (parts.Length < 2 || !images.Contains(parts[0]))
                throw new ComposePlanException("The images of the deployment could not be read on the server.");
            if (parts.Length == 2 && parts[1] == MissingImage)
            {
                result[parts[0]] = null;
                continue;
            }
            if (parts.Length != 3 || !parts[1].StartsWith("sha256:", StringComparison.Ordinal))
                throw new ComposePlanException("The images of the deployment could not be read on the server.");
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var document = JsonDocument.Parse(parts[2]);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in document.RootElement.EnumerateObject())
                        labels[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                else if (document.RootElement.ValueKind != JsonValueKind.Null)
                    throw new ComposePlanException("The images of the deployment could not be read on the server.");
            }
            catch (JsonException)
            {
                throw new ComposePlanException("The images of the deployment could not be read on the server.");
            }
            result[parts[0]] = (parts[1], labels);
        }
        if (images.Any(i => !result.ContainsKey(i)))
            throw new ComposePlanException("The images of the deployment could not be read on the server.");
        return result;
    }

    /// <summary>The route labels of an image under <paramref name="prefixes"/>, for each service that uses it: the key is
    /// shown, the value never (it is not written in the compose file).</summary>
    public static IEnumerable<ComposeExposure> FromImageLabels(string service, IReadOnlyDictionary<string, string> labels,
        IReadOnlyList<string> prefixes) =>
        labels.Where(l => prefixes.Any(p => l.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(l => new ComposeExposure(service, ExposureKind.RouteLabel, l.Key)
            {
                Key = l.Key, Value = l.Value, ValueWithheld = true, FromImage = true,
            });

    // ------------------------------------------------------------------------------------------- JSON helpers

    internal static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) ? v : null;

    internal static string? Text(JsonElement element, string name) =>
        Property(element, name) is { } v
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    internal static IEnumerable<string> Strings(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "")
            : [];

    /// <summary>Whether a text looks like an interpolation (it holds a <c>$</c> that is not <c>$$</c>).</summary>
    public static bool HasVariable(string? raw) => raw is not null && VariablePattern.IsMatch(raw.Replace("$$", ""));

    private static readonly Regex VariablePattern = new(@"\$", RegexOptions.CultureInvariant);
}
