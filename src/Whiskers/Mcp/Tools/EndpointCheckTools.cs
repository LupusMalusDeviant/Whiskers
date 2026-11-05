using System.ComponentModel;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Whiskers.Models;
using Whiskers.Services.EndpointChecks;
using Whiskers.Services.Mcp;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Mcp.Tools;

/// <summary>R1-57 (contract 0004, decision 6): the endpoint checks and their last result, read-only. There is no write
/// counterpart: a check runs a command on the host, so only an Admin defines one, in the UI. The exceptions' addresses
/// are left out (only their count), so the list does not map the internal network for every reader.</summary>
[McpServerToolType]
public class EndpointCheckTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("List the endpoint checks (HTTP status, TCP connect, TLS certificate expiry) that run from the managed hosts, with each check's last result. Use it to see which services a host cannot reach or whose certificate is about to expire. Read-only; checks are defined by an Admin in the UI.")]
    public static string EndpointChecksList(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IEndpointCheckStore store,
        IServerConfigService servers,
        [Description("Limit to one server (optional)")] string? serverId = null,
        [Description("Only checks whose last run did not pass (optional)")] bool failingOnly = false)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "endpoint_checks_list");
        if (denied != null) return denied;

        var last = store.LastResults();
        var lines = new List<string>();
        foreach (var check in store.List(string.IsNullOrWhiteSpace(serverId) ? null : serverId))
        {
            last.TryGetValue(check.Id, out var result);
            if (failingOnly && result is { Outcome: EndpointCheckOutcome.Passed }) continue;
            if (failingOnly && result is null) continue;
            var server = servers.GetServer(check.ServerId)?.Name ?? check.ServerId;
            var state = !check.Enabled
                ? check.SuspendedReason is { } why ? $"suspended ({why})" : "off"
                : result is null ? "not run yet"
                : Summary(result);
            var exceptions = check.Exceptions.Count == 0 ? "" : $", {check.Exceptions.Count} internal-target exception(s)";
            lines.Add($"- {check.Name} [{check.Kind}] on {server} ({check.Id}), every {check.IntervalSeconds} s{exceptions}: {state}");
        }

        if (lines.Count == 0)
            return failingOnly ? "No endpoint check is failing." : "No endpoint checks are defined.";
        return "Endpoint checks:\n" + string.Join('\n', lines);
    }

    /// <summary>Review m6: the outcome and its numbers only. The run's detail names the address that answered or was
    /// refused — for an excepted target an internal one — which is exactly the map the exceptions' addresses are left
    /// out for.</summary>
    internal static string Summary(EndpointCheckResultEntity result)
    {
        var parts = new List<string>
        {
            $"{result.Outcome} at {result.AtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}",
        };
        if (result.HttpStatus is { } status) parts.Add($"HTTP {status.ToString(CultureInfo.InvariantCulture)}");
        if (result.LatencyMs is { } ms) parts.Add($"{ms.ToString(CultureInfo.InvariantCulture)} ms");
        if (result.TlsNotAfterUtc is { } notAfter)
            parts.Add($"certificate valid until {notAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        return string.Join(", ", parts);
    }
}
