using System.Text.Json;
using Whiskers.Mcp.Tools;
using Whiskers.Services.Docker;

namespace Whiskers.Services.Mutations;

/// <summary>Builds a <see cref="MutationTarget"/> from tool-style arguments (<c>containerId</c>, <c>serverId</c>,
/// <c>zone</c>, …). Used by the MCP and agent adapters, which only see the arguments.</summary>
public static class MutationTargets
{
    public static MutationTarget FromArguments(Capability capability, IReadOnlyDictionary<string, JsonElement> args)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(args);
        var serverId = String(args, "serverId");
        var id = capability.TargetKind switch
        {
            "container" => String(args, "containerId"),
            "network" => String(args, "networkId") ?? String(args, "name"),
            "dns-zone" => String(args, "zone"),
            "cloud-server" => String(args, "server"),
            "scheduled-task" => String(args, "taskId") ?? String(args, "name"),
            "log-alert" => String(args, "name"),
            "volume" => String(args, "volumeName") ?? String(args, "name"),
            "workload" => String(args, "workloadId"),
            "host" or "server" => serverId ?? "local",
            _ => null,
        };
        var name = capability.TargetKind switch
        {
            "host" => String(args, "projectName") ?? String(args, "appName") ?? String(args, "serviceName") ?? String(args, "siteName"),
            "dns-zone" or "workload" => String(args, "name"),
            _ => null,
        };
        return new MutationTarget(capability.TargetKind, id ?? "", name, serverId);
    }

    private static string? String(IReadOnlyDictionary<string, JsonElement> args, string key) =>
        args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;
}

/// <summary>Result of resolving a target against the inventory: the canonical target, or why it was refused.</summary>
public sealed record TargetResolution(MutationTarget? Target, string? Error);

/// <summary>Pipeline step after authorization: the target must exist and be unambiguous before policy is
/// evaluated (INV-57). An unresolved target is refused.</summary>
public interface IMutationTargetResolver
{
    Task<TargetResolution> ResolveAsync(MutationRequest request, Capability capability, CancellationToken ct);
}

/// <summary>
/// WP2 step 2.1 resolves <b>container</b> targets (id, name or unique id prefix, the rule MCP tools already use) to
/// the container's full id and name. Other target kinds pass through unchanged and are resolved by the service that
/// acts on them; their inventory checks follow with the surfaces that use them.
/// </summary>
public sealed class MutationTargetResolver : IMutationTargetResolver
{
    private readonly IServiceProvider _services;

    public MutationTargetResolver(IServiceProvider services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    public async Task<TargetResolution> ResolveAsync(MutationRequest request, Capability capability, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capability);
        var target = request.Target;
        if (capability.TargetKind != "container" || target.Kind != "container")
            return new TargetResolution(target, null);
        if (string.IsNullOrWhiteSpace(target.Id))
            return new TargetResolution(null, "Container ID or name is required.");

        var docker = _services.GetService<IDockerService>();
        if (docker is null)
            return new TargetResolution(null, "No container runtime is available to resolve the target.");

        var containers = await docker.ListContainersAsync(all: true, serverId: target.ServerId, ct);
        var (container, error) = McpInputValidation.Resolve(containers, target.Id);
        return container is null
            ? new TargetResolution(null, error ?? $"Container not found: {target.Id}")
            : new TargetResolution(target with { Id = container.Id, Name = container.Name }, null);
    }
}
