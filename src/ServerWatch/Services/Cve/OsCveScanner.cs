using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models.Cve;
using ServerWatch.Services.Server;
using ServerWatch.Utils;

namespace ServerWatch.Services.Cve;

/// <summary>
/// Scans the host OS (Debian/Ubuntu apt-based) for pending security updates and,
/// optionally, the CVE IDs they address. CVE IDs are extracted from apt changelogs
/// (slow but works without extra tooling installed on the target server).
/// </summary>
public class OsCveScanner
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
            "sudo apt-get update -q -y 2>&1 | tail -3",
            TimeSpan.FromMinutes(2),
            ct);
        if (!update.Success)
            _logger.LogDebug("apt-get update on {Server} exited {Exit}: {Err}",
                serverId, update.ExitCode, Truncate(update.Error ?? update.Output, 200));

        // 2. List upgradable security-pocket packages.
        var listCmd = "apt list --upgradable 2>/dev/null | grep -E -- '-security[ /]' || true";
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
                    serverId, p.Pkg, settings.OsChangelogTimeoutSeconds, ct);

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
        string serverId, string pkg, int timeoutSec, CancellationToken ct)
    {
        // apt-get changelog doesn't need root. Pipe through grep -oE for CVE-IDs only,
        // then sort -u for de-dup. Tail-cap to a reasonable line count to handle huge
        // changelogs.
        var cmd =
            $"apt-get changelog {ShellUtils.Quote(pkg)} 2>/dev/null | head -c 200000 | " +
            "grep -oE 'CVE-[0-9]{4}-[0-9]+' | sort -u || true";
        var exec = await _executor.ExecuteAsync(
            serverId, cmd, TimeSpan.FromSeconds(timeoutSec), ct);

        if (string.IsNullOrWhiteSpace(exec.Output))
            return new List<string>();

        return exec.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => CveIdRegex.IsMatch(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CveFinding BuildFinding(string serverId, AptPackage p, string? cveId)
    {
        return new CveFinding
        {
            ServerId = serverId,
            Source = CveSource.Os,
            // Pending security updates count as HIGH by default. Precise CVE severity
            // would require an NVD/USN lookup — out of scope for the initial version.
            Severity = CveSeverity.High,
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
