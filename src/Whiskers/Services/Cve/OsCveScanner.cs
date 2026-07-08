using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models.Cve;
using Whiskers.Services.Server;
using Whiskers.Utils;

namespace Whiskers.Services.Cve;

/// <summary>
/// Scans the host OS (Debian/Ubuntu apt-based) for pending security updates and,
/// optionally, the CVE IDs they address. CVE IDs are extracted from apt changelogs
/// (slow but works without extra tooling installed on the target server).
/// </summary>
public class OsCveScanner : IOsCveScanner
{
    private readonly IHostCommandExecutor _executor;
    private readonly IOptionsMonitor<CveMonitorSettings> _settings;
    private readonly ILogger<OsCveScanner> _logger;

    // Examples we need to match:
    //   apparmor/noble-security 4.0.1really4.0.1-0ubuntu0.24.04.6 amd64 [upgradable from: 4.0.1really4.0.1-0ubuntu0.24.04.4]
    //   linux-image-generic/noble-security 6.8.0-117.119 amd64 [upgradable from: 6.8.0-71.71]
    private static readonly Regex AptListRegex = new(
        @"^(?<pkg>[a-zA-Z0-9.+:\-]+)/(?<pocket>\S+)\s+(?<newver>\S+)\s+(?<arch>\S+)\s+\[upgradable from:\s+(?<oldver>[^\]]+)\]",
        RegexOptions.Compiled);

    private static readonly Regex CveIdRegex = new(@"CVE-\d{4}-\d+", RegexOptions.Compiled);

    public OsCveScanner(
        IHostCommandExecutor executor,
        IOptionsMonitor<CveMonitorSettings> settings,
        ILogger<OsCveScanner> logger)
    {
        _executor = executor;
        _settings = settings;
        _logger = logger;
    }

    public async Task<CveScanResult> ScanAsync(string serverId, CancellationToken ct = default)
    {
        var result = new CveScanResult { ServerId = serverId, Source = CveSource.Os };
        var settings = _settings.CurrentValue;

        // 1. Refresh apt cache. Failures are tolerated (e.g. transient network glitch);
        //    we still try to list whatever is in the existing cache.
        var update = await _executor.ExecuteAsync(
            serverId,
            // LC_ALL=C.UTF-8 keeps apt output English so the upgradable regex matches on non-English hosts.
            "LC_ALL=C.UTF-8 sudo apt-get update -q -y 2>&1 | tail -3",
            TimeSpan.FromMinutes(2),
            ct);
        if (!update.Success)
            _logger.LogDebug("apt-get update on {Server} exited {Exit}: {Err}",
                serverId, update.ExitCode, Truncate(update.Error ?? update.Output, 200));

        // 2. List upgradable security-pocket packages.
        var listCmd = "LC_ALL=C.UTF-8 apt list --upgradable 2>/dev/null | grep -E -- '-security[ /]' || true";
        var list = await _executor.ExecuteAsync(serverId, listCmd, TimeSpan.FromSeconds(30), ct);
        if (list.ExitCode is not 0 and not 1)
        {
            // grep with no matches exits 1; we OR with true so we should never get there,
            // but be defensive.
            result.Error = $"apt list failed (exit {list.ExitCode}): " +
                Truncate(string.IsNullOrEmpty(list.Error) ? list.Output : list.Error, 200);
            return result;
        }

        var packages = ParseAptListOutput(list.Output ?? "");

        if (packages.Count == 0)
        {
            _logger.LogInformation("OS CVE scan {Server}: no pending security updates", serverId);
            return result;
        }

        // 3. For each package, optionally resolve CVE-IDs from its changelog.
        foreach (var p in packages)
        {
            if (ct.IsCancellationRequested) break;

            if (settings.EnableOsCveIds)
            {
                var cves = await GetCveIdsFromChangelogAsync(
                    serverId, p.Pkg, p.OldVer, settings.OsChangelogTimeoutSeconds, ct);

                if (cves.Count == 0)
                {
                    // No CVEs surfaced — still record one synthetic entry so the UI shows
                    // the pending security update.
                    result.Findings.Add(BuildFinding(serverId, p, cveId: null));
                }
                else
                {
                    foreach (var cve in cves)
                        result.Findings.Add(BuildFinding(serverId, p, cveId: cve));
                }
            }
            else
            {
                result.Findings.Add(BuildFinding(serverId, p, cveId: null));
            }
        }

        _logger.LogInformation(
            "OS CVE scan {Server}: {Pkgs} security package(s), {Findings} finding(s)",
            serverId, packages.Count, result.Findings.Count);
        return result;
    }

    private static List<AptPackage> ParseAptListOutput(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<AptPackage>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("WARNING:")) continue;
            var m = AptListRegex.Match(trimmed);
            if (!m.Success) continue;
            // grep might match a non-security pocket if a line happens to contain "-security"
            // in another field; keep only true security pocket entries.
            if (!m.Groups["pocket"].Value.Contains("-security", StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add(new AptPackage(
                m.Groups["pkg"].Value,
                m.Groups["newver"].Value,
                m.Groups["oldver"].Value));
        }
        return list;
    }

    private async Task<List<string>> GetCveIdsFromChangelogAsync(
        string serverId, string pkg, string installedVer, int timeoutSec, CancellationToken ct)
    {
        // apt-get changelog doesn't need root. We fetch the raw changelog (capped) and parse
        // it in-process so we attribute ONLY the CVEs from stanzas NEWER than the installed
        // version — i.e. the ones this pending update actually fixes. Grepping the entire
        // changelog (curl's goes back to 2005) and tagging every historical CVE onto a single
        // point-update was the cause of thousands of bogus "High" findings.
        var cmd =
            $"apt-get changelog {ShellUtils.Quote(pkg)} 2>/dev/null | head -c 200000 || true";
        var exec = await _executor.ExecuteAsync(
            serverId, cmd, TimeSpan.FromSeconds(timeoutSec), ct);

        if (string.IsNullOrWhiteSpace(exec.Output))
            return new List<string>();

        return ExtractNewCveIds(exec.Output, installedVer);
    }

    // First line of a Debian/Ubuntu changelog stanza, e.g.
    //   curl (8.5.0-2ubuntu10.10) noble-security; urgency=medium
    // Stanza bodies and the "-- maintainer" trailer are indented, so only true headers
    // start at column 0 with "<pkg> (<version>)".
    private static readonly Regex ChangelogHeaderRegex = new(
        @"^\S+\s+\((?<ver>[^)]+)\)", RegexOptions.Compiled);

    /// <summary>
    /// Returns the distinct CVE IDs mentioned in changelog stanzas strictly NEWER than
    /// <paramref name="installedVer"/>. Walks newest→oldest and stops at the installed
    /// version's stanza (those fixes are already applied). A 5-stanza backstop bounds the
    /// scan if the installed version isn't present in the (capped) changelog.
    /// </summary>
    public static List<string> ExtractNewCveIds(string changelog, string installedVer)
    {
        var installed = StripEpoch(installedVer).Trim();
        var cves = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stanzas = 0;

        foreach (var line in changelog.Split('\n'))
        {
            var header = ChangelogHeaderRegex.Match(line);
            if (header.Success)
            {
                var ver = StripEpoch(header.Groups["ver"].Value).Trim();
                if (ver == installed) break;   // reached the installed release — stop
                if (++stanzas > 5) break;       // backstop against runaway over-attribution
                continue;
            }
            foreach (Match m in CveIdRegex.Matches(line))
                if (seen.Add(m.Value)) cves.Add(m.Value);
        }
        return cves;
    }

    // Drop a leading "epoch:" so changelog and apt versions compare equal (e.g. "1:2.3" → "2.3").
    private static string StripEpoch(string v)
    {
        var i = v.IndexOf(':');
        return i >= 0 ? v[(i + 1)..] : v;
    }

    private static CveFinding BuildFinding(string serverId, AptPackage p, string? cveId)
    {
        return new CveFinding
        {
            ServerId = serverId,
            Source = CveSource.Os,
            // Pending OS security updates default to MEDIUM: apt alone doesn't reveal the real
            // per-CVE severity, and hardcoding High flooded the dashboard with false High
            // findings. Precise severity would need an NVD/USN lookup (future enhancement).
            Severity = CveSeverity.Medium,
            Package = p.Pkg,
            InstalledVersion = p.OldVer,
            FixedVersion = p.NewVer,
            CveId = cveId ?? $"SECURITY-UPDATE/{p.Pkg}",
            Title = cveId == null
                ? $"Pending security update for {p.Pkg}"
                : null,
            Reference = cveId == null
                ? null
                : $"https://nvd.nist.gov/vuln/detail/{cveId}"
        };
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    private record AptPackage(string Pkg, string NewVer, string OldVer);
}
