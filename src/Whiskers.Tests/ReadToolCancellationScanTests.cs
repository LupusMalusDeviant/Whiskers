using System.Reflection;
using ModelContextProtocol.Server;
using Whiskers.Mcp;
using Whiskers.Models;

namespace Whiskers.Tests;

/// <summary>
/// INV-40 (review of the agent fixes): a read tool that reaches a host, Docker, a cloud or DNS provider or another
/// network call must take the caller's <see cref="CancellationToken"/> — the MCP SDK binds it from the request and the
/// agent passes its own — so a closed chat, a stopped trigger or instruct_agent's deadline stops the call at its source.
/// No read tool did; the invoker's abandonment only released the caller while the read kept running against the host.
/// </summary>
public class ReadToolCancellationScanTests
{
    /// <summary>The services through which a tool reaches a host or the network. A tool that injects one of them is a
    /// network read; one that only reads Whiskers' own state (stores, settings, the local database) is not listed.</summary>
    private static readonly Type[] NetworkServices =
    [
        typeof(Whiskers.Services.Docker.IDockerService),
        typeof(Whiskers.Services.Server.IHostCommandExecutor),
        typeof(Whiskers.Services.Server.IFirewallService),
        typeof(Whiskers.Services.Server.INginxService),
        typeof(Whiskers.Services.Server.ISystemdService),
        typeof(Whiskers.Services.Server.ISslCertService),
        typeof(Whiskers.Services.Database.IDatabaseService),
        typeof(Whiskers.Services.Backup.IVolumeBackupService),
        typeof(Whiskers.Services.LogMonitor.ILogSearchService),
        typeof(Whiskers.Services.Metrics.IMetricsSource),
        typeof(Whiskers.Services.Cloud.ICloudControlService),
        typeof(Whiskers.Services.Cloud.Providers.IHetznerExtensions),
        typeof(Whiskers.Services.Dns.IDnsRecordService),
    ];

    private static IEnumerable<MethodInfo> ReadTools() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null
                        && m.GetCustomAttribute<McpToolLevelAttribute>()?.Level == McpPermissionLevels.Read);

    private static bool ReachesTheNetwork(MethodInfo tool) =>
        tool.GetParameters().Any(p => NetworkServices.Contains(p.ParameterType));

    [Fact]
    public void Every_read_tool_that_reaches_a_host_or_the_network_takes_the_callers_token()
    {
        var missing = ReadTools()
            .Where(ReachesTheNetwork)
            .Where(m => m.GetParameters().All(p => p.ParameterType != typeof(CancellationToken)))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "Read tools that reach a host or the network without a CancellationToken parameter (INV-40):\n  " +
            string.Join("\n  ", missing));
    }

    [Fact]
    public void The_scan_sees_the_network_read_tools()
    {
        // Guards the scan itself: a renamed attribute or a moved assembly must not turn it into a test of nothing.
        var network = ReadTools().Where(ReachesTheNetwork).Select(m => m.Name).ToList();
        Assert.Contains("ListContainers", network);
        Assert.Contains("GetServerLogs", network);
        Assert.Contains("ListCloudServers", network);
        Assert.Contains("ListDnsRecords", network);
        Assert.True(network.Count >= 29, $"Only {network.Count} network read tools found.");
    }
}
