using System.Text.RegularExpressions;
using Whiskers.Models;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Docker;

/// <summary>
/// Read-backs of the network capabilities (R1-37, INV-43): after the call returned, the server's networks are listed
/// again. A network that is missing, still there, or without (or still with) the container fails the outcome.
/// </summary>
public abstract class NetworkVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    protected async Task<IList<NetworkInfo>> NetworksAsync(MutationContext context)
    {
        var server = RequireServer(context);
        return await Services.GetRequiredService<IDockerService>().ListNetworksAsync(server);
    }

    /// <summary>The network the call is about: the target the gateway resolved (its full id), else the arguments.</summary>
    protected static string Subject(MutationContext context) =>
        (context.Request.Target.Id is { Length: > 0 } id ? id : null)
        ?? VerifierSupport.Arg(context, "networkId")
        ?? VerifierSupport.Arg(context, "name")
        ?? throw new MutationNotVerifiableException("the call named no network to read back.");

    protected static NetworkInfo? Find(IEnumerable<NetworkInfo> networks, MutationContext context, string subject)
    {
        var name = context.Request.Target.Name ?? VerifierSupport.Arg(context, "name");
        return networks.FirstOrDefault(n =>
            n.Id == subject || n.Name == subject || (n.Id.Length > 0 && subject.Length >= 12 && n.Id.StartsWith(subject, StringComparison.Ordinal))
            || (name is not null && n.Name == name));
    }

    /// <summary>The container of a connect / disconnect: the one the gateway resolved, else the argument.</summary>
    protected static string Container(MutationContext context) =>
        context.Request.Target.RelatedContainerId
        ?? VerifierSupport.Arg(context, "containerId")
        ?? throw new MutationNotVerifiableException("the call named no container to read back.");

    protected static bool Attached(NetworkInfo network, string container) =>
        network.Containers.Any(c => c.ContainerId == container || c.Name == container
                                    || (container.Length >= 12 && c.ContainerId.StartsWith(container, StringComparison.Ordinal)));
}

/// <summary>network.create: the network is listed, with the driver the call asked for.</summary>
public sealed class NetworkCreateVerifier(IServiceProvider services) : NetworkVerifier(services)
{
    public override string CapabilityId => "network.create";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var name = VerifierSupport.Arg(context, "name") ?? context.Request.Target.Name ?? Subject(context);
        var network = (await NetworksAsync(context)).FirstOrDefault(n => n.Name == name)
                      ?? throw new MutationVerificationFailedException($"no network '{name}' is listed.");
        var driver = VerifierSupport.Arg(context, "driver") ?? "bridge";
        if (!string.Equals(network.Driver, driver, StringComparison.OrdinalIgnoreCase))
            throw new MutationVerificationFailedException($"network '{name}' uses the driver '{network.Driver}', not '{driver}'.");
    }
}

/// <summary>network.remove: the network is no longer listed.</summary>
public sealed class NetworkRemoveVerifier(IServiceProvider services) : NetworkVerifier(services)
{
    public override string CapabilityId => "network.remove";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var subject = Subject(context);
        if (Find(await NetworksAsync(context), context, subject) is { } still)
            throw new MutationVerificationFailedException($"network '{still.Name}' is still listed.");
    }
}

/// <summary>network.connect / network.disconnect: the container is attached to the network, or no longer.</summary>
public sealed class NetworkAttachVerifier(IServiceProvider services, bool connect) : NetworkVerifier(services)
{
    public override string CapabilityId => connect ? "network.connect" : "network.disconnect";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var subject = Subject(context);
        var container = Container(context);
        var network = Find(await NetworksAsync(context), context, subject)
                      ?? throw new MutationVerificationFailedException($"network '{subject}' is not listed.");
        if (Attached(network, container) != connect)
            throw new MutationVerificationFailedException(connect
                ? $"the container is not attached to network '{network.Name}'."
                : $"the container is still attached to network '{network.Name}'.");
    }
}

/// <summary>compose.deploy: every container of the services the deployed files define, in the project the plan resolved,
/// exists and runs (a one-shot container may have exited
/// with 0). Calls that name no project — a page's nested calls per colliding container or for its mounts — are not
/// verifiable on their own: the deployment they belong to is.</summary>
public sealed partial class ComposeDeployVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "compose.deploy";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        // The project the plan resolved, when the call reported it; else the one the call named.
        var project = VerifierSupport.Fact(context, ComposeImpact.ProjectFact) ?? VerifierSupport.Arg(context, "projectName");
        if (project is "")
            throw new MutationNotVerifiableException("the project name is interpolated; its value is not kept to read back.");
        if (string.IsNullOrWhiteSpace(project))
            throw new MutationNotVerifiableException("the call named no compose project to read back.");
        // An interpolated name is recorded as its expression (never its value), and compose rewrites names outside its
        // charset: neither is the label to look for.
        if (!ComposeProjectName().IsMatch(project))
            throw new MutationNotVerifiableException("the project name is not the one compose labels its containers with.");
        var server = RequireServer(context);
        var services = VerifierSupport.Fact(context, ComposeImpact.ServicesFact) is { Length: > 0 } listed
            ? listed.Split('\n').ToHashSet(StringComparer.Ordinal)
            : null;
        // Compose lower-cases the project name it labels its containers with.
        var containers = (await Services.GetRequiredService<IDockerService>().ListContainersAsync(true, server, ct))
            // The label itself: ComposeProject answers "Standalone" for a container without one.
            .Where(c => c.Labels.TryGetValue(ProjectLabel, out var label) && string.Equals(label, project, StringComparison.OrdinalIgnoreCase))
            // An orphan of a service the files no longer define is not started by `up`: only the deployed services count.
            .Where(c => services is null || (c.Labels.TryGetValue(ServiceLabel, out var service) && services.Contains(service)))
            .ToList();
        if (containers.Count == 0)
            throw new MutationVerificationFailedException($"compose project '{project}' has no container.");
        var down = containers.FirstOrDefault(c => !Up(c));
        if (down is not null)
            throw new MutationVerificationFailedException($"container '{down.Name}' of '{project}' is '{down.Status}'.");
    }

    private const string ProjectLabel = "com.docker.compose.project";
    private const string ServiceLabel = "com.docker.compose.service";

    // Compose project names: letters (compose lower-cases them), digits, dash and underscore, starting with a letter or digit.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
    private static partial Regex ComposeProjectName();

    private static bool Up(ContainerInfo c) =>
        string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(c.State, "exited", StringComparison.OrdinalIgnoreCase)
            && c.Status.StartsWith("Exited (0)", StringComparison.OrdinalIgnoreCase));
}
