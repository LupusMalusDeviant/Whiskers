using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Cve;

namespace Whiskers.Services.AutoPatch;

/// <summary>A container the nightly cycle will try to patch.</summary>
/// <param name="FixableFindings">Findings at or above the configured severity that name a fixed version.</param>
public sealed record AutoPatchCandidate(ContainerInfo Container, int FixableFindings)
{
    public bool IsDatabase => Container.IsDatabase;
}

/// <summary>A container that was considered and left alone, with the reason.</summary>
public sealed record AutoPatchSkip(string ServerId, string ContainerName, string Reason);

public sealed record AutoPatchPlan(IReadOnlyList<AutoPatchCandidate> Candidates, IReadOnlyList<AutoPatchSkip> Skipped);

/// <summary>
/// Decides which containers the nightly cycle touches. Pure: no Docker, no clock — so every rule here is a
/// test, not a hope.
/// </summary>
public static class AutoPatchPlanner
{
    /// <summary>Containers that carry the connection their own update would travel through: Whiskers itself,
    /// the socket proxy and mTLS tunnel it reaches the hosts with, and the reverse proxy in front of the MCP
    /// gateway. Recreating any of them from inside a cycle cuts the cycle off mid-flip.</summary>
    public static readonly IReadOnlySet<string> AlwaysExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "serverwatch", "whiskers", "socket-proxy", "ghostunnel", "nginx-proxy-manager",
    };

    /// <summary>Opt-out label. <c>whiskers.autopatch=false</c> on a service keeps it out of every cycle.</summary>
    public const string OptOutLabel = "whiskers.autopatch";

    public static AutoPatchPlan Plan(
        IEnumerable<ContainerInfo> containers,
        IEnumerable<CveScanResult> cveResults,
        AutoPatchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(containers);
        ArgumentNullException.ThrowIfNull(cveResults);
        ArgumentNullException.ThrowIfNull(settings);

        // Keyed by (server, NAME): the container id changes on every recreate, the name does not, and a
        // scan from before last night's update must still find the container it was about.
        var fixable = cveResults
            .Where(r => r.Source == CveSource.Container && !string.IsNullOrEmpty(r.ContainerName))
            .GroupBy(r => (r.ServerId, Name: r.ContainerName!), Comparer)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(r => r.Findings)
                      .Count(f => f.Severity >= settings.MinSeverity && !string.IsNullOrWhiteSpace(f.FixedVersion)),
                Comparer);

        var excluded = new HashSet<string>(settings.ExcludeContainers ?? [], StringComparer.OrdinalIgnoreCase);
        var candidates = new List<AutoPatchCandidate>();
        var skipped = new List<AutoPatchSkip>();

        foreach (var c in containers)
        {
            fixable.TryGetValue((c.ServerId, c.Name), out var count);
            if (count == 0) continue;   // nothing to fix — not even worth a line in the report

            var reason = SkipReason(c, excluded);
            if (reason is not null)
                skipped.Add(new AutoPatchSkip(c.ServerId, c.Name, reason));
            else
                candidates.Add(new AutoPatchCandidate(c, count));
        }

        // Stateless services first: if the night goes wrong, it goes wrong on a proxy before it reaches a
        // database. Within a group, the most findings first.
        var ordered = candidates
            .OrderBy(x => x.IsDatabase)
            .ThenByDescending(x => x.FixableFindings)
            .ThenBy(x => x.Container.ServerId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Container.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var max = Math.Max(0, settings.MaxPerCycle);
        foreach (var over in ordered.Skip(max))
            skipped.Add(new AutoPatchSkip(over.Container.ServerId, over.Container.Name,
                $"over the limit of {max} per night — next night"));

        return new AutoPatchPlan(ordered.Take(max).ToList(), skipped);
    }

    private static string? SkipReason(ContainerInfo c, HashSet<string> excluded)
    {
        if (AlwaysExcluded.Contains(c.Name))
            return "carries Whiskers' own connection — update it by hand, detached";
        if (excluded.Contains(c.Name))
            return "excluded in settings";
        if (c.Labels.TryGetValue(OptOutLabel, out var v) && string.Equals(v, "false", StringComparison.OrdinalIgnoreCase))
            return $"opted out ({OptOutLabel}=false)";
        if (!c.Labels.ContainsKey("com.docker.compose.service") || !c.Labels.ContainsKey("com.docker.compose.project.working_dir"))
            return "not managed by compose — no file to recreate it from";
        if (!string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))
            return $"not running ({c.State})";
        if (c.Image.Contains("@sha256:", StringComparison.Ordinal))
            return "pinned to a digest";
        return null;
    }

    private static readonly IEqualityComparer<(string ServerId, string Name)> Comparer = new KeyComparer();

    private sealed class KeyComparer : IEqualityComparer<(string ServerId, string Name)>
    {
        public bool Equals((string ServerId, string Name) a, (string ServerId, string Name) b)
            => string.Equals(a.ServerId, b.ServerId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ServerId, string Name) k)
            => HashCode.Combine(k.ServerId.ToUpperInvariant(), k.Name.ToUpperInvariant());
    }
}
