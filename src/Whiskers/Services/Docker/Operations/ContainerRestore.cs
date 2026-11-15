using Docker.DotNet;
using Docker.DotNet.Models;

namespace Whiskers.Services.Docker.Operations;

/// <summary>The Docker calls that put a container back after a failed recreate (<c>container.update</c>) or C12
/// rollback: remove the container the failed attempt created, give the previous one its name back, start it. A seam so
/// the restore order and its error reporting are testable without a Docker daemon (<c>ContainerRestoreTests</c>).</summary>
internal interface IContainerRestoreSteps
{
    /// <summary>Force-removes a container (killing it if it runs).</summary>
    Task RemoveAsync(string id);
    Task RenameAsync(string id, string newName);
    Task StartAsync(string id);
}

/// <summary>The production <see cref="IContainerRestoreSteps"/> over a <see cref="DockerClient"/>. The steps take no
/// cancellation: once a restore begins it runs to its end.</summary>
internal sealed class DockerClientContainerRestore(DockerClient client) : IContainerRestoreSteps
{
    public Task RemoveAsync(string id) =>
        client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);

    public Task RenameAsync(string id, string newName) =>
        client.Containers.RenameContainerAsync(id, new ContainerRenameParameters { NewName = newName }, CancellationToken.None);

    public Task StartAsync(string id) =>
        client.Containers.StartContainerAsync(id, new ContainerStartParameters(), CancellationToken.None);
}

/// <summary>Restores the previous container after a failed recreate or C12 rollback.</summary>
internal static class ContainerRestore
{
    /// <summary>
    /// Puts <paramref name="previousId"/> (renamed to <paramref name="stashedName"/> before the attempt) back under
    /// <paramref name="name"/> and starts it. The container the failed attempt created (<paramref name="createdId"/>,
    /// <c>null</c> when the create itself failed) holds <paramref name="name"/>, so it is force-removed FIRST — renaming
    /// the previous one back while it exists answers 409 and left the service down (1.0.0 review, Fleet HIGH 4).
    /// Returns normally when the previous container runs under its name again. Otherwise it throws an
    /// <see cref="InvalidOperationException"/> that says the rollback failed and names the previous container's current
    /// name; the attempt's error and every restore error are inside it. Never a log-only failure.
    /// </summary>
    public static async Task RestorePreviousAsync(IContainerRestoreSteps steps, string? createdId, string previousId,
        string name, string stashedName, Exception failure, string operation)
    {
        var errors = new List<Exception>();
        var currentName = stashedName;
        var started = false;

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

    private static string Short(string id) => id.Length > 12 ? id[..12] : id;
}
