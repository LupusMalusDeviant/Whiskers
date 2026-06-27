using ServerWatch.Models.Cve;

namespace ServerWatch.Services.Cve;

/// <summary>Scans a container image for known CVEs using Trivy.</summary>
public interface ITrivyScanner
{
    Task<CveScanResult> ScanContainerImageAsync(string serverId, string containerId, string containerName, string image, CancellationToken ct = default);
}
