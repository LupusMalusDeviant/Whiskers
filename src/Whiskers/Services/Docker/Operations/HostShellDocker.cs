using Docker.DotNet;
using Docker.DotNet.Models;

namespace Whiskers.Services.Docker.Operations;

/// <summary>The Docker calls the SSH-free host shell makes, one per step (R1-57). <see cref="HostShellOperations"/> puts
/// each under the run's deadline; this seam keeps the deadline logic independent of the Docker client library and lets
/// tests stall any single step.</summary>
internal interface IHostShellDocker
{
    /// <summary>Whether <paramref name="image"/> is present on the host.</summary>
    Task<bool> HasImageAsync(string image, CancellationToken ct);
    Task PullImageAsync(string repository, string tag, CancellationToken ct);
    /// <summary>Creates the helper; returns its id.</summary>
    Task<string> CreateAsync(CreateContainerParameters parameters, CancellationToken ct);
    Task StartAsync(string id, CancellationToken ct);
    /// <summary>Waits for the helper to exit; returns its exit code.</summary>
    Task<long> WaitAsync(string id, CancellationToken ct);
    Task<(string Output, string Error)> ReadLogsAsync(string id, CancellationToken ct);
    Task KillAsync(string id, CancellationToken ct);
    Task RemoveAsync(string id, CancellationToken ct);
    /// <summary>Non-running helpers carrying <paramref name="label"/>, with when they were created.</summary>
    Task<IReadOnlyList<(string Id, DateTime Created)>> ListStoppedAsync(string label, CancellationToken ct);
}

/// <summary>The production <see cref="IHostShellDocker"/>: a <see cref="DockerClient"/> from the connection manager.</summary>
internal sealed class DockerClientHostShell(DockerClient client) : IHostShellDocker
{
    public async Task<bool> HasImageAsync(string image, CancellationToken ct)
    {
        try
        {
            await client.Images.InspectImageAsync(image, ct);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
    }

    public Task PullImageAsync(string repository, string tag, CancellationToken ct) =>
        client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = repository, Tag = tag },
            null, new Progress<JSONMessage>(), ct);

    public async Task<string> CreateAsync(CreateContainerParameters parameters, CancellationToken ct) =>
        (await client.Containers.CreateContainerAsync(parameters, ct)).ID;

    public Task StartAsync(string id, CancellationToken ct) =>
        client.Containers.StartContainerAsync(id, new ContainerStartParameters(), ct);

    public async Task<long> WaitAsync(string id, CancellationToken ct) =>
        (await client.Containers.WaitContainerAsync(id, ct)).StatusCode;

    public async Task<(string Output, string Error)> ReadLogsAsync(string id, CancellationToken ct)
    {
        using var mux = await client.Containers.GetContainerLogsAsync(id, false,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true }, ct);
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        await mux.CopyOutputToAsync(Stream.Null, stdout, stderr, ct);
        stdout.Position = 0;
        stderr.Position = 0;
        return (await new StreamReader(stdout).ReadToEndAsync(ct), await new StreamReader(stderr).ReadToEndAsync(ct));
    }

    public Task KillAsync(string id, CancellationToken ct) =>
        client.Containers.KillContainerAsync(id, new ContainerKillParameters(), ct);

    public Task RemoveAsync(string id, CancellationToken ct) =>
        client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, ct);

    public async Task<IReadOnlyList<(string Id, DateTime Created)>> ListStoppedAsync(string label, CancellationToken ct)
    {
        var list = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [$"{label}=1"] = true },
                ["status"] = new Dictionary<string, bool>
                {
                    ["created"] = true, ["exited"] = true, ["dead"] = true
                },
            }
        }, ct);
        return list.Select(c => (c.ID, c.Created)).ToList();
    }
}
