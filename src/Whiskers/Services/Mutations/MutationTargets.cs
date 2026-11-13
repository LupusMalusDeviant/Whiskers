using System.Text.Json;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Services.Cloud;
using Whiskers.Services.Deployment;
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
        var id = IdKey(capability, args) is { } key ? String(args, key) : null;
        var name = capability.TargetKind switch
        {
            "host" => String(args, "projectName") ?? String(args, "appName") ?? String(args, "serviceName") ?? String(args, "siteName"),
            "dns-zone" or "workload" or "git-deploy-app" or "webhook" => String(args, "name"),
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
        if (resolved.Resolved && IdKey(capability, args) is { } key && key != "serverId"
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

    /// <summary>The argument that names the target of a capability, if the caller passed one. A volume backup is named
    /// by its backup id, a log-alert rule by its rule id where the call has one (contract 0012, D5, D6).</summary>
    private static string? IdKey(Capability capability, IReadOnlyDictionary<string, JsonElement> args) => capability.TargetKind switch
    {
        "container" => "containerId",
        "network" => Has(args, "networkId") ? "networkId" : "name",
        "dns-zone" => "zone",
        "cloud-server" => "server",
        "scheduled-task" => Has(args, "taskId") ? "taskId" : "name",
        "log-alert" => Has(args, "ruleId") ? "ruleId" : "name",
        "volume" when capability.Id is "volume.restore" or "volume.backup_delete" => "backupId",
        "volume" => Has(args, "volumeName") ? "volumeName" : "name",
        "whiskers" when capability.Id == "selfbackup.delete" => "archive",
        "fleet" when capability.Id == "automation.owner_set" => "feature",
        "workload" => "workloadId",
        "git-deploy-app" => "appId",
        "webhook" => "webhookId",
        "config" => "section",
        "account" => "account",
        "identity" => "binding",
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
    IReadOnlyList<string>? Args = null)
{
    /// <summary>Contract 0012 (D1): the typed object the execution acts on, built by the resolver from
    /// <see cref="Target"/> (which stays the recorded form). Set by <see cref="MutationTargetResolver"/> for every
    /// resolved target; <c>null</c> for a refusal and from a resolver that builds none (test doubles).</summary>
    public ResolvedTarget? Object { get; init; }

    /// <summary>How a branch builds its object from the final recorded target (inside the resolver only).</summary>
    internal Func<MutationTarget, ResolvedTarget>? Build { get; init; }
}

/// <summary>Pipeline step after authorization: the target must exist and be unambiguous before policy is
/// evaluated (INV-57). An unresolved target is refused.</summary>
public interface IMutationTargetResolver
{
    Task<TargetResolution> ResolveAsync(MutationRequest request, Capability capability, CancellationToken ct);

    /// <summary>
    /// Contract 0012 (D9): reads <paramref name="target"/> again right before it is acted on — resolving
    /// <paramref name="request"/> (the target as the caller named it) the same way as the decision did — and compares
    /// the object's stable identity, server and
    /// <see cref="ResolvedTarget.Fingerprint"/>. Returns a resolution with <see cref="TargetResolution.Target"/> set
    /// when the object is still the decided one; otherwise a refusal coded
    /// <see cref="MutationReasonCodes.TargetUnknown"/> (vanished) or <see cref="MutationReasonCodes.TargetChanged"/>
    /// (replaced or changed). A read that fails throws; the gateway refuses (fail closed).
    /// </summary>
    Task<TargetResolution> ConfirmAsync(MutationRequest request, Capability capability, ResolvedTarget target,
        CancellationToken ct);
}

/// <summary>
/// Resolves a mutation's target against the live inventory before policy (INV-24, INV-57, R1-06): the server first
/// (an empty server id is the default server, decided here, not later by whoever executes), then the object on it.
/// Unknown or ambiguous targets are refused. The result is canonical: ids, not prefixes or display names, plus what
/// the operation also touches (<see cref="MutationTarget.Related"/>: the containers attached to a network, the
/// container a network is connected to, a workload's live controller) so a protected resource is matched on what
/// really changes.
/// <para>Operations that create their target (a new network or task) resolve only the server; adding or saving a
/// server matches only that server by its own id (never the default server or a name) and passes a new one through.</para>
/// <para>Contract 0012: every kind has a branch — volumes and volume backups, log-alert rules and Whiskers' own records
/// included (<c>MutationTargetObjects.cs</c>) — and every resolution carries its typed <see cref="ResolvedTarget"/>
/// (<see cref="TargetResolution.Object"/>). A kind without a branch is refused.</para>
/// </summary>
public sealed partial class MutationTargetResolver : IMutationTargetResolver
{
    /// <summary>Capabilities whose target does not exist yet (and <c>server.onboard</c>, which may name a new server).</summary>
    private static readonly HashSet<string> Creates = new(StringComparer.Ordinal)
    {
        "network.create", "scheduler.create", "server.onboard", "container.create",
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

        var resolution = capability.TargetKind switch
        {
            "container" when Creates.Contains(capability.Id) => NewOnServer(target),
            "container" => await ContainerAsync(request, capability, target, ct),
            "host" => await HostAsync(request, capability, target, ct),
            "server" when capability.Id.StartsWith("endpointcheck.", StringComparison.Ordinal) =>
                await RecordAsync(request, capability, target, ct),
            "server" => Server(target, NullIfEmpty(target.Id) ?? target.ServerId,
                mustExist: capability.Id is not ("server.onboard" or "server.save"),
                onboard: capability.Id == "server.onboard"),
            "network" => await NetworkAsync(request, capability, target, ct),
            "cloud-server" => CloudServer(target),
            "scheduled-task" => Creates.Contains(capability.Id) ? Pass(target) : await TaskAsync(target),
            "dns-zone" => DnsZone(target),
            "workload" => await WorkloadAsync(target, ct),
            "git-deploy-app" => await GitAppAsync(request, capability, target, ct),
            "webhook" => await WebhookAsync(request, capability, target),
            "volume" => await VolumeAsync(request, capability, target, ct),
            "log-alert" => await LogAlertAsync(capability, target),
            "config" or "account" or "identity" or "whiskers" or "fleet" => await RecordAsync(request, capability, target, ct),
            // Contract 0012 (D2): every kind has a branch; one without is refused, never passed through.
            _ => Refuse($"'{capability.Id}' acts on a '{capability.TargetKind}', which Whiskers cannot resolve."),
        };
        return Finish(capability, await WithMentionedContainersAsync(request, resolution, ct));
    }

    /// <summary>An object the operation creates on a server: the server only.</summary>
    private TargetResolution NewOnServer(MutationTarget target)
    {
        var (server, error) = ResolveServer(target.ServerId);
        return server is null ? Refuse(error!, target.ServerId) : Pass(target with { ServerId = server.Id });
    }

    /// <summary>Shortest word of free text that is looked up as a container id (Docker's short ids have 12), so
    /// ordinary hex-looking words (<c>facade</c>, a date) do not count.</summary>
    internal const int MinimumMentionedIdLength = 12;

    /// <summary>Shortest id prefix looked up in a command that runs a container runtime (<c>docker rm -f c0</c>):
    /// Docker accepts any unique prefix, so every hex word there counts — it hits only when it really is a prefix of
    /// a protected container's id.</summary>
    internal const int MinimumRuntimeIdLength = 1;

    /// <summary>
    /// R1-16 (INV-24): free text — a command, SQL, a compose file — can name a container by its id or a unique id
    /// prefix, which no name pattern matches. Every such word is looked up among the containers of the resolved
    /// server, and the containers it names are added to what the operation touches (with their names), so the
    /// protected-resource check sees them. The lookup fails closed: if the server's containers cannot be listed, the
    /// operation is refused.
    /// </summary>
    private async Task<TargetResolution> WithMentionedContainersAsync(MutationRequest request, TargetResolution resolution,
        CancellationToken ct)
    {
        if (resolution.Target is not { ServerId: { Length: > 0 } serverId } target) return resolution;
        var words = request.Arguments
            .Where(a => Whiskers.Services.Guardrails.ProtectedResourceMatcher.FreeTextArguments.Contains(a.Key)
                        && a.Value.ValueKind == JsonValueKind.String)
            .SelectMany(a =>
            {
                var text = a.Value.GetString() ?? "";
                var shortest = Whiskers.Services.Guardrails.ProtectedResourceMatcher.InvokesContainerRuntime(a.Key, text)
                    ? MinimumRuntimeIdLength
                    : MinimumMentionedIdLength;
                // In a runtime command every hex word counts (an all-digit id prefix included), except a number
                // given as an option's value (--tail 100).
                var candidates = shortest == MinimumRuntimeIdLength
                    ? Whiskers.Services.Guardrails.ProtectedResourceMatcher.RuntimeIdWords(text)
                    : Whiskers.Services.Guardrails.ProtectedResourceMatcher.MentionWords(a.Key, text);
                return candidates.Where(w => w.Length >= shortest && w.All(Uri.IsHexDigit));
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (words.Count == 0) return resolution;
        if (_services.GetService<IDockerService>() is not { } docker)
            return Refuse("The containers the operation names could not be checked: no container runtime is available.");
        IList<ContainerInfo> containers;
        try
        {
            containers = await docker.ListContainersAsync(all: true, serverId: serverId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Refuse("The containers the operation names could not be checked: the server's containers could not be listed.");
        }
        var mentioned = containers
            .Where(c => words.Any(w => c.Id.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (mentioned.Count == 0) return resolution;
        return resolution with
        {
            Target = target with
            {
                Related = target.Related.Concat(mentioned.SelectMany(c => new[] { c.Id, c.Name }))
                    .Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
                RelatedResources = target.RelatedResources
                    .Concat(mentioned.Select(c => (IReadOnlyList<string>)Aliases(c.Id, c.Name))).ToList(),
            },
        };
    }

    private async Task<TargetResolution> ContainerAsync(MutationRequest request, Capability capability,
        MutationTarget target, CancellationToken ct)
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
        if (container is null)
            return Refuse(notFound ?? $"Container not found: {target.Id}", target.Id);
        var resolved = target with { Id = container.Id, Name = container.Name, ServerId = server.Id };
        // Contract 0012 (D9): a container replaced under the same name has another id or creation time.
        var fingerprint = Hash(container.Id, container.Created.ToString("O"));
        var project = container.ComposeProject is { Length: > 0 } p && p != "Standalone" ? p : null;
        Func<MutationTarget, ResolvedTarget> build = t => new ResolvedContainer(Token, t, fingerprint, project);

        // A changed .env brings the container's whole compose project up (R1-07): every container of the plan is
        // decided with it. A plan that cannot be made is refused.
        if (capability.Id == "container.env_set" && MutationTargets.String(request.Arguments, "envVars") is { } envVars)
        {
            var planned = await WithPlanAsync(resolved,
                planner => planner.PlanEnvChangeAsync(container, server.Id, existing => DotEnvMerge.Merge(existing, envVars).Content, ct));
            return planned.Target is null ? planned : planned with { Build = build };
        }
        return Done(resolved, build);
    }

    /// <summary>A host-level operation: its server. A compose deployment that brings its file along (the MCP
    /// <c>deploy_compose</c> tool) is planned here, and every container the plan touches is decided with it (R1-07).
    /// Without the file (a UI deploy, which decides the plan's containers itself) only the server is resolved. A
    /// cleanup (<c>docker system prune</c>) removes every stopped container of the server, so each of them is decided
    /// with it (R1-16); if they cannot be listed the cleanup is refused.</summary>
    private async Task<TargetResolution> HostAsync(MutationRequest request, Capability capability, MutationTarget target,
        CancellationToken ct)
    {
        var resolution = Server(target, target.ServerId ?? NullIfEmpty(target.Id), mustExist: true);
        // Contract 0012 (D11): an nginx site, a systemd unit or a firewall rule on the server, against the host's listing.
        if (resolution.Target is { } onServer && CapabilityCatalog.HostObjects.Contains(capability.Id))
            return await HostObjectAsync(request, capability, onServer);
        if (resolution.Target is { } pruned && capability.Id == "host.prune")
            return await WithStoppedContainersAsync(pruned,
                MutationTargets.String(request.Arguments, "containerIds")?.Split('\n', StringSplitOptions.RemoveEmptyEntries), ct);
        if (resolution.Target is { } appHost && capability.Id == "app.deploy")
            return await AppDeployAsync(request, resolution, appHost, ct);
        if (resolution.Target is not { } resolvedHost || capability.Id != "compose.deploy") return resolution;
        // Contract 0006 (review B1): the nested decision's lists are Whiskers' own — a page or an automation gate fills
        // them from the plan it made. A caller on any other surface (MCP, the agent) could mark an exposure or a shared
        // container as decided with them, so such a call is refused — and so is `mounts` (review N3), which would
        // otherwise reach the target's Related set.
        if (request.Surface != MutationSurface.Ui && !Unattended.IsSurface(request.Surface)
            && request.Arguments.Keys.FirstOrDefault(k => RefusedFromCallers.Contains(k, StringComparer.OrdinalIgnoreCase)) is { } injected)
            return Refuse($"The argument '{injected}' is set by Whiskers itself for a planned deployment; a call may not bring it.");
        // Contract 0006 (Amendment 7): the containers a planned deployment shares a namespace with, from the nested
        // decision — whatever else the call carries. A withheld one came from an .env value: decided, never recorded.
        var related = Lines(request, "related");
        var relatedWithheld = Lines(request, "relatedWithheld");
        var host = related.Length + relatedWithheld.Length == 0 ? resolvedHost : resolvedHost with
        {
            Related = resolvedHost.Related.Concat(related).Concat(relatedWithheld).Distinct(StringComparer.Ordinal).ToList(),
            Withheld = resolvedHost.Withheld.Concat(relatedWithheld).ToHashSet(StringComparer.Ordinal),
        };
        if (MutationTargets.String(request.Arguments, "composeContent") is not { } content)
        {
            // The mounts of a planned deployment, decided before its containers (ComposeImpact, R1-16): each is
            // something the deployment touches. Whoever planned the list may have filled it from an .env: none of it is
            // recorded.
            var mounts = Lines(request, "mounts");
            // Contract 0006: the exposures of a planned deployment (the nested decision of the UI and automation
            // paths), as the rule reads them; a withheld one is bound and compared, never written.
            var exposures = Lines(request, "exposure");
            var exposuresWithheld = Lines(request, "exposureWithheld");
            // Review m1: an entry whose value only is withheld — its key may be named in the reason.
            var valuesWithheld = Lines(request, "exposureValueWithheld");
            if (mounts.Length + exposures.Length + exposuresWithheld.Length + valuesWithheld.Length == 0)
                return ReferenceEquals(host, resolvedHost) ? resolution : Done(host);
            return Done(host with
            {
                Related = host.Related.Concat(mounts).Distinct(StringComparer.Ordinal).ToList(),
                Withheld = host.Withheld.Concat(mounts).Concat(exposuresWithheld).Concat(valuesWithheld)
                    .ToHashSet(StringComparer.Ordinal),
                Exposures = host.Exposures.Concat(exposures).Concat(exposuresWithheld).Concat(valuesWithheld)
                    .Distinct(StringComparer.Ordinal).ToList(),
                ExposureKeysShown = host.ExposureKeysShown.Concat(valuesWithheld.Except(exposuresWithheld, StringComparer.Ordinal))
                    .ToHashSet(StringComparer.Ordinal),
            });
        }
        if (MutationTargets.String(request.Arguments, "projectName") is not { } project
            || !McpInputValidation.IsSafeProjectName(project))
            return Refuse("A compose deployment needs a valid project name.");
        return await WithPlanAsync(host, planner => planner.PlanAsync(
            ComposeDeployment.FromContent(host.ServerId!, ComposeDeployment.DeploymentDirectory(project), content), ct));
    }

    /// <summary>
    /// Contract 0013, D5: an <c>app.deploy</c> from any surface but the UI and automation (MCP <c>deploy_app</c>, the
    /// agent) is planned here from its arguments through the same mapper the tool uses (<see cref="DeploymentCompose"/>)
    /// and the same path checks (<see cref="DeploymentPathPlanner"/>), so its exposures, mounts and touched containers
    /// are decided — <c>ExposureChangeRule</c> sees them. Arguments that cannot be mapped, a Kubernetes server, or the
    /// nested decision's arguments refuse the call. The UI and automation decide their plan nested themselves
    /// (<see cref="ComposeImpact"/>); only the server is resolved for them.
    /// </summary>
    private async Task<TargetResolution> AppDeployAsync(MutationRequest request, TargetResolution resolution,
        MutationTarget host, CancellationToken ct)
    {
        // INV-62 (user decision 2026-11-10): no app deployment while the Deployment module is off, on any surface.
        if (DeploymentModuleGate.IsOff(_services.GetService<Whiskers.Modules.IModuleRegistry>()))
            return Refuse($"Deployment refused: {DeploymentModuleGate.DisabledReason}");
        if (request.Surface is MutationSurface.Ui or MutationSurface.Automation) return resolution;
        if (request.Arguments.Keys.FirstOrDefault(k => RefusedFromCallers.Contains(k, StringComparer.OrdinalIgnoreCase)) is { } injected)
            return Refuse($"The argument '{injected}' is set by Whiskers itself for a planned deployment; a call may not bring it.");
        var (server, error) = ResolveServer(host.ServerId);
        if (server is null) return Refuse(error!, host.ServerId);
        if (server.ConnectionType == ConnectionType.Kubernetes)
            return Refuse("deploy_app needs a Docker host.");
        DeploymentComposeFile file;
        try
        {
            var args = request.Arguments;
            file = DeploymentCompose.ToCompose(DeploymentCompose.FromToolArguments(MutationTargets.String(args, "appName"),
                MutationTargets.String(args, "image"), MutationTargets.String(args, "ports"),
                MutationTargets.String(args, "envVars"), MutationTargets.String(args, "volumes"),
                MutationTargets.String(args, "restartPolicy"), MutationTargets.String(args, "network")));
        }
        catch (DeploymentComposeException ex)
        {
            return Refuse($"Deployment refused: {ex.Message}");
        }
        if (_services.GetService<IDockerService>() is not { } docker
            || _services.GetService<Whiskers.Services.Server.IHostCommandExecutor>() is not { } executor)
            return Refuse("The deployment's impact cannot be determined: no container runtime is available.");
        return await WithPlanAsync(host, planner =>
            new DeploymentPathPlanner(planner, docker, executor, DeploymentPath.AppDeploy).PlanAsync(file.Deployment(server.Id), ct));
    }

    /// <summary>Contract 0006 (review B1): the arguments of the nested exposure decision, accepted only from the UI and
    /// automation surfaces (<see cref="ComposeImpact.ExposureArguments"/>).</summary>
    internal static readonly IReadOnlyList<string> NestedDecisionArguments =
        ["exposure", "exposureShown", "exposureWithheld", "exposureValueWithheld", "exposureClasses", "related", "relatedWithheld"];

    /// <summary>What a <c>compose.deploy</c> from any surface but the UI and automation may not bring: the nested exposure
    /// decision's arguments and the nested mounts decision's <c>mounts</c> (review B1, N3).</summary>
    internal static readonly IReadOnlyList<string> RefusedFromCallers = [.. NestedDecisionArguments, "mounts"];

    private static string[] Lines(MutationRequest request, string argument) =>
        MutationTargets.String(request.Arguments, argument)?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <param name="named">The containers the cleanup removes by id (the scheduled cleanup lists them first); each
    /// is decided by id and name, and one that is no longer there refuses the run. Without a list every stopped
    /// container of the server is decided.</param>
    private async Task<TargetResolution> WithStoppedContainersAsync(MutationTarget host, IReadOnlyList<string>? named,
        CancellationToken ct)
    {
        if (_services.GetService<IDockerService>() is not { } docker)
            return Refuse("The containers a cleanup removes could not be checked: no container runtime is available.");
        IList<ContainerInfo> containers;
        try
        {
            containers = await docker.ListContainersAsync(all: true, serverId: host.ServerId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Refuse("The containers a cleanup removes could not be checked: the server's containers could not be listed.");
        }
        var stopped = containers.Where(c => !string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase)).ToList();
        if (named is not null)
        {
            var byId = containers.DistinctBy(c => c.Id).ToDictionary(c => c.Id, StringComparer.Ordinal);
            if (named.Any(id => !byId.ContainsKey(id)))
                return Refuse("A container the cleanup removes is no longer on the server.");
            stopped = named.Distinct(StringComparer.Ordinal).Select(id => byId[id]).ToList();
        }
        return Done(host with
        {
            Related = host.Related.Concat(stopped.SelectMany(c => new[] { c.Id, c.Name }))
                .Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            RelatedResources = host.RelatedResources.Concat(stopped.Select(c => (IReadOnlyList<string>)Aliases(c.Id, c.Name))).ToList(),
        });
    }

    /// <summary>The target with every container of the plan as related resource, or a refusal when the plan cannot
    /// be made (fail closed).</summary>
    private async Task<TargetResolution> WithPlanAsync(MutationTarget target, Func<IComposePlanner, Task<ComposePlan>> plan)
    {
        var planner = _services.GetService<IComposePlanner>();
        if (planner is null)
            return Refuse("The compose planner is not available.");
        try
        {
            var made = await plan(planner);
            // Contract 0006: every plan made here (deploy_compose, set_container_env) carries its exposures into the
            // target — the rule reads only these — and the containers it shares a namespace with into what it touches.
            var exposures = made.Exposures();
            return Done(target with
            {
                Related = target.Related.Concat(made.DecidedIdentities()).Concat(made.Mounts()).Concat(made.SharedIdentities())
                    .Distinct(StringComparer.Ordinal).ToList(),
                Withheld = target.Withheld.Concat(made.WithheldMounts()).Concat(made.WithheldSharedIdentities())
                    .Concat(exposures.Where(e => e.Withheld).Select(e => e.Identity)).ToHashSet(StringComparer.Ordinal),
                RelatedResources = target.RelatedResources.Concat(made.DecidedResources())
                    .Concat(made.SharedContainers.Select(c => (IReadOnlyList<string>)Aliases(c.Id, c.Name))).ToList(),
                Exposures = target.Exposures.Concat(exposures.Select(e => e.Identity)).Distinct(StringComparer.Ordinal).ToList(),
                ExposureKeysShown = target.ExposureKeysShown
                    .Concat(exposures.Where(e => e is { ValueWithheld: true, KeyWithheld: false }).Select(e => e.Identity))
                    .ToHashSet(StringComparer.Ordinal),
            });
        }
        catch (ComposePlanException ex)
        {
            return Refuse($"The compose deployment's impact cannot be determined: {ex.Message}");
        }
    }

    /// <summary>INV-59 (#187): whether a server already holds mTLS state that an onboarding would replace — TLS client
    /// material in its record, or an identity (active or pending) in the per-host credential store. A store that
    /// cannot be read counts as state: the onboarding then needs the approval (fail closed).</summary>
    private bool HasMtlsState(Models.ServerConfig server)
    {
        if (server.TcpUseTls || !string.IsNullOrWhiteSpace(server.TcpClientCertPath)
            || !string.IsNullOrWhiteSpace(server.TcpClientKeyPath) || !string.IsNullOrWhiteSpace(server.TcpCaCertPath))
            return true;
        try
        {
            return _services.GetService<Whiskers.Services.Certificates.IMtlsClientCredentialStore>()?.IdentityOf(server.Id)
                   is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    private TargetResolution Server(MutationTarget target, string? idOrName, bool mustExist, bool onboard = false)
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
                : Done(target with
                {
                    Id = existing.Id, Name = target.Name ?? existing.Name, ServerId = existing.Id,
                    HasControlPlaneState = onboard && HasMtlsState(existing),
                });
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

        // What the operation also touches, decided by the capability, never by which arguments the call happens to
        // carry: for connect / disconnect the container named in the call, resolved on the same server; for anything
        // else (removal) every attached container, which the removal cuts off.
        var related = new List<string>();
        var resources = new List<IReadOnlyList<string>>();
        string? relatedContainerId = null;
        if (!ContainerScoped.Contains(capability.Id))
        {
            related.AddRange(network.Containers.SelectMany(c => new[] { c.ContainerId, c.Name }));
            resources.AddRange(network.Containers.Select(c => Aliases(c.ContainerId, c.Name)));
        }
        else if (MutationTargets.String(request.Arguments, "containerId") is not { } containerArg)
            return Refuse("Container ID or name is required.");
        else
        {
            var containers = await docker.ListContainersAsync(all: true, serverId: server.Id, ct);
            var (container, notFound) = McpInputValidation.Resolve(containers, containerArg);
            if (container is null) return Refuse(notFound ?? $"Container not found: {containerArg}", containerArg);
            related.Add(container.Id);
            related.Add(container.Name);
            resources.Add(Aliases(container.Id, container.Name));
            relatedContainerId = container.Id;
        }
        return Done(target with
        {
            Id = network.Id, Name = network.Name, ServerId = server.Id,
            Related = related.Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            RelatedResources = resources.Where(g => g.Count > 0).ToList(),
            RelatedContainerId = relatedContainerId,
        });
    }

    /// <summary>Network capabilities that act on one container named in the call.</summary>
    private static readonly HashSet<string> ContainerScoped = new(StringComparer.Ordinal)
    {
        "network.connect", "network.disconnect",
    };

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

        // Live, not the label the page rendered: start / stop scale and restart roll the pod's CURRENT controller —
        // read here with its UID (contract 0012, D8), and acted on exactly as read: the provider reads nothing again.
        var identity = await factory.GetForServer(server.Id).GetWorkloadIdentityAsync(target.Id, ct);
        if (identity is null) return Refuse($"Workload not found: {target.Id}", target.Id);
        var pod = identity.Workload;
        var controller = identity.ControllerKind is "None" or "Container"
            ? pod.ComposeProject is { Length: > 0 } c && c != "Standalone" ? c : pod.Name
            : identity.ControllerName;
        // D9: a controller replaced under the same name has another UID; a bare pod (or a container) is its own UID.
        var fingerprint = identity.ControllerKind is "None"
            ? Hash(pod.Id, identity.PodUid)
            : Hash(identity.Namespace, identity.ControllerKind, identity.ControllerName, identity.ControllerUid);
        return Done(target with
        {
            Id = pod.Id, Name = controller, ServerId = server.Id,
            Related = new[] { pod.Id, pod.Name }.Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            Aliases = Aliases(pod.Name),
        }, t => new ResolvedWorkload(Token, t, fingerprint, pod.Id, identity.ControllerName, identity.Namespace,
            identity.ControllerKind, identity.ControllerUid, identity.PodUid));
    }

    /// <summary>
    /// A Git deployment app (R1-08): the stored app by its id, with its own name and server — never the ones the page
    /// rendered. A call that names a <c>revision</c> must meet the stored one, so a decision is never made on inputs
    /// that were edited meanwhile. Saving a new app resolves only the chosen server. A per-container decision inside a
    /// Git deployment (the plan's containers, R1-07) resolves that container.
    /// </summary>
    private async Task<TargetResolution> GitAppAsync(MutationRequest request, Capability capability, MutationTarget target,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.Id))
        {
            if (MutationTargets.String(request.Arguments, "containerId") is { } containerArg)
            {
                var resolved = await ContainerAsync(request, capability,
                    new MutationTarget("container", containerArg, null, target.ServerId), ct);
                return resolved.Target is { } c
                    ? Done(target with
                    {
                        Id = c.Id, Name = c.Name, ServerId = c.ServerId, Related = [c.Id, c.Name!],
                    })
                    : resolved;
            }
            if (capability.Id != "gitdeploy.save")
                return Refuse("The Git deployment app is required.");
            var (newServer, newError) = ResolveServer(target.ServerId);
            return newServer is null ? Refuse(newError!, target.ServerId) : Pass(target with { ServerId = newServer.Id });
        }

        var apps = _services.GetService<Whiskers.Services.GitDeploy.IGitDeployService>();
        if (apps is null)
            return Refuse("Git deployments are not available.");
        var app = await apps.GetAppAsync(target.Id);
        if (app is null)
            return Refuse($"Git deployment app not found: {target.Id}", target.Id);
        if (request.Arguments.TryGetValue("revision", out var revision)
            && (revision.ValueKind != System.Text.Json.JsonValueKind.Number || !revision.TryGetInt32(out var rev) || rev != app.Revision))
            return new(null, $"'{app.Name}' was changed after it was loaded; reload and try again.",
                MutationReasonCodes.TargetChanged, [app.Name]);

        // Saving may move the app to another server: decide on the server it will deploy to. Everything else acts
        // on the stored app, and a caller naming another server than the app's is refused.
        var serverId = capability.Id == "gitdeploy.save" ? target.ServerId ?? app.ServerId : app.ServerId;
        var (server, error) = ResolveServer(serverId);
        if (server is null) return Refuse(error!, serverId);
        if (capability.Id != "gitdeploy.save" && !string.IsNullOrWhiteSpace(target.ServerId)
            && ResolveServer(target.ServerId).Server?.Id != server.Id)
            return Refuse($"'{app.Name}' deploys to '{server.Id}', not to '{target.ServerId}'.");
        var revisionDecided = app.Revision;
        return Done(target with
        {
            Id = app.Id, Name = capability.Id == "gitdeploy.save" ? target.Name ?? app.Name : app.Name,
            ServerId = server.Id, Related = [app.Name, app.RepoUrl],
            // The stored name and the repository are the app's own, not another resource it acts on.
            Aliases = Aliases(app.Name, app.RepoUrl),
        }, t => new ResolvedGitApp(Token, t, Hash(app.Id, revisionDecided.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            revisionDecided));
    }

    /// <summary>
    /// A webhook (R1-09): the stored webhook by its id, with its own name and the server its action runs on — never
    /// the ones the page rendered. Creating a new one resolves only the chosen server. A webhook whose server was
    /// removed meanwhile can still be managed (switched off, deleted, taken over): only its record changes; its
    /// action is decided on the server again when it runs, and refused there. A new webhook for a Git deployment app
    /// (<c>appId</c>, contract 0001) is decided on the app's server, with the app as what it acts on.
    /// </summary>
    private async Task<TargetResolution> WebhookAsync(MutationRequest request, Capability capability, MutationTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Id))
        {
            if (capability.Id != "webhook.save")
                return Refuse("The webhook is required.");
            if (MutationTargets.String(request.Arguments, "appId") is { } appId)
            {
                var git = _services.GetService<Whiskers.Services.GitDeploy.IGitDeployService>();
                if (git is null)
                    return Refuse("Git deployments are not available.");
                var app = await git.GetAppAsync(appId);
                if (app is null)
                    return Refuse($"Git deployment app not found: {appId}", appId);
                var (appServer, appError) = ResolveServer(app.ServerId);
                if (appServer is null) return Refuse(appError!, app.ServerId);
                if (!string.IsNullOrWhiteSpace(target.ServerId) && ResolveServer(target.ServerId).Server?.Id != appServer.Id)
                    return Refuse($"'{app.Name}' deploys to '{appServer.Id}', not to '{target.ServerId}'.");
                return Pass(target with
                {
                    Name = target.Name ?? app.Name, ServerId = appServer.Id,
                    Related = [app.Name, app.RepoUrl],
                    RelatedResources = [Aliases(app.Name, app.RepoUrl)],
                });
            }
            var (newServer, newError) = ResolveServer(target.ServerId);
            return newServer is null ? Refuse(newError!, target.ServerId) : Pass(target with { ServerId = newServer.Id });
        }

        var webhooks = _services.GetService<Whiskers.Services.Webhooks.IWebhookService>();
        if (webhooks is null)
            return Refuse("Webhooks are not available.");
        var webhook = await webhooks.GetWebhookAsync(target.Id);
        if (webhook is null)
            return Refuse($"Webhook not found: {target.Id}", target.Id);
        var serverId = Whiskers.Services.Webhooks.WebhookTargets.ServerOf(webhook);
        var server = ResolveServer(serverId).Server;
        var fingerprint = WebhookFingerprint(webhook);
        return Done(target with
        {
            Id = webhook.WebhookId, Name = webhook.Name, ServerId = server?.Id ?? serverId,
            Related = new[] { webhook.Name, webhook.TargetId }.Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            Aliases = Aliases(webhook.Name),
            RelatedResources = new[] { Aliases(webhook.TargetId) }.Where(g => g.Count > 0).ToList(),
        }, t => new ResolvedWebhook(Token, t, fingerprint, null));
    }

    /// <summary>Contract 0012 (D9): what makes a webhook another one — its action, target, server, owner and switch.</summary>
    internal static string WebhookFingerprint(WebhookEntity webhook) =>
        Hash(webhook.WebhookId, webhook.Action, webhook.TargetType, webhook.TargetId, webhook.ServerId, webhook.Owner,
            webhook.Enabled ? "on" : "off");

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

    /// <summary>One resource's aliases, empty ones left out.</summary>
    private static IReadOnlyList<string> Aliases(params string?[] aliases) =>
        aliases.Where(a => !string.IsNullOrEmpty(a)).Select(a => a!).Distinct(StringComparer.Ordinal).ToList();

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
