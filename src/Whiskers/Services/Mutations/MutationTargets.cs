using System.Text.Json;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Cloud;
using Whiskers.Services.Dns;
using Whiskers.Services.Docker;
using Whiskers.Services.Scheduler;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Workloads;

namespace Whiskers.Services.Mutations;

/// <summary>Builds a <see cref="MutationTarget"/> from tool-style arguments (<c>containerId</c>, <c>serverId</c>,
/// <c>zone</c>, …), and writes the resolved identity back into those arguments for execution. Used by the MCP and
/// agent adapters, which only see the arguments.</summary>
public static class MutationTargets
{
    public static MutationTarget FromArguments(Capability capability, IReadOnlyDictionary<string, JsonElement> args)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(args);
        var serverId = String(args, "serverId");
        var id = IdKey(capability.TargetKind, args) is { } key ? String(args, key) : null;
        var name = capability.TargetKind switch
        {
            "host" => String(args, "projectName") ?? String(args, "appName") ?? String(args, "serviceName") ?? String(args, "siteName"),
            "dns-zone" or "workload" => String(args, "name"),
            _ => null,
        };
        // A host or server named by nobody is the default server; the resolver decides which one that is (R1-06).
        return new MutationTarget(capability.TargetKind, id ?? "", name, serverId);
    }

    /// <summary>
    /// The arguments the operation runs with: the caller's arguments, with the key that named the target and
    /// <c>serverId</c> replaced by the identity the gateway decided on (R1-06). The tool then acts on exactly what
    /// was resolved and checked, never on a second, independent lookup of what the caller typed. Keys the caller
    /// did not pass stay absent (a tool without that parameter must not receive it), except <c>serverId</c> for a
    /// tool that takes one (<paramref name="takesServerId"/>).
    /// </summary>
    public static Dictionary<string, JsonElement> CanonicalArguments(Capability capability,
        IReadOnlyDictionary<string, JsonElement> args, MutationTarget resolved, bool takesServerId = false)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resolved);
        var canonical = new Dictionary<string, JsonElement>(args);
        if (resolved.Resolved && IdKey(capability.TargetKind, args) is { } key && key != "serverId"
            && !string.IsNullOrEmpty(resolved.Id))
            canonical[key] = JsonSerializer.SerializeToElement(resolved.Id);
        // The server is passed whenever the tool takes one, also when the caller left it out: "no server" means the
        // default server to the gateway, while a tool may read it as "local" — the decision would then be made on
        // one machine and the operation run on another.
        if ((takesServerId || String(args, "serverId") is not null) && !string.IsNullOrEmpty(resolved.ServerId))
            canonical["serverId"] = JsonSerializer.SerializeToElement(resolved.ServerId);
        if (String(args, "containerId") is not null && resolved.RelatedContainerId is { Length: > 0 } related
            && capability.TargetKind == "network")
            canonical["containerId"] = JsonSerializer.SerializeToElement(related);
        return canonical;
    }

    /// <summary>The argument that names the target of a kind, if the caller passed one.</summary>
    private static string? IdKey(string kind, IReadOnlyDictionary<string, JsonElement> args) => kind switch
    {
        "container" => "containerId",
        "network" => Has(args, "networkId") ? "networkId" : "name",
        "dns-zone" => "zone",
        "cloud-server" => "server",
        "scheduled-task" => Has(args, "taskId") ? "taskId" : "name",
        "log-alert" => "name",
        "volume" => Has(args, "volumeName") ? "volumeName" : "name",
        "workload" => "workloadId",
        "host" or "server" => "serverId",
        _ => null,
    };

    private static bool Has(IReadOnlyDictionary<string, JsonElement> args, string key) => String(args, key) is not null;

    internal static string? String(IReadOnlyDictionary<string, JsonElement> args, string key) =>
        args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;
}

/// <summary>Result of resolving a target against the inventory: the canonical target, or why it was refused —
/// with a <see cref="MutationReasonCodes"/> code and its arguments, so the UI can say it in the user's language.</summary>
public sealed record TargetResolution(MutationTarget? Target, string? Error, string? Code = null,
    IReadOnlyList<string>? Args = null);

/// <summary>Pipeline step after authorization: the target must exist and be unambiguous before policy is
/// evaluated (INV-57). An unresolved target is refused.</summary>
public interface IMutationTargetResolver
{
    Task<TargetResolution> ResolveAsync(MutationRequest request, Capability capability, CancellationToken ct);
}

/// <summary>
/// Resolves a mutation's target against the live inventory before policy (INV-24, INV-57, R1-06): the server first
/// (an empty server id is the default server, decided here, not later by whoever executes), then the object on it.
/// Unknown or ambiguous targets are refused. The result is canonical: ids, not prefixes or display names, plus what
/// the operation also touches (<see cref="MutationTarget.Related"/>: the containers attached to a network, the
/// container a network is connected to, a workload's live controller) so a protected resource is matched on what
/// really changes.
/// <para>Operations that create their target (a new network or task) resolve only the server; adding or saving a
/// server matches only that server by its own id (never the default server or a name) and passes a new one through. Volumes,
/// log alerts and the Whiskers-wide kinds pass through: their services identify them by a name they own.</para>
/// </summary>
public sealed class MutationTargetResolver : IMutationTargetResolver
{
    /// <summary>Capabilities whose target does not exist yet.</summary>
    private static readonly HashSet<string> Creates = new(StringComparer.Ordinal)
    {
        "network.create", "scheduler.create", "server.onboard",
    };

    private readonly IServiceProvider _services;

    public MutationTargetResolver(IServiceProvider services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    public async Task<TargetResolution> ResolveAsync(MutationRequest request, Capability capability, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capability);
        var target = request.Target;
        if (target.Kind != capability.TargetKind)
            return Refuse($"The target is a '{target.Kind}', but '{capability.Id}' acts on a '{capability.TargetKind}'.");

        return capability.TargetKind switch
        {
            "container" => await ContainerAsync(target, ct),
            "host" => Server(target, target.ServerId ?? NullIfEmpty(target.Id), mustExist: true),
            "server" => Server(target, NullIfEmpty(target.Id) ?? target.ServerId,
                mustExist: capability.Id is not ("server.onboard" or "server.save")),
            "network" => await NetworkAsync(request, capability, target, ct),
            "cloud-server" => CloudServer(target),
            "scheduled-task" => Creates.Contains(capability.Id) ? Pass(target) : await TaskAsync(target),
            "dns-zone" => DnsZone(target),
            "workload" => await WorkloadAsync(target, ct),
            _ => Pass(target),
        };
    }

    private async Task<TargetResolution> ContainerAsync(MutationTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.Id))
            return Refuse("Container ID or name is required.");
        var (server, error) = ResolveServer(target.ServerId);
        if (server is null) return Refuse(error!, target.ServerId);
        var docker = _services.GetService<IDockerService>();
        if (docker is null)
            return Refuse("No container runtime is available to resolve the target.");

        var containers = await docker.ListContainersAsync(all: true, serverId: server.Id, ct);
        var (container, notFound) = McpInputValidation.Resolve(containers, target.Id);
        return container is null
            ? Refuse(notFound ?? $"Container not found: {target.Id}", target.Id)
            : Done(target with { Id = container.Id, Name = container.Name, ServerId = server.Id });
    }

    private TargetResolution Server(MutationTarget target, string? idOrName, bool mustExist)
    {
        if (!mustExist)
        {
            // Adding or saving a server: only the very server by its id, never the default server or a name match —
            // the operation writes this id, so a decision on another server would record the wrong one.
            var servers = string.IsNullOrWhiteSpace(idOrName) ? null
                : _services.GetService<IServerConfigService>()?.GetServers();
            var existing = servers?.FirstOrDefault(s => s.Id == idOrName)
                           ?? servers?.FirstOrDefault(s => string.Equals(s.Id, idOrName, StringComparison.OrdinalIgnoreCase));
            return existing is null
                ? Pass(target)
                : Done(target with { Id = existing.Id, Name = target.Name ?? existing.Name, ServerId = existing.Id });
        }
        var (server, error) = ResolveServer(idOrName);
        if (server is not null)
            return Done(target with { Id = server.Id, Name = target.Name ?? server.Name, ServerId = server.Id });
        return Refuse(error!, idOrName);
    }

    private async Task<TargetResolution> NetworkAsync(MutationRequest request, Capability capability,
        MutationTarget target, CancellationToken ct)
    {
        var (server, error) = ResolveServer(target.ServerId);
        if (server is null) return Refuse(error!, target.ServerId);
        if (Creates.Contains(capability.Id))
            return Pass(target with { ServerId = server.Id });
        if (string.IsNullOrWhiteSpace(target.Id))
            return Refuse("Network ID or name is required.");
        var docker = _services.GetService<IDockerService>();
        if (docker is null)
            return Refuse("No container runtime is available to resolve the target.");

        var networks = await docker.ListNetworksAsync(server.Id);
        var exact = networks.Where(n => n.Id == target.Id || n.Name == target.Id).ToList();
        var matches = exact.Count > 0 ? exact
            : networks.Where(n => n.Id.StartsWith(target.Id, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0) return Refuse($"Network not found: {target.Id}", target.Id);
        if (matches.Count > 1)
            return Refuse($"Ambiguous network '{target.Id}' — matches {matches.Count} networks; use the full id.", target.Id);
        var network = matches[0];

        // What the operation also touches: for connect / disconnect the container named in the call, resolved on
        // the same server; otherwise (removal) every attached container, which the removal cuts off.
        var related = new List<string>();
        string? relatedContainerId = null;
        if (MutationTargets.String(request.Arguments, "containerId") is not { } containerArg)
            related.AddRange(network.Containers.SelectMany(c => new[] { c.ContainerId, c.Name }));
        else
        {
            var containers = await docker.ListContainersAsync(all: true, serverId: server.Id, ct);
            var (container, notFound) = McpInputValidation.Resolve(containers, containerArg);
            if (container is null) return Refuse(notFound ?? $"Container not found: {containerArg}", containerArg);
            related.Add(container.Id);
            related.Add(container.Name);
            relatedContainerId = container.Id;
        }
        return Done(target with
        {
            Id = network.Id, Name = network.Name, ServerId = server.Id,
            Related = related.Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            RelatedContainerId = relatedContainerId,
        });
    }

    private TargetResolution CloudServer(MutationTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Id))
            return Refuse("The Whiskers server (name or id) is required.");
        var cloud = _services.GetService<ICloudControlService>();
        if (cloud is null)
            return Refuse("Cloud control is not available.");
        var (server, error) = Unique(cloud.CloudServers(), target.Id, "cloud server");
        return server is null
            ? Refuse(error!, target.Id)
            : Done(target with { Id = server.Id, Name = server.Name, ServerId = server.Id });
    }

    private async Task<TargetResolution> TaskAsync(MutationTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Id))
            return Refuse("Task ID is required.");
        var scheduler = _services.GetService<ISchedulerService>();
        if (scheduler is null)
            return Refuse("The scheduler is not available.");
        var tasks = await scheduler.GetTasksAsync();
        var byId = tasks.FirstOrDefault(t => t.TaskId == target.Id);
        if (byId is null)
        {
            var byName = tasks.Where(t => t.Name == target.Id).ToList();
            if (byName.Count > 1)
                return Refuse($"Ambiguous task '{target.Id}' — {byName.Count} tasks have that name; use the task id.", target.Id);
            byId = byName.FirstOrDefault();
        }
        return byId is null
            ? Refuse($"Scheduled task not found: {target.Id}", target.Id)
            : Done(target with { Id = byId.TaskId, Name = byId.Name, ServerId = byId.ServerId ?? target.ServerId });
    }

    private TargetResolution DnsZone(MutationTarget target)
    {
        string zone;
        try
        {
            zone = DnsRecordService.NormalizeZone(target.Id);
        }
        catch (ArgumentException ex)
        {
            return Refuse(ex.Message);
        }
        // The same fence the DNS service applies: a configured list of zones is the inventory.
        var allowed = _services.GetService<IOptionsMonitor<DnsSettings>>()?.CurrentValue.AllowedZones?
            .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().TrimEnd('.').ToLowerInvariant()).ToList() ?? [];
        if (allowed.Count > 0 && !allowed.Contains(zone, StringComparer.Ordinal))
            return Refuse($"Zone '{zone}' is not one of the allowed zones.");
        return Done(target with { Id = zone });
    }

    private async Task<TargetResolution> WorkloadAsync(MutationTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.Id))
            return Refuse("Workload ID is required.");
        var (server, error) = ResolveServer(target.ServerId);
        if (server is null) return Refuse(error!, target.ServerId);
        var factory = _services.GetService<IWorkloadProviderFactory>();
        if (factory is null)
            return Refuse("No workload runtime is available to resolve the target.");

        // Live, not the label the page rendered: start / stop scale and restart rolls the pod's CURRENT controller.
        var pod = await factory.GetForServer(server.Id).GetWorkloadAsync(target.Id, ct);
        if (pod is null) return Refuse($"Workload not found: {target.Id}", target.Id);
        var controller = pod.ComposeProject is { Length: > 0 } c && c != "Standalone" ? c : pod.Name;
        return Done(target with
        {
            Id = pod.Id, Name = controller, ServerId = server.Id,
            Related = new[] { pod.Id, pod.Name }.Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
        });
    }

    /// <summary>The server a call acts on: an empty id is the configured default server; otherwise exactly one
    /// server whose id matches (case-sensitive first) or whose id / name matches ignoring case.</summary>
    private (Whiskers.Models.ServerConfig? Server, string? Error) ResolveServer(string? idOrName)
    {
        var config = _services.GetService<IServerConfigService>();
        if (config is null) return (null, "The server registry is not available.");
        if (string.IsNullOrWhiteSpace(idOrName))
            return config.GetDefaultServer() is { } fallback
                ? (fallback, null)
                : (null, "No server is configured.");
        return Unique(config.GetServers(), idOrName, "server");
    }

    private static (Whiskers.Models.ServerConfig? Server, string? Error) Unique(IReadOnlyList<Whiskers.Models.ServerConfig> servers, string idOrName,
        string what)
    {
        if (servers.FirstOrDefault(s => s.Id == idOrName) is { } exact) return (exact, null);
        var matches = servers.Where(s => string.Equals(s.Id, idOrName, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(s.Name, idOrName, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(s => s.Id).ToList();
        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => (null, $"Unknown {what}: '{idOrName}'."),
            _ => (null, $"Ambiguous {what} '{idOrName}' — matches {matches.Count}; use the id."),
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static TargetResolution Done(MutationTarget target) => new(target with { Resolved = true }, null);
    private static TargetResolution Pass(MutationTarget target) => new(target, null);
    /// <summary>A refusal with its reason code: unknown or ambiguous <paramref name="named"/> where the message says
    /// so, otherwise the English detail as the argument of <see cref="MutationReasonCodes.TargetNotResolved"/>.</summary>
    private static TargetResolution Refuse(string error, string? named = null)
    {
        if (!string.IsNullOrWhiteSpace(named) && error.StartsWith("Ambiguous", StringComparison.Ordinal))
            return new(null, error, MutationReasonCodes.TargetAmbiguous, [named]);
        if (!string.IsNullOrWhiteSpace(named)
            && (error.StartsWith("Unknown", StringComparison.Ordinal) || error.Contains("not found", StringComparison.Ordinal)))
            return new(null, error, MutationReasonCodes.TargetUnknown, [named]);
        return new(null, error, MutationReasonCodes.TargetNotResolved, [error]);
    }
}
