using Whiskers.Services.Cve;
using Whiskers.Services.Docker;

namespace Whiskers.Services.ImageUpdate;

/// <param name="UpdateAvailable">False when the registry has nothing newer — then the risk is moot and the
/// findings list is empty by construction, not by luck.</param>
/// <param name="Error">Set when the assessment could not be made at all. An assessment that failed must say
/// so; returning "no findings" would read as "safe to update".</param>
public sealed record UpdateRiskReport(
    string ServerId,
    string ContainerName,
    string ImageRef,
    bool UpdateAvailable,
    string? CurrentDigest,
    string? CandidateDigest,
    UpdateRisk? Risk,
    string? Error);

public interface IUpdateRiskService
{
    /// <param name="scanCandidate">Scan the candidate image for CVEs so the benefit side of the decision has
    /// a number. Costs a Trivy run (tens of seconds); without it the report says the benefit is unknown
    /// rather than pretending it is zero.</param>
    /// <remarks>It pulls the candidate image, so it is a mutation of its own (<c>image.assess_update</c>, R1-48) that
    /// composes <c>image.pull</c>; it starts and recreates nothing.</remarks>
    [Whiskers.Services.Mutations.MutatingOperation("image.assess_update")]
    Task<UpdateRiskReport> AssessAsync(string serverId, string container, bool scanCandidate = true,
        CancellationToken ct = default);
}

/// <summary>
/// Answers "what would updating this container change?" before anything is recreated (GAP-6, 2026-08-28).
///
/// <para>Pulls the candidate image — which starts nothing and restarts nothing — then compares what the two
/// images declare, and counts which vulnerabilities the new one actually leaves behind. The comparison itself
/// lives in <see cref="UpdateRiskAssessor"/> and is pure; this class is the plumbing that feeds it real
/// images.</para>
/// </summary>
public sealed class UpdateRiskService(
    IDockerService docker,
    ICveFindingsStore cveStore,
    ITrivyScanner trivy,
    ILogger<UpdateRiskService> logger) : IUpdateRiskService
{
    public async Task<UpdateRiskReport> AssessAsync(string serverId, string container,
        bool scanCandidate = true, CancellationToken ct = default)
    {
        var all = await docker.ListAllContainersAsync();
        var target = all.FirstOrDefault(c =>
            string.Equals(c.ServerId, serverId, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(c.Name, container, StringComparison.OrdinalIgnoreCase) ||
             c.Id.StartsWith(container, StringComparison.OrdinalIgnoreCase)));

        if (target is null)
            return Failed(serverId, container, "", $"No container '{container}' on server '{serverId}'.");

        var image = target.Image;
        if (string.IsNullOrWhiteSpace(image))
            return Failed(serverId, target.Name, "", "The container reports no image reference.");

        // Read the RUNNING image before pulling. Afterwards the tag points at the new digest and the
        // comparison would be the candidate against itself — a guaranteed "nothing changed".
        var before = await docker.GetImageContractAsync(image, serverId);
        var currentDigest = await docker.GetImageDigestAsync(image, serverId);
        if (before is null)
            return Failed(serverId, target.Name, image, "Could not inspect the running image.");

        try
        {
            await docker.PullImageAsync(image, null, serverId);
        }
        catch (Exception ex)
        {
            return Failed(serverId, target.Name, image, $"Could not fetch the candidate image: {ex.Message}");
        }

        var after = await docker.GetImageContractAsync(image, serverId);
        var candidateDigest = await docker.GetImageDigestAsync(image, serverId);
        if (after is null)
            return Failed(serverId, target.Name, image, "Could not inspect the candidate image.");

        var updateAvailable = !string.Equals(currentDigest, candidateDigest, StringComparison.OrdinalIgnoreCase);

        int? closed = null;
        if (updateAvailable && scanCandidate)
            closed = await CountClosedAsync(serverId, target.Id, target.Name, image, ct);

        var tag = TagOf(image);
        var risk = UpdateRiskAssessor.Assess(before, after, tag, tag, closed);

        return new UpdateRiskReport(serverId, target.Name, image, updateAvailable,
            currentDigest, candidateDigest, risk, null);
    }

    /// <summary>
    /// Which vulnerabilities the new image no longer has. Compared by CVE id, not by count: an image can
    /// close five and open three, and a net "-2" would hide both halves.
    /// </summary>
    private async Task<int?> CountClosedAsync(string serverId, string containerId, string name, string image,
        CancellationToken ct)
    {
        var known = cveStore.Get(serverId, containerId);
        if (known is null || known.Findings.Count == 0) return null;   // nothing to compare against

        var candidate = await trivy.ScanContainerImageAsync(serverId, containerId, name, image, ct);
        if (candidate.Error is not null)
        {
            logger.LogInformation(
                "Could not scan the candidate image for {Container}: {Error} — reporting the benefit as " +
                "unknown rather than as zero", name, candidate.Error);
            return null;
        }

        var still = candidate.Findings.Select(f => f.CveId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return known.Findings.Select(f => f.CveId).Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(id => !still.Contains(id));
    }

    /// <summary>The tag part of an image reference, or null. Handles a registry port ("host:5000/app:1.2")
    /// by looking only after the last slash.</summary>
    internal static string? TagOf(string imageRef)
    {
        if (string.IsNullOrWhiteSpace(imageRef)) return null;
        var lastSlash = imageRef.LastIndexOf('/');
        var namePart = lastSlash >= 0 ? imageRef[(lastSlash + 1)..] : imageRef;
        if (namePart.Contains('@')) return null;          // pinned by digest: no tag semantics at all
        var colon = namePart.LastIndexOf(':');
        return colon > 0 ? namePart[(colon + 1)..] : "latest";
    }

    private static UpdateRiskReport Failed(string serverId, string name, string image, string error)
        => new(serverId, name, image, false, null, null, null, error);
}
