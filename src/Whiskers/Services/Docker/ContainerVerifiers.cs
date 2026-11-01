using System.Text.Json;
using Docker.DotNet;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Docker;

/// <summary>
/// Read-backs of the container capabilities (R1-36, INV-43/44): after the call returned, the container is inspected
/// and must be in the state the call promised. A container that is not there, in another state or killed by the OOM
/// killer fails the outcome — the call completed, its effect did not. The docker service is resolved when a
/// verification runs: it is itself guarded by the gateway that holds these verifiers.
/// </summary>
public abstract class ContainerVerifier(IServiceProvider services) : IMutationVerifier
{
    public abstract string CapabilityId { get; }

    public async Task<VerificationStatus> VerifyAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var docker = services.GetRequiredService<IDockerService>();
        var (container, serverId) = Subject(context, result);
        if (string.IsNullOrWhiteSpace(container))
        {
            if (UnnamedIsNotVerifiable) return VerificationStatus.NotVerifiable;
            throw new MutationVerificationFailedException("the call named no container to read back.");
        }
        await CheckAsync(docker, container, serverId, ct);
        return VerificationStatus.Verified;
    }

    /// <summary>The container to read back and its server: the target the gateway resolved, else the arguments.</summary>
    protected virtual (string? Container, string? ServerId) Subject(MutationContext context, object? result)
    {
        var target = context.Request.Target;
        var id = !string.IsNullOrWhiteSpace(target.Id) ? target.Id : Argument(context, "containerId");
        return (id, target.ServerId ?? Argument(context, "serverId"));
    }

    protected abstract Task CheckAsync(IDockerService docker, string container, string? serverId, CancellationToken ct);

    /// <summary>A call that names no container (a rollback by its snapshot id) records NotVerifiable instead of
    /// Failed; set only where such a call exists.</summary>
    protected virtual bool UnnamedIsNotVerifiable => false;

    /// <summary>The container's state. A read that the server's budget turned away (a duplicate of a read in flight, a
    /// full queue) is tried again: it says nothing about the container.</summary>
    protected static async Task<(string State, int ExitCode, bool OomKilled)> InspectAsync(IDockerService docker,
        string container, string? serverId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await docker.InspectContainerStateAsync(container, serverId);
            }
            catch (Exception ex) when (attempt < 3
                                       && ex is Budget.DuplicateRequestException or Budget.BudgetSaturatedException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }
    }

    protected static string? Argument(MutationContext context, string name)
        => context.Request.Arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A container id a call returned (a recreate or a create answers with the new one).</summary>
    protected static string? ReturnedId(object? result)
        => result is string s && s.Length is >= 12 and <= 64 && s.All(Uri.IsHexDigit) ? s : null;

    protected static async Task RequireRunningAsync(IDockerService docker, string container, string? serverId,
        CancellationToken ct, bool finishedCounts = false)
    {
        var (state, exitCode, oomKilled) = await InspectAsync(docker, container, serverId, ct);
        // A one-shot container (a migration, a backup) that already ran to completion did start.
        if (finishedCounts && !oomKilled && exitCode == 0 && string.Equals(state, "exited", StringComparison.OrdinalIgnoreCase))
            return;
        if (oomKilled)
            throw new MutationVerificationFailedException($"the container was killed for lack of memory (state {state}).");
        if (!string.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
            throw new MutationVerificationFailedException($"the container is '{state}' (exit code {exitCode}), not running.");
    }
}

/// <summary>container.start: the container runs — or, a one-shot container, already ran to completion (exit code 0).</summary>
public sealed class ContainerStartVerifier(IServiceProvider services) : ContainerVerifier(services)
{
    public override string CapabilityId => "container.start";

    protected override Task CheckAsync(IDockerService docker, string container, string? serverId, CancellationToken ct)
        => RequireRunningAsync(docker, container, serverId, ct, finishedCounts: true);
}

/// <summary>container.restart: the container runs again — or, a one-shot container, already ran to completion.</summary>
public sealed class ContainerRestartVerifier(IServiceProvider services) : ContainerVerifier(services)
{
    public override string CapabilityId => "container.restart";

    protected override Task CheckAsync(IDockerService docker, string container, string? serverId, CancellationToken ct)
        => RequireRunningAsync(docker, container, serverId, ct, finishedCounts: true);
}

/// <summary>container.stop: the container no longer runs (stopped, created or dead — not running, restarting or
/// paused).</summary>
public sealed class ContainerStopVerifier(IServiceProvider services) : ContainerVerifier(services)
{
    public override string CapabilityId => "container.stop";

    protected override async Task CheckAsync(IDockerService docker, string container, string? serverId, CancellationToken ct)
    {
        var (state, _, _) = await InspectAsync(docker, container, serverId, ct);
        if (state is not ("exited" or "created" or "dead"))
            throw new MutationVerificationFailedException($"the container is still '{state}'.");
    }
}

/// <summary>container.remove: the container is gone.</summary>
public sealed class ContainerRemoveVerifier(IServiceProvider services) : ContainerVerifier(services)
{
    public override string CapabilityId => "container.remove";

    protected override async Task CheckAsync(IDockerService docker, string container, string? serverId, CancellationToken ct)
    {
        string state;
        try
        {
            (state, _, _) = await InspectAsync(docker, container, serverId, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            return;
        }
        throw new MutationVerificationFailedException($"the container still exists ('{state}').");
    }
}

/// <summary>container.update: the recreated container — the id the recreate returned, else the container of the same
/// name — runs.</summary>
public sealed class ContainerUpdateVerifier(IServiceProvider services) : ContainerVerifier(services)
{
    public override string CapabilityId => "container.update";

    protected override (string? Container, string? ServerId) Subject(MutationContext context, object? result)
    {
        var (old, serverId) = base.Subject(context, result);
        // The old id is gone after a recreate; the name stays.
        var name = context.Request.Target.Name ?? Argument(context, "name");
        return (ReturnedId(result) ?? name ?? old, serverId);
    }

    protected override Task CheckAsync(IDockerService docker, string container, string? serverId, CancellationToken ct)
        => RequireRunningAsync(docker, container, serverId, ct);
}

/// <summary>container.create / app.deploy (a single-container deployment): the container the call returned runs. A
/// deployment whose answer names no container (the MCP tool answers with text) records NotVerifiable; compose
/// deployments are R1-37.</summary>
public sealed class ContainerCreateVerifier(IServiceProvider services, string capabilityId = "container.create")
    : ContainerVerifier(services)
{
    public override string CapabilityId => capabilityId;

    protected override bool UnnamedIsNotVerifiable => true;

    protected override (string? Container, string? ServerId) Subject(MutationContext context, object? result)
        => (ReturnedId(result), context.Request.Target.ServerId ?? Argument(context, "serverId"));

    protected override Task CheckAsync(IDockerService docker, string container, string? serverId, CancellationToken ct)
        => RequireRunningAsync(docker, container, serverId, ct);
}

/// <summary>container.rollback: the container of the snapshot's name runs again on the previous image.</summary>
public sealed class ContainerRollbackVerifier(IServiceProvider services) : ContainerVerifier(services)
{
    public override string CapabilityId => "container.rollback";

    // The page's rollback names only the snapshot; the docker call inside it names the container.
    protected override bool UnnamedIsNotVerifiable => true;

    protected override (string? Container, string? ServerId) Subject(MutationContext context, object? result)
        => (ReturnedId(result) ?? Argument(context, "containerName") ?? context.Request.Target.Name,
            context.Request.Target.ServerId ?? Argument(context, "serverId"));

    protected override Task CheckAsync(IDockerService docker, string container, string? serverId, CancellationToken ct)
        => RequireRunningAsync(docker, container, serverId, ct);
}
