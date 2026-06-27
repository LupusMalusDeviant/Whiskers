using ServerWatch.Models.Cve;

namespace ServerWatch.Services.Cve;

/// <summary>Scans a server's host OS packages for known CVEs.</summary>
public interface IOsCveScanner
{
    Task<CveScanResult> ScanAsync(string serverId, CancellationToken ct = default);
}
