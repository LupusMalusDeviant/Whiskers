using Whiskers.Models;

namespace Whiskers.Services.Workloads;

/// <summary>In-memory <see cref="IWorkloadProvider"/> for unit tests and the demo mode
/// (outOfTheBox W4): seeded workloads, start/stop/restart mutate the in-memory state, logs return
/// a canned line per call. Thread-safety: coarse lock — this is a test/demo double, not a service.</summary>
public sealed class FakeWorkloadProvider : IWorkloadProvider
{
    private readonly object _gate = new();
    private readonly List<ContainerInfo> _workloads;
    public List<string> Calls { get; } = new();

    public FakeWorkloadProvider(string serverId, IEnumerable<ContainerInfo>? seed = null,
        WorkloadCapabilities? capabilities = null)
    {
        ServerId = serverId;
        _workloads = seed?.ToList() ?? new List<ContainerInfo>();
        Capabilities = capabilities ?? WorkloadCapabilities.Docker;
    }

    public string ServerId { get; }
    public WorkloadCapabilities Capabilities { get; }

    public Task<IList<ContainerInfo>> ListWorkloadsAsync(bool all = true, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls.Add($"list:{all}");
            IList<ContainerInfo> result = _workloads
                .Where(w => all || w.State == "running")
                .Select(Clone)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<ContainerInfo?> GetWorkloadAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls.Add($"get:{id}");
            return Task.FromResult(Find(id) is { } w ? Clone(w) : null);
        }
    }

    public Task StartAsync(string id, CancellationToken ct = default) => Mutate(id, "start", "running");
    public Task StopAsync(string id, CancellationToken ct = default) => Mutate(id, "stop", "exited");

    public Task RestartAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls.Add($"restart:{id}");
            _ = Find(id) ?? throw new InvalidOperationException($"Workload '{id}' not found");
            return Task.CompletedTask;
        }
    }

    public Task<string> GetLogsAsync(string id, int tailLines = 100, DateTime? since = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls.Add($"logs:{id}:{tailLines}");
            return Task.FromResult($"[fake] logs for {id} (tail {tailLines})\n");
        }
    }

    public Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Calls.Add($"stats:{id}");
            return Task.FromResult(Capabilities.SupportsStats && Find(id) is not null
                ? new ContainerStats { ContainerId = id, CpuPercent = 1.5, MemoryUsageBytes = 64 * 1024 * 1024 }
                : (ContainerStats?)null);
        }
    }

    private Task Mutate(string id, string call, string newState)
    {
        lock (_gate)
        {
            Calls.Add($"{call}:{id}");
            var w = Find(id) ?? throw new InvalidOperationException($"Workload '{id}' not found");
            w.State = newState;
            return Task.CompletedTask;
        }
    }

    private ContainerInfo? Find(string id) =>
        _workloads.FirstOrDefault(w => w.Id == id || w.Name == id || w.Id.StartsWith(id));

    private static ContainerInfo Clone(ContainerInfo w) => new()
    {
        Id = w.Id, Name = w.Name, Image = w.Image, Status = w.Status, State = w.State,
        Created = w.Created, HealthStatus = w.HealthStatus,
        Labels = new Dictionary<string, string>(w.Labels),
        Ports = w.Ports.ToList(), ServerId = w.ServerId, ServerName = w.ServerName,
    };
}
