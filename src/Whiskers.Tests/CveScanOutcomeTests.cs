using Whiskers.Models.Cve;
using Whiskers.Services.Cve;

namespace Whiskers.Tests;

/// <summary>
/// What happens to a scan that failed (2026-08-28).
///
/// <para>Two rules pull in opposite directions and both are right. A failed scan must never overwrite real
/// findings — replacing them with an empty result would report a clean bill of health and then re-notify
/// every CVE on the next success. But a failed scan with <em>nothing</em> behind it has to be stored anyway,
/// because the alternative is storing nothing at all, and a target that is absent reads exactly like a target
/// with no findings.</para>
///
/// <para>That second half was missing. Two running containers on host-f sat in exactly that state: their
/// local image layers were damaged, Trivy could not export them, neither had ever been scanned successfully
/// there, and so they appeared in no list at all. The staleness metric could not help — what is not there
/// cannot age. Only a person counting containers by hand would have noticed, and that is how it was found.</para>
/// </summary>
public class CveScanOutcomeTests
{
    private static CveScanResult Result(string? error = null, params string[] cveIds)
    {
        var r = new CveScanResult
        {
            ServerId = "host-f", Source = CveSource.Container,
            ContainerId = "c1", ContainerName = "ghostunnel", Error = error
        };
        foreach (var id in cveIds)
            r.Findings.Add(new CveFinding { ServerId = "host-f", CveId = id, Package = "openssl" });
        return r;
    }

    [Fact]
    public void A_failed_scan_with_no_earlier_result_is_stored_rather_than_dropped()
    {
        // THE case. Dropping it leaves the target absent from every list, and absent looks like clean.
        var action = CveScanOutcome.Decide(previous: null, Result(error: "not found in tar"));

        Assert.Equal(ScanResultAction.Store, action);
    }

    [Fact]
    public void A_failed_scan_never_overwrites_findings_that_are_already_known()
    {
        // The other half, and it must not be weakened: a transient Trivy timeout replacing real findings with
        // an empty result would report a clean host and then re-notify every CVE on the next success.
        var previous = Result(cveIds: "CVE-2026-1111");

        var action = CveScanOutcome.Decide(previous, Result(error: "trivy timed out"));

        Assert.Equal(ScanResultAction.KeepPrevious, action);
    }

    [Fact]
    public void A_successful_scan_always_wins()
    {
        Assert.Equal(ScanResultAction.Store, CveScanOutcome.Decide(null, Result(cveIds: "CVE-2026-1")));
        Assert.Equal(ScanResultAction.Store,
            CveScanOutcome.Decide(Result(cveIds: "CVE-2026-1"), Result(cveIds: "CVE-2026-2")));
    }

    [Fact]
    public void A_successful_scan_that_found_nothing_is_a_result_not_a_failure()
    {
        // An image with no known CVEs is good news and must be stored as such — otherwise a clean image would
        // be indistinguishable from an unscanned one, which is the same confusion from the other side.
        Assert.Equal(ScanResultAction.Store, CveScanOutcome.Decide(Result(cveIds: "CVE-2026-1"), Result()));
    }

    [Fact]
    public void A_failure_is_never_announced_as_new_findings()
    {
        // A stored failure has an empty finding list. Running it through the diff would either say nothing or,
        // worse, page somebody about a scanner problem as though vulnerabilities had appeared.
        Assert.False(CveScanOutcome.ShouldNotify(Result(error: "not found in tar")));
        Assert.True(CveScanOutcome.ShouldNotify(Result(cveIds: "CVE-2026-1")));
    }

    [Fact]
    public void An_empty_error_string_counts_as_success_not_as_failure()
    {
        // Error is a nullable string set by several scanners; treating "" as a failure would silently stop
        // storing perfectly good results.
        Assert.Equal(ScanResultAction.Store, CveScanOutcome.Decide(null, Result(error: "")));
        Assert.True(CveScanOutcome.ShouldNotify(Result(error: "   ")));
    }
}
