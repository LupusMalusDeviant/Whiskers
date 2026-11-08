using Whiskers.Services.Mutations;

namespace Whiskers.Services.Cve;

/// <summary>Background CVE monitor; also exposes a manual scan cycle the UI can trigger.</summary>
public interface ICveMonitorService
{
    /// <summary>One scan cycle across all enabled servers, started by a person (INV-18, user decision 2026-11-07:
    /// Operator). The monitor's own unattended cycle does not go through this method. Returns <c>false</c> when a cycle
    /// was already running (or the CVE module is off) and this call did nothing. <paramref name="started"/> is called
    /// once the cycle has taken the scan gate, so a page says "started" only for a scan that really started.</summary>
    [MutatingOperation("cve.scan")]
    Task<bool> RunOneCycleAsync(CancellationToken ct = default, Action? started = null);
}
