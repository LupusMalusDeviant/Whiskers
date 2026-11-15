using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Whiskers.Services.Docker.Operations;

/// <summary>A container as the restore sees it: id, Docker state (<c>created</c>, <c>running</c>, <c>exited</c>, …) and
/// the image it was created from, as the daemon lists it.</summary>
internal sealed record RestoreContainer(string Id, string State, string Image);

/// <summary>The Docker calls that put a container back after a failed recreate (<c>container.update</c>) or C12
/// rollback: find the container holding a name, read a container's state, remove the container the failed attempt
/// created, give the previous one its name back, start it. A seam so the restore order and its error reporting are
/// testable without a Docker daemon (<c>ContainerRestoreTests</c>).</summary>
internal interface IContainerRestoreSteps
{
    /// <summary>The container whose name is exactly <paramref name="name"/>, running or not; <c>null</c> when no
    /// container has that name.</summary>
    Task<RestoreContainer?> FindByNameAsync(string name);
    /// <summary>The Docker state of container <paramref name="id"/> (<c>created</c>, <c>running</c>, <c>exited</c>, …).</summary>
    Task<string> GetStateAsync(string id);
    /// <summary>Force-removes a container (killing it if it runs).</summary>
    Task RemoveAsync(string id);
    /// <summary>Removes a container WITHOUT force: the daemon itself refuses a running (or paused, restarting) one with
    /// 409, so a container that started after its state was read is never killed.</summary>
    Task RemoveIfStoppedAsync(string id);
    Task RenameAsync(string id, string newName);
    Task StartAsync(string id);
}

/// <summary>The production <see cref="IContainerRestoreSteps"/> over a <see cref="DockerClient"/>. The steps take no
/// cancellation: once a restore begins it runs to its end.</summary>
internal sealed class DockerClientContainerRestore(DockerClient client) : IContainerRestoreSteps
{
    public async Task<RestoreContainer?> FindByNameAsync(string name)
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
        return hit is null ? null : new RestoreContainer(hit.ID, hit.State ?? "", hit.Image ?? "");
    }

    public async Task<string> GetStateAsync(string id) =>
        (await client.Containers.InspectContainerAsync(id, CancellationToken.None)).State?.Status ?? "";

    public Task RemoveAsync(string id) =>
        client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);

    public Task RemoveIfStoppedAsync(string id) =>
        client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = false }, CancellationToken.None);

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
    /// (1.0.0 review, Fleet HIGH 4). When <paramref name="createdId"/> is <c>null</c> (the create call failed), a container
    /// the daemon may still have made is looked up under the guards of <see cref="FindLostCreateAsync"/>.
    /// Returns normally when the previous container runs under its name again. Otherwise it throws, naming the previous
    /// container's current name and whether it runs; the attempt's error and every restore error are inside.
    /// </summary>
    public static async Task RestorePreviousAsync(IContainerRestoreSteps steps, string? createdId, string previousId,
        string name, string stashedName, string createImage, Exception failure, string operation)
    {
        var errors = new List<Exception>();
        var currentName = stashedName;
        var started = false;

        // A known created id is this attempt's own container and is force-removed. One found by the lookup is removed
        // without force, so the daemon refuses it (409) should it have started meanwhile.
        var found = createdId is null;
        createdId ??= await FindLostCreateAsync(steps, previousId, name, createImage, failure);

        var nameFree = true;
        var leftover = "";
        if (createdId is not null)
        {
            try
            {
                if (found)
                    await steps.RemoveIfStoppedAsync(createdId);
                else
                    await steps.RemoveAsync(createdId);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
                nameFree = false;  // the failed container still holds the name; a rename would only add a 409
                leftover = IsConflict(ex) && found
                    ? $" The container holding '{name}' ({Short(createdId)}) was kept: the daemon refused to remove it, it runs."
                    : $" The container the failed {operation} created ({Short(createdId)}) could not be removed and still holds the name '{name}'.";
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

        throw new InvalidOperationException(
            $"{operation} of '{name}' failed ({failure.Message}) and the rollback failed too: the previous container " +
            $"({Short(previousId)}) is now named '{currentName}' and is {(started ? "running" : "not running")}.{leftover} " +
            $"Rollback errors: {string.Join("; ", errors.Select(e => e.Message))}",
            new AggregateException([failure, .. errors]));
    }

    /// <summary>
    /// The stash rename (moving the previous container out of the way under <paramref name="stashedName"/>) failed, so
    /// nothing was created. If the container ran before the attempt (<paramref name="wasRunning"/>), it is started
    /// again. Always throws: the attempt did not happen; the message says whether the container runs again and, as far
    /// as the daemon tells, under which name.
    /// </summary>
    public static async Task RecoverFailedStashAsync(IContainerRestoreSteps steps, string previousId, string name,
        string stashedName, bool wasRunning, Exception failure, string operation)
    {
        var state = "it was not running before and was left as it was";
        Exception? startError = null;
        if (wasRunning)
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

        // A rename that answered with an error may still have taken effect: say which name it has, as the daemon lists it.
        var naming = await NameOfAsync(steps, previousId, name, stashedName) switch
        {
            { } current => $"It is named '{current}'",
            null => $"Its current name could not be determined (it was '{name}')",
        };

        throw new InvalidOperationException(
            $"{operation} of '{name}' did not happen: the container ({Short(previousId)}) could not be renamed out of the " +
            $"way to '{stashedName}' ({failure.Message}). {naming}; {state}.",
            startError is null ? failure : new AggregateException(failure, startError));
    }

    /// <summary>
    /// A C12 rollback with no previous container to restore: removes the container the failed rollback created, so a
    /// never-started container does not keep <paramref name="name"/>. When <paramref name="createdId"/> is <c>null</c> a
    /// container the daemon may still have made is looked up under the guards of <see cref="FindLostCreateAsync"/>.
    /// Only a container in a state that is allowed (<c>created</c>, <c>exited</c>, <c>dead</c>) is removed, and without
    /// force, so the daemon refuses (409) one that started after its state was read. Anything else — running (its start
    /// reported a failure but took effect), paused, restarting, an unreadable or unknown state — is kept, and this throws
    /// with its state. Returns normally when it is gone or none was created.
    /// </summary>
    public static async Task RemoveCreatedAsync(IContainerRestoreSteps steps, string? createdId, string name,
        string createImage, Exception failure, string operation)
    {
        createdId ??= await FindLostCreateAsync(steps, null, name, createImage, failure);
        if (createdId is null)
            return;

        string state;
        try { state = await steps.GetStateAsync(createdId); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{operation} of '{name}' failed ({failure.Message}); the container it created ({Short(createdId)}) was " +
                $"kept because its state could not be read ({ex.Message}). It holds the name '{name}'.",
                new AggregateException(failure, ex));
        }

        if (!RemovableStates.Contains(state))
            throw new InvalidOperationException(
                $"{operation} of '{name}' reported a failure ({failure.Message}), but the container it created " +
                $"({Short(createdId)}) is {(state.Length == 0 ? "in an unknown state" : $"'{state}'")}; it was kept under the name '{name}'.",
                failure);

        try { await steps.RemoveIfStoppedAsync(createdId); }
        catch (Exception ex) when (IsConflict(ex))
        {
            throw new InvalidOperationException(
                $"{operation} of '{name}' reported a failure ({failure.Message}), but the daemon refused to remove the " +
                $"container it created ({Short(createdId)}): it runs. It was kept under the name '{name}'.",
                new AggregateException(failure, ex));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{operation} of '{name}' failed ({failure.Message}) and the container it created ({Short(createdId)}) " +
                $"could not be removed ({ex.Message}); it holds the name '{name}' and is {state}.",
                new AggregateException(failure, ex));
        }
    }

    /// <summary>Whether <paramref name="failure"/> is the daemon's definite refusal (a 4xx answer, such as 409 for a
    /// name another container holds): then the create made nothing, and nothing may be looked up to remove.</summary>
    internal static bool IsDefiniteRejection(Exception failure) =>
        failure is DockerApiException { StatusCode: var status } && (int)status is >= 400 and < 500;

    /// <summary>The daemon's 409 — for a non-forced remove: the container runs.</summary>
    private static bool IsConflict(Exception ex) => ex is DockerApiException { StatusCode: HttpStatusCode.Conflict };

    /// <summary>States in which a container this attempt created may be removed: never started, or stopped.</summary>
    private static readonly HashSet<string> RemovableStates = new(StringComparer.OrdinalIgnoreCase) { "created", "exited", "dead" };

    /// <summary>Whether a container in the listed Docker <paramref name="state"/> was running for the purpose of starting
    /// it again: <c>running</c>, <c>paused</c> and <c>restarting</c> all hold a process the attempt's stop ended.</summary>
    internal static bool WasRunning(string? state) =>
        state is not null && (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(state, "paused", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(state, "restarting", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// When the create call failed without a definite refusal (a timeout, a dropped tunnel, a 5xx), the daemon may still
    /// have made the container. The container holding <paramref name="name"/> is taken for it only when it is not the
    /// previous one, was created from <paramref name="createImage"/>, and was never started (state <c>created</c>);
    /// anything else — someone else's container, a running or exited one — is never ours to remove. A lookup that fails
    /// is left to the next step to report.
    /// </summary>
    private static async Task<string?> FindLostCreateAsync(IContainerRestoreSteps steps, string? previousId, string name,
        string createImage, Exception failure)
    {
        if (IsDefiniteRejection(failure))
            return null;
        try
        {
            var holder = await steps.FindByNameAsync(name);
            return holder is not null
                   && holder.Id != previousId
                   && string.Equals(holder.State, "created", StringComparison.OrdinalIgnoreCase)
                   // The image as the daemon lists it: the tag the recreate created from, or the sha256 id the C12
                   // rollback created from. A listing that shows it differently (a moved tag shown as an id) is a
                   // mismatch, and a mismatch fails safe: the container is left alone.
                   && string.Equals(holder.Image, createImage, StringComparison.Ordinal)
                ? holder.Id
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> NameOfAsync(IContainerRestoreSteps steps, string id, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if ((await steps.FindByNameAsync(candidate))?.Id == id)
                    return candidate;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    internal static string Short(string? id) => id is null ? "" : id.Length > 12 ? id[..12] : id;
}
