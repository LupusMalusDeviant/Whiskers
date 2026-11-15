using Docker.DotNet;
using Docker.DotNet.Models;

namespace Whiskers.Services.Docker.Operations;

/// <summary>The Docker calls that put a container back after a failed recreate (<c>container.update</c>) or C12
/// rollback: find the container holding a name, remove the container the failed attempt created, give the previous one
/// its name back, start it. A seam so the restore order and its error reporting are testable without a Docker daemon
/// (<c>ContainerRestoreTests</c>).</summary>
internal interface IContainerRestoreSteps
{
    /// <summary>The container whose name is exactly <paramref name="name"/>, running or not, with its Docker state
    /// (<c>created</c>, <c>running</c>, <c>exited</c>, …); <c>null</c> when no container has that name.</summary>
    Task<(string Id, string State)?> FindByNameAsync(string name);
    /// <summary>Force-removes a container (killing it if it runs).</summary>
    Task RemoveAsync(string id);
    Task RenameAsync(string id, string newName);
    Task StartAsync(string id);
}

/// <summary>The production <see cref="IContainerRestoreSteps"/> over a <see cref="DockerClient"/>. The steps take no
/// cancellation: once a restore begins it runs to its end.</summary>
internal sealed class DockerClientContainerRestore(DockerClient client) : IContainerRestoreSteps
{
    public async Task<(string Id, string State)?> FindByNameAsync(string name)
    {
        // The daemon's name filter matches a pattern; the exact comparison below decides.
        var listed = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["name"] = new Dictionary<string, bool> { [name] = true },
            },
        }, CancellationToken.None);
        var hit = listed.FirstOrDefault(c => c.Names?.Any(n => n.TrimStart('/') == name) == true);
        return hit is null ? null : (hit.ID, hit.State ?? "");
    }

    public Task RemoveAsync(string id) =>
        client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);

    public Task RenameAsync(string id, string newName) =>
        client.Containers.RenameContainerAsync(id, new ContainerRenameParameters { NewName = newName }, CancellationToken.None);

    public Task StartAsync(string id) =>
        client.Containers.StartContainerAsync(id, new ContainerStartParameters(), CancellationToken.None);
}

/// <summary>Restores the previous container after a failed recreate or C12 rollback. Every method here either leaves
/// the previous container as it was before the attempt or throws an <see cref="InvalidOperationException"/> that says
/// what is left and under which name — never a log-only failure.</summary>
internal static class ContainerRestore
{
    /// <summary>
    /// Puts <paramref name="previousId"/> (renamed to <paramref name="stashedName"/> before the attempt) back under
    /// <paramref name="name"/> and starts it. The container the failed attempt created holds <paramref name="name"/>, so
    /// it is force-removed FIRST — renaming the previous one back while it exists answers 409 and left the service down
    /// (1.0.0 review, Fleet HIGH 4). When <paramref name="createdId"/> is <c>null</c> (the create call failed — possibly
    /// after the daemon made the container, its answer lost), the container holding the name is looked up and removed
    /// only if it is not the previous one and was never started (state <c>created</c>); a running or foreign container is
    /// never removed. Returns normally when the previous container runs under its name again. Otherwise it throws, naming
    /// the previous container's current name and whether it runs; the attempt's error and every restore error are inside.
    /// </summary>
    public static async Task RestorePreviousAsync(IContainerRestoreSteps steps, string? createdId, string previousId,
        string name, string stashedName, Exception failure, string operation)
    {
        var errors = new List<Exception>();
        var currentName = stashedName;
        var started = false;

        createdId ??= await FindLostCreateAsync(steps, previousId, name);

        var nameFree = true;
        if (createdId is not null)
        {
            try { await steps.RemoveAsync(createdId); }
            catch (Exception ex)
            {
                errors.Add(ex);
                nameFree = false;  // the failed container still holds the name; a rename would only add a 409
            }
        }

        if (nameFree)
        {
            try
            {
                await steps.RenameAsync(previousId, name);
                currentName = name;
            }
            catch (Exception ex) { errors.Add(ex); }
        }

        // Started even under its stashed name: a running previous container beats none. The failure is still reported.
        try
        {
            await steps.StartAsync(previousId);
            started = true;
        }
        catch (Exception ex) { errors.Add(ex); }

        if (errors.Count == 0)
            return;

        var leftover = createdId is not null && !nameFree
            ? $" The container the failed {operation} created ({Short(createdId)}) could not be removed and still holds the name '{name}'."
            : "";
        throw new InvalidOperationException(
            $"{operation} of '{name}' failed ({failure.Message}) and the rollback failed too: the previous container " +
            $"({Short(previousId)}) is now named '{currentName}' and is {(started ? "running" : "not running")}.{leftover} " +
            $"Rollback errors: {string.Join("; ", errors.Select(e => e.Message))}",
            new AggregateException([failure, .. errors]));
    }

    /// <summary>
    /// The stash rename (moving the previous container out of the way under <paramref name="stashedName"/>) failed, so
    /// nothing was created. If the attempt had stopped the container (<paramref name="wasStopped"/>), it is started
    /// again. Always throws: the attempt did not happen; the message says whether the container runs again.
    /// </summary>
    public static async Task RecoverFailedStashAsync(IContainerRestoreSteps steps, string previousId, string name,
        string stashedName, bool wasStopped, Exception failure, string operation)
    {
        var state = "it was not running before and was left as it was";
        Exception? startError = null;
        if (wasStopped)
        {
            try
            {
                await steps.StartAsync(previousId);
                state = "it was started again";
            }
            catch (Exception ex)
            {
                startError = ex;
                state = $"starting it again failed too ({ex.Message}), it is stopped";
            }
        }

        throw new InvalidOperationException(
            $"{operation} of '{name}' did not happen: the container ({Short(previousId)}) could not be renamed out of the " +
            $"way to '{stashedName}' ({failure.Message}). It keeps its name '{name}'; {state}.",
            startError is null ? failure : new AggregateException(failure, startError));
    }

    /// <summary>
    /// A C12 rollback with no previous container to restore: removes the container the failed rollback created, so a
    /// never-started container does not keep <paramref name="name"/>. Returns normally when it is gone (or none was
    /// created); throws when it could not be removed.
    /// </summary>
    public static async Task RemoveCreatedAsync(IContainerRestoreSteps steps, string? createdId, string name,
        Exception failure, string operation)
    {
        if (createdId is null)
            return;
        try { await steps.RemoveAsync(createdId); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{operation} of '{name}' failed ({failure.Message}) and the container it created ({Short(createdId)}) " +
                $"could not be removed ({ex.Message}); it holds the name '{name}' and is not running.",
                new AggregateException(failure, ex));
        }
    }

    // When the create call itself failed, the daemon may still have made the container. It is ours to remove only when
    // it is not the previous container and was never started; a lookup that fails is left to the rename to report.
    private static async Task<string?> FindLostCreateAsync(IContainerRestoreSteps steps, string previousId, string name)
    {
        try
        {
            var holder = await steps.FindByNameAsync(name);
            return holder is { } h && h.Id != previousId && string.Equals(h.State, "created", StringComparison.OrdinalIgnoreCase)
                ? h.Id
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Short(string id) => id.Length > 12 ? id[..12] : id;
}
