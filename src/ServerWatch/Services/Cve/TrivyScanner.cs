using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models.Cve;
using ServerWatch.Services.Server;
using ServerWatch.Utils;

namespace ServerWatch.Services.Cve;

/// <summary>
/// Scans a single container image for known CVEs by running Trivy as a one-shot
/// container on the target server. Uses a named Docker volume to persist the
/// Trivy vulnerability DB between scans (only the first scan per server pays the
/// full DB download cost).
/// </summary>
public class TrivyScanner : ITrivyScanner
{
    private readonly IHostCommandExecutor _executor;
    private readonly IOptionsMonitor<CveMonitorSettings> _settings;
    private readonly ILogger<TrivyScanner> _logger;

    private const string CacheVolume = "serverwatch-trivy-cache";

    public TrivyScanner(
        IHostCommandExecutor executor,
        IOptionsMonitor<CveMonitorSettings> settings,
        ILogger<TrivyScanner> logger)
    {
        _executor = executor;
        _settings = settings;
        _logger = logger;
    }

    public async Task<CveScanResult> ScanContainerImageAsync(
        string serverId,
        string containerId,
        string containerName,
        string image,
        CancellationToken ct = default)
    {
        var result = new CveScanResult
        {
            ServerId = serverId,
            Source = CveSource.Container,
            ContainerId = containerId,
            ContainerName = containerName,
            Image = image
        };

        if (string.IsNullOrWhiteSpace(image))
        {
            result.Error = "image is empty";
            return result;
        }

        var trivyImage = _settings.CurrentValue.TrivyImage;
        // We scan ALL severities and filter at notify/display time. Trivy DB persists in
        // a named volume so subsequent scans are fast.
        var cmd =
            $"docker run --rm -v {CacheVolume}:/root/.cache/trivy {trivyImage} " +
            $"image --format json --quiet --no-progress --timeout 8m " +
            ShellUtils.Quote(image);

        // First scan on a server downloads the Trivy DB (~600 MB compressed) so allow
        // up to 10 minutes. Subsequent scans usually finish in seconds.
        var exec = await _executor.ExecuteAsync(serverId, cmd, TimeSpan.FromMinutes(10), ct);
        if (!exec.Success || string.IsNullOrWhiteSpace(exec.Output))
        {
            result.Error = $"trivy exit {exec.ExitCode}: " +
                Truncate(string.IsNullOrEmpty(exec.Error) ? exec.Output : exec.Error, 400);
            _logger.LogWarning("Trivy scan failed for {Image} on {Server}: {Error}",
                image, serverId, result.Error);
            return result;
        }

        try
        {
            var root = JsonNode.Parse(exec.Output);
            var results = root?["Results"]?.AsArray();
            if (results == null)
                return result;

            foreach (var r in results)
            {
                var vulns = r?["Vulnerabilities"]?.AsArray();
                if (vulns == null) continue;
                foreach (var v in vulns)
                {
                    if (v == null) continue;
                    var cveId = TryString(v, "VulnerabilityID");
                    if (string.IsNullOrEmpty(cveId)) continue;

                    result.Findings.Add(new CveFinding
                    {
                        ServerId = serverId,
                        Source = CveSource.Container,
                        ContainerId = containerId,
                        ContainerName = containerName,
                        Image = image,
                        CveId = cveId,
                        Package = TryString(v, "PkgName") ?? "",
                        InstalledVersion = TryString(v, "InstalledVersion"),
                        FixedVersion = TryString(v, "FixedVersion"),
                        Title = TryString(v, "Title"),
                        Reference = TryString(v, "PrimaryURL"),
                        Severity = ParseSeverity(TryString(v, "Severity"))
                    });
                }
            }

            _logger.LogInformation("Trivy scan {Image} on {Server}: {Count} findings",
                image, serverId, result.Findings.Count);
        }
        catch (Exception ex)
        {
            result.Error = $"parse failed: {ex.Message}";
            _logger.LogWarning(ex, "Failed to parse Trivy JSON for {Image} on {Server}", image, serverId);
        }

        return result;
    }

    private static string? TryString(JsonNode? node, string prop)
    {
        var val = node?[prop];
        if (val is null) return null;
        try { return val.GetValue<string>(); }
        catch { return val.ToString(); }
    }

    private static CveSeverity ParseSeverity(string? s) => s?.ToUpperInvariant() switch
    {
        "CRITICAL" => CveSeverity.Critical,
        "HIGH" => CveSeverity.High,
        "MEDIUM" => CveSeverity.Medium,
        "LOW" => CveSeverity.Low,
        _ => CveSeverity.Unknown
    };

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
