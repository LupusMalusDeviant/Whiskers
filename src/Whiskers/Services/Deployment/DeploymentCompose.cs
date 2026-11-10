using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Deployment;

/// <summary>A deployment request that cannot become a compose file (contract 0013, D1). The message names the field and
/// never an environment value.</summary>
public sealed class DeploymentComposeException(string message) : Exception(message);

/// <summary>The compose project a <see cref="DeploymentRequest"/> becomes (contract 0013, D1): its project name (also
/// the directory under <c>/opt/deployments</c>), the service and container name, and the file Whiskers writes.</summary>
public sealed record DeploymentComposeFile(string Project, string Name, string Yaml)
{
    /// <summary>The deployment of this file on <paramref name="serverId"/>, in <c>/opt/deployments/&lt;project&gt;</c>.</summary>
    public ComposeDeployment Deployment(string serverId) =>
        ComposeDeployment.FromContent(serverId, ComposeDeployment.DeploymentDirectory(Project), Yaml);
}

/// <summary>
/// Contract 0013, D1: the only translation of the Deploy page's form and MCP <c>deploy_app</c> into a compose file. The
/// page, the tool and the gateway's target (<c>MutationTargets</c>, D5) all call it, so the decision and the run see
/// the same file. Pure: reads nothing, writes nothing; an entry that cannot be mapped refuses the whole request.
/// </summary>
public static partial class DeploymentCompose
{
    /// <summary>The first line of every file this mapper writes: the form and <c>deploy_app</c> own the directory (D3).</summary>
    public const string Marker = "# whiskers: app.deploy";

    private static readonly JsonSerializerOptions Quoting = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The compose file of <paramref name="request"/>. Throws <see cref="DeploymentComposeException"/> for an
    /// entry D1 refuses.</summary>
    public static DeploymentComposeFile ToCompose(DeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var image = (request.Image ?? "").Trim();
        if (image.Length == 0) throw new DeploymentComposeException("The image is required.");
        if (image.Contains('$') || image.Any(char.IsWhiteSpace))
            throw new DeploymentComposeException("The image may not contain '$' or whitespace.");

        var name = string.IsNullOrWhiteSpace(request.ContainerName) ? NameFromImage(image) : request.ContainerName.Trim();
        if (!ValidName().IsMatch(name))
            throw new DeploymentComposeException(string.IsNullOrWhiteSpace(request.ContainerName)
                ? "No valid name can be derived from the image; enter a container name."
                : "Invalid name. Use only letters, numbers, dots, hyphens and underscores.");
        var project = ComposeModel.NormalizeProjectName(name);
        if (!McpInputValidation.IsSafeProjectName(project))
            throw new DeploymentComposeException($"The name '{name}' does not give a valid compose project name.");

        var restart = (request.RestartPolicy ?? "").Trim() switch
        {
            "" or "unless-stopped" => "unless-stopped",
            "always" => "always",
            "no" => "no",
            "on-failure" => "on-failure:5",
            _ => throw new DeploymentComposeException("Unknown restart policy. Use unless-stopped, always, on-failure or no."),
        };

        var network = (request.NetworkName ?? "").Trim();
        if (network.Contains('$')) throw new DeploymentComposeException("The network name may not contain '$'.");

        var ports = new List<string>();
        foreach (var (hostRaw, containerRaw) in request.PortMappings)
        {
            var host = Port(hostRaw);
            var container = Port(containerRaw);
            var ip = request.PortBindIps.GetValueOrDefault(hostRaw) is { Length: > 0 } bind ? BindIp(bind) : null;
            ports.Add(ip is null ? $"{host}:{container}/tcp" : $"{ip}:{host}:{container}/tcp");
        }

        var volumes = new List<string>();
        var named = new List<string>();
        foreach (var raw in request.Volumes.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            var (entry, volume) = Volume(raw.Trim());
            volumes.Add(entry);
            if (volume is not null && !named.Contains(volume, StringComparer.Ordinal)) named.Add(volume);
        }

        var environment = new List<(string Key, string Value)>();
        foreach (var (rawKey, value) in request.EnvironmentVars)
        {
            var key = (rawKey ?? "").Trim();
            if (!EnvKey().IsMatch(key))
                throw new DeploymentComposeException($"Invalid environment variable name '{Shown(key)}'.");
            if (environment.Any(e => e.Key == key))
                throw new DeploymentComposeException($"The environment variable '{key}' is set twice.");
            // No interpolation: a '$' in a value is written as '$$', so compose passes it on literally.
            environment.Add((key, (value ?? "").Replace("$", "$$", StringComparison.Ordinal)));
        }

        var yaml = new StringBuilder();
        yaml.Append(Marker).Append('\n');
        yaml.Append("name: ").Append(Q(project)).Append('\n');
        yaml.Append("services:\n");
        yaml.Append("  ").Append(Q(name)).Append(":\n");
        yaml.Append("    container_name: ").Append(Q(name)).Append('\n');
        yaml.Append("    image: ").Append(Q(image)).Append('\n');
        yaml.Append("    restart: ").Append(Q(restart)).Append('\n');
        if (network is "" or "bridge" or "host" or "none")
            yaml.Append("    network_mode: ").Append(Q(network is "" ? "bridge" : network)).Append('\n');
        else
            yaml.Append("    networks:\n      - ").Append(Q(network)).Append('\n');
        List("ports", ports);
        List("volumes", volumes);
        if (environment.Count > 0)
        {
            yaml.Append("    environment:\n");
            foreach (var (key, value) in environment)
                yaml.Append("      ").Append(Q(key)).Append(": ").Append(Q(value)).Append('\n');
        }
        if (named.Count > 0)
        {
            // A named volume keeps its own name (as before), not the project-scoped <project>_<name>.
            yaml.Append("volumes:\n");
            foreach (var volume in named)
                yaml.Append("  ").Append(Q(volume)).Append(":\n    name: ").Append(Q(volume)).Append('\n');
        }
        if (network is not ("" or "bridge" or "host" or "none"))
            yaml.Append("networks:\n  ").Append(Q(network)).Append(":\n    external: true\n");
        return new DeploymentComposeFile(project, name, yaml.ToString());

        void List(string key, List<string> entries)
        {
            if (entries.Count == 0) return;
            yaml.Append("    ").Append(key).Append(":\n");
            foreach (var entry in entries) yaml.Append("      - ").Append(Q(entry)).Append('\n');
        }
    }

    /// <summary>
    /// The request of MCP <c>deploy_app</c>'s arguments (comma lists). A malformed port or environment entry refuses the
    /// call — nothing is dropped silently any more (D1). Used by the tool and by the gateway's target, so both map the
    /// same arguments the same way.
    /// </summary>
    public static DeploymentRequest FromToolArguments(string? appName, string? image, string? ports, string? envVars,
        string? volumes, string? restartPolicy, string? network)
    {
        if (string.IsNullOrWhiteSpace(appName) || !ValidName().IsMatch(appName.Trim()))
            throw new DeploymentComposeException("Invalid app name. Use only letters, numbers, dots, hyphens, and underscores.");
        if (string.IsNullOrWhiteSpace(image)) throw new DeploymentComposeException("Image is required.");
        var request = new DeploymentRequest
        {
            ContainerName = appName.Trim(),
            Image = image.Trim(),
            RestartPolicy = string.IsNullOrWhiteSpace(restartPolicy) ? "unless-stopped" : restartPolicy.Trim(),
            NetworkName = string.IsNullOrWhiteSpace(network) ? null : network.Trim(),
        };
        var index = 0;
        foreach (var mapping in Split(ports))
        {
            index++;
            var parts = mapping.Split(':');
            if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
                throw new DeploymentComposeException($"Port mapping #{index} is not 'hostPort:containerPort'.");
            var host = parts[0].Trim();
            if (!request.PortMappings.TryAdd(host, parts[1].Trim()))
                throw new DeploymentComposeException($"Host port {Shown(host)} is mapped twice.");
        }
        index = 0;
        foreach (var entry in Split(envVars))
        {
            index++;
            var eq = entry.IndexOf('=');
            // The entry is not echoed: without '=' it may be a value.
            if (eq <= 0) throw new DeploymentComposeException($"Environment entry #{index} is not 'KEY=VALUE'.");
            var key = entry[..eq].Trim();
            if (!request.EnvironmentVars.TryAdd(key, entry[(eq + 1)..].Trim()))
                throw new DeploymentComposeException($"The environment variable '{Shown(key)}' is set twice.");
        }
        request.Volumes = Split(volumes).ToList();
        return request;
    }

    /// <summary>A name from the image's last path segment, without tag and digest (<c>ghcr.io/org/app:1</c> → <c>app</c>).</summary>
    public static string NameFromImage(string image)
    {
        var reference = image.Split('@')[0];
        var last = reference[(reference.LastIndexOf('/') + 1)..];
        return last.Split(':')[0];
    }

    private static IEnumerable<string> Split(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int Port(string? raw) =>
        int.TryParse((raw ?? "").Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var port) && port is >= 1 and <= 65535
            ? port
            : throw new DeploymentComposeException($"'{Shown(raw)}' is not a port (1-65535).");

    private static string BindIp(string raw)
    {
        if (!IPAddress.TryParse(raw.Trim().Trim('[', ']'), out var ip))
            throw new DeploymentComposeException($"'{Shown(raw)}' is not an IP address.");
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
    }

    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "ro", "rw", "z", "Z" };

    /// <summary>A volume entry in short syntax, and the named volume it uses (or <c>null</c> for a host path).</summary>
    private static (string Entry, string? Named) Volume(string raw)
    {
        if (raw.Contains('$')) throw new DeploymentComposeException("A volume may not contain '$'.");
        var parts = raw.Split(':');
        if (parts.Length is not (2 or 3))
            throw new DeploymentComposeException($"Volume '{raw}' is not 'source:destination[:mode]'.");
        var (source, destination) = (parts[0].Trim(), parts[1].Trim());
        if (parts.Length == 3 && !Modes.Contains(parts[2].Trim()))
            throw new DeploymentComposeException($"Volume '{raw}' has an unknown mode (use ro, rw, z or Z).");
        if (!destination.StartsWith('/'))
            throw new DeploymentComposeException($"Volume '{raw}' needs an absolute path inside the container.");
        string? named = null;
        if (!source.StartsWith('/'))
        {
            if (source.Contains('/') || source.StartsWith('.') || source.StartsWith('~') || !VolumeName().IsMatch(source))
                throw new DeploymentComposeException($"Volume '{raw}' has a relative source; use an absolute host path or a volume name.");
            named = source;
        }
        var entry = $"{source}:{destination}" + (parts.Length == 3 ? $":{parts[2].Trim()}" : "");
        return (entry, named);
    }

    /// <summary>A double-quoted YAML scalar (JSON string syntax is valid YAML).</summary>
    private static string Q(string value) => JsonSerializer.Serialize(value, Quoting);

    /// <summary>A short, single-line excerpt of an input that may be shown (never an environment value).</summary>
    private static string Shown(string? value)
    {
        var line = (value ?? "").ReplaceLineEndings(" ");
        return line.Length > 40 ? line[..40] + "…" : line;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9._-]+\z")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex EnvKey();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*\z")]
    private static partial Regex VolumeName();
}
