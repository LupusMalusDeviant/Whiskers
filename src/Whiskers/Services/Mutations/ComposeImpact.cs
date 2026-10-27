using Whiskers.Models;
using Whiskers.Services.Docker;

namespace Whiskers.Services.Mutations;

/// <summary>
/// What a <c>docker compose up</c> touches: every existing container of the project. A decision on the project name
/// or directory alone never meets a protected container whose name differs from it, so the UI decides once per
/// affected container as well (nested inside the deploy's own decision).
/// </summary>
public static class ComposeImpact
{
    private const string WorkingDirLabel = "com.docker.compose.project.working_dir";

    /// <summary>The containers (running or not) on <paramref name="serverId"/> that belong to compose project
    /// <paramref name="project"/> or were started from <paramref name="workingDir"/>. Pass an explicit server id:
    /// <c>null</c> lists the default server.</summary>
    public static async Task<IReadOnlyList<ContainerInfo>> ContainersAsync(IDockerService docker, string serverId,
        string? project, string? workingDir)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var dir = workingDir?.TrimEnd('/');
        return (await docker.ListContainersAsync(all: true, serverId: serverId))
            .Where(c =>
                (!string.IsNullOrWhiteSpace(project) && c.ComposeProject != "Standalone"
                    && string.Equals(c.ComposeProject, project, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(dir) && c.Labels.TryGetValue(WorkingDirLabel, out var wd)
                    && string.Equals(wd.TrimEnd('/'), dir, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>Runs <paramref name="action"/> inside one <paramref name="capabilityId"/> decision per container
    /// (nested), so a single refusal — a protected container, a stricter override — stops the whole action.</summary>
    public static Task<T> RunForEachAsync<T>(this IUiMutationRunner runner, string capabilityId,
        IReadOnlyList<ContainerInfo> containers, string serverId, Func<Task<T>> action) =>
        Next(runner, capabilityId, containers, serverId, action, 0);

    private static Task<T> Next<T>(IUiMutationRunner runner, string capabilityId, IReadOnlyList<ContainerInfo> containers,
        string serverId, Func<Task<T>> action, int index) =>
        index >= containers.Count
            ? action()
            : runner.RunAsync(capabilityId,
                new { containerId = containers[index].Id, name = containers[index].Name, serverId },
                _ => Next(runner, capabilityId, containers, serverId, action, index + 1));
}
