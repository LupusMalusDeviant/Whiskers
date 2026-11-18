namespace Whiskers.Services.Cve;

/// <summary>
/// Decides when a Trivy scan is worth retrying against the registry instead of the local daemon
/// (2026-08-28).
///
/// <para><b>The problem.</b> Trivy's default path exports the image from the Docker daemon and reads the
/// resulting tar. On a host where Docker uses the <b>containerd image store</b>
/// (<c>io.containerd.snapshotter.v1</c> instead of the classic <c>overlay2</c> graph driver) that export can
/// omit layers, and Trivy stops with <c>file blobs/sha256/… not found in tar</c>. Nothing is damaged: the same
/// image digest scans perfectly on a host with the classic driver, a fresh pull changes nothing, and neither
/// does a fresh Trivy cache. It is the export path, not the image.</para>
///
/// <para><b>What it cost.</b> Two running containers on web-02 had no vulnerability data at all — for
/// weeks, in one case — and because a target that has never been scanned was stored nowhere, they were
/// invisible rather than visibly broken. The host looked clean because nobody was looking at it.</para>
///
/// <para><b>Why a retry and not a permanent switch.</b> Reading from the registry always works but costs a
/// pull per scan and needs credentials for private images; the local export is free and handles
/// locally-built images that exist in no registry at all. So the local path stays the default and the
/// registry is the fallback for exactly this failure.</para>
/// </summary>
public static class TrivyImageSource
{
    /// <summary>The signature of the containerd-store export failure, as Trivy words it.</summary>
    private const string MissingBlobSignature = "not found in tar";

    /// <summary>
    /// True when the failure is the containerd-store export problem and a registry read is worth trying.
    ///
    /// <para>Deliberately narrow. A blanket retry would paper over a genuinely unreachable host or a broken
    /// scanner by quietly pulling from the internet instead, and the second attempt's failure would be the
    /// only thing anybody ever saw.</para>
    /// </summary>
    public static bool ShouldRetryFromRegistry(string? trivyOutput)
        => !string.IsNullOrWhiteSpace(trivyOutput)
           && trivyOutput.Contains(MissingBlobSignature, StringComparison.OrdinalIgnoreCase);
}
