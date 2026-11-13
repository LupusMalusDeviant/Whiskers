using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.Cve;
using Whiskers.Services.Docker;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests;

/// <summary>
/// Fable review H4: a server whose container listing failed or timed out must keep its stored container findings. The
/// plain fleet listing showed such a server as one without containers, and the prune then dropped all of them — their
/// ages started over and every finding was reported as new on the next good scan.
/// </summary>
public class CveListingFailureTests
{
    public class ScriptedProxy<T> : NullProxy<T> where T : class
    {
        public Dictionary<string, Func<object?[]?, object?>> Handlers { get; set; } = new();

        public static T Create(Dictionary<string, Func<object?[]?, object?>> handlers)
        {
            var proxy = DispatchProxy.Create<T, ScriptedProxy<T>>();
            ((ScriptedProxy<T>)(object)proxy).Handlers = handlers;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            Handlers.TryGetValue(method!.Name, out var handler) ? handler(args) : base.Invoke(method, args);
    }

    private sealed class RecordingStore : ICveFindingsStore
    {
        public List<string> Pruned { get; } = [];
        public List<CveScanResult> Stored { get; } = [];
        public bool IsScanning { get; set; }
        public DateTime? LastScanAt { get; set; }
        public void Set(CveScanResult result) => Stored.Add(result);
        public CveScanResult? Get(string serverId, string? containerId) => null;
        public IReadOnlyList<CveScanResult> GetForServer(string serverId) => [];
        public IReadOnlyList<CveScanResult> GetAll() => [];
        public void Remove(string serverId, string? containerId) { }
        public int PruneServer(string serverId, IReadOnlySet<string> liveKeys) { Pruned.Add(serverId); return 0; }
        public int PruneRemovedServers(IReadOnlySet<string> configuredServerIds) => 0;
        public void Clear() { }
        public Task SaveAsync() => Task.CompletedTask;
        public CveSummary SummarizeServer(string serverId) => throw new NotSupportedException();
        public IReadOnlyList<CveGroup> BuildGroups(IReadOnlyDictionary<string, DateTime> firstSeen,
            IReadOnlyDictionary<string, string> serverNames) => [];
    }

    private static T Null<T>() where T : class => NullProxy<T>.Create();

    private static (CveMonitorService Monitor, RecordingStore Store, List<string> Scanned, List<(string Server, bool Success)> Cycles) Monitor(
        FleetContainerListing listing)
    {
        var cycles = new List<(string Server, bool Success)>();
        var metrics = ScriptedProxy<Whiskers.Services.Observability.SelfMetrics.ISelfMetrics>.Create(new()
        {
            ["RecordCycle"] = a => { lock (cycles) cycles.Add(((string)a![1]!, (bool)a[3]!)); return null; },
        });
        var servers = new List<ServerConfig>
        {
            new() { Id = "slow", Name = "slow", ConnectionType = ConnectionType.SSH },
            new() { Id = "fine", Name = "fine", ConnectionType = ConnectionType.SSH },
        };
        var config = ScriptedProxy<IServerConfigService>.Create(new()
        {
            ["GetServers"] = _ => servers,
            ["GetEnabledServers"] = _ => servers,
        });
        var docker = ScriptedProxy<IDockerService>.Create(new()
        {
            ["ListAllContainersDetailedAsync"] = _ => Task.FromResult(listing),
            ["ListAllContainersAsync"] = _ => throw new InvalidOperationException("the plain listing hides failed servers"),
        });
        var scanned = new List<string>();
        var trivy = ScriptedProxy<ITrivyScanner>.Create(new()
        {
            ["ScanContainerImageAsync"] = a =>
            {
                lock (scanned) scanned.Add((string)a![0]!);
                return Task.FromResult(new CveScanResult { ServerId = (string)a[0]!, ContainerId = (string)a[1]! });
            },
        });
        var store = new RecordingStore();
        var monitor = new CveMonitorService(store,
            new StaticOptionsMonitor<CveMonitorSettings>(new CveMonitorSettings { Enabled = true, ScanOs = false, ScanContainers = true, NotifyOnFinding = false }),
            NullLogger<CveMonitorService>.Instance,
            metrics, config, docker, Null<IOsCveScanner>(), trivy,
            Null<Whiskers.Services.Notifications.INotificationService>(), Null<ICveAgeStore>(),
            Null<Microsoft.AspNetCore.SignalR.IHubContext<Whiskers.Hubs.ContainerHub>>(), Null<Whiskers.Services.Metrics.IMetricsSource>());
        return (monitor, store, scanned, cycles);
    }

    [Fact]
    public async Task A_server_whose_listing_timed_out_keeps_its_container_findings()
    {
        var listing = new FleetContainerListing
        {
            Containers = [new ContainerInfo { Id = "c1", Name = "web", Image = "nginx", ServerId = "fine" }],
            RespondedServerIds = new HashSet<string> { "fine" },
            FailedServers = [new FleetServerFailure("slow", "slow", "timed out after 8 s")],
        };
        var (monitor, store, scanned, cycles) = Monitor(listing);

        Assert.True(await monitor.RunOneCycleAsync());

        Assert.Equal(["fine"], store.Pruned);           // never "slow": its findings stay
        Assert.Equal(["fine"], scanned);
        // The skipped server is no success this cycle: its "last success" age keeps growing while its findings wait.
        Assert.Contains(("slow", false), cycles);
        Assert.Contains(("fine", true), cycles);
    }

    [Fact]
    public async Task A_server_missing_from_the_listing_altogether_is_not_pruned_either()
    {
        var listing = new FleetContainerListing
        {
            RespondedServerIds = new HashSet<string> { "fine" },
        };
        var (monitor, store, _, _) = Monitor(listing);

        Assert.True(await monitor.RunOneCycleAsync());

        Assert.Equal(["fine"], store.Pruned);
    }
}
