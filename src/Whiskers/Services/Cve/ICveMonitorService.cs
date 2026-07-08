namespace Whiskers.Services.Cve;

/// <summary>Background CVE monitor; also exposes a manual scan cycle the UI can trigger.</summary>
public interface ICveMonitorService
{
    Task RunOneCycleAsync(CancellationToken ct = default);
}
