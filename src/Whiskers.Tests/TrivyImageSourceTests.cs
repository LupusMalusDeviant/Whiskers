using Whiskers.Services.Cve;

namespace Whiskers.Tests;

/// <summary>
/// When a failed Trivy scan is worth retrying from the registry (2026-08-28).
///
/// <para>Trivy's default path exports the image from the Docker daemon. On a host using the <b>containerd
/// image store</b> that export can omit layers and Trivy stops with <c>file blobs/sha256/… not found in
/// tar</c>. Nothing is damaged, and everything about it invites the wrong diagnosis: the image digest is
/// identical to one that scans perfectly elsewhere, a fresh pull changes nothing, and neither does a fresh
/// Trivy cache. Four hypotheses were tested and refuted before the storage driver turned out to be the only
/// difference between the two hosts.</para>
///
/// <para>The cost of not noticing: two running containers on host-f with no vulnerability data at all,
/// invisible rather than visibly broken, on a host that therefore looked clean.</para>
/// </summary>
public class TrivyImageSourceTests
{
    [Fact]
    public void The_containerd_export_failure_is_retried_from_the_registry()
    {
        // The real message, verbatim from host-f.
        const string output =
            "2026-08-28T13:41:02Z FATAL Fatal error image scan error: scan error: unable to initialize a " +
            "scan service: unable to initialize an image scan service: unable to find the specified image " +
            "... unable to populate: unable to open: failed to initialize the struct from the temporary " +
            "file: file blobs/sha256/607e1646cecf2022ea1e12ec2798d1e0bf9e932a8b1d35dab60467485ffb24cf " +
            "not found in tar";

        Assert.True(TrivyImageSource.ShouldRetryFromRegistry(output));
    }

    [Fact]
    public void An_unreachable_host_is_not_retried_from_the_registry()
    {
        // THE counter-case. A blanket retry would answer "the host is down" by quietly pulling from the
        // internet, and the second attempt's failure would be the only thing anybody ever saw — a real
        // outage reported as a scanner problem.
        Assert.False(TrivyImageSource.ShouldRetryFromRegistry(
            "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?"));
        Assert.False(TrivyImageSource.ShouldRetryFromRegistry("trivy exit 1: connection refused"));
        Assert.False(TrivyImageSource.ShouldRetryFromRegistry("unauthorized: authentication required"));
    }

    [Fact]
    public void A_successful_scan_is_never_retried()
    {
        // A retry after success would double every scan's cost across the fleet.
        Assert.False(TrivyImageSource.ShouldRetryFromRegistry("""{"Results":[]}"""));
    }

    [Fact]
    public void Nothing_at_all_is_not_a_reason_to_retry()
    {
        Assert.False(TrivyImageSource.ShouldRetryFromRegistry(null));
        Assert.False(TrivyImageSource.ShouldRetryFromRegistry(""));
        Assert.False(TrivyImageSource.ShouldRetryFromRegistry("   "));
    }

    [Fact]
    public void The_signature_is_matched_whatever_the_casing()
    {
        // Trivy's wording has moved between releases; the casing is not worth a silent regression.
        Assert.True(TrivyImageSource.ShouldRetryFromRegistry("file blobs/sha256/abc NOT FOUND IN TAR"));
    }
}
