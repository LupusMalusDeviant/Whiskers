using Whiskers.Services.Mutations;

namespace Whiskers.Services.Cve;

/// <summary>Background CVE monitor; also exposes a manual scan cycle the UI can trigger.</summary>
public interface ICveMonitorService
{
    /// <summary>One scan cycle across all enabled servers, started by a person (INV-18, user decision 2026-11-07:
    /// Operator). The monitor's own unattended cycle does not go through this method.</summary>
    [MutatingOperation("cve.scan")]
    Task RunOneCycleAsync(CancellationToken ct = default);
}
