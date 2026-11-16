using Whiskers.Models.Cve;

namespace Whiskers.Services.Cve;

/// <summary>What to do with the result of one scan attempt.</summary>
public enum ScanResultAction
{
    /// <summary>Put it in the store — either it succeeded, or it failed and there is nothing better there.</summary>
    Store,

    /// <summary>Leave the earlier result alone. The scan failed and the old findings are still the best
    /// knowledge available.</summary>
    KeepPrevious
}

/// <summary>
/// The one decision every scan attempt ends in, kept apart from the loop so it can be tested (Plan-0007
/// follow-up, 2026-08-28).
///
/// <para>The rule has two halves and both matter:</para>
///
/// <para><b>A failed scan must not overwrite good findings.</b> Trivy times out, apt has a bad minute, a host
/// is briefly unreachable — replacing real findings with an empty result would report a clean bill of health
/// and then re-notify every CVE on the next success. That half was always right.</para>
///
/// <para><b>But a failed scan with nothing behind it has to be stored anyway.</b> The old code kept the
/// previous result in both cases, and when there was no previous result it therefore stored nothing at all —
/// leaving the target absent from every list. On 2026-08-28 two running containers on infomaniak had been in
/// that state: their local image layers were damaged, Trivy could not export them, and because neither had
/// ever been scanned successfully there they appeared nowhere. An absent target reads exactly like a clean
/// one, and the staleness metric cannot help — what is not there cannot age. Storing the failure makes the
/// target exist and say why it is empty.</para>
/// </summary>
public static class CveScanOutcome
{
    public static ScanResultAction Decide(CveScanResult? previous, CveScanResult result)
    {
        // A successful scan always wins, including one that legitimately found nothing.
        if (string.IsNullOrWhiteSpace(result.Error)) return ScanResultAction.Store;

        // It failed. Keep real findings if there are any; otherwise record the failure rather than nothing,
        // because "no entry" and "no findings" are indistinguishable everywhere downstream.
        // Review 1.0.0 (T1): "real findings" means findings. A stored clean result is no knowledge worth keeping over a
        // failure — kept, it went on reading "clean" for a host that cannot be scanned at all (a host without apt).
        return previous is { Findings.Count: > 0 } ? ScanResultAction.KeepPrevious : ScanResultAction.Store;
    }

    /// <summary>True when this attempt produced findings worth diffing and notifying about. A failure never
    /// does: reporting its empty result as "new findings" would page somebody about a scanner problem.</summary>
    public static bool ShouldNotify(CveScanResult result) => string.IsNullOrWhiteSpace(result.Error);
}
