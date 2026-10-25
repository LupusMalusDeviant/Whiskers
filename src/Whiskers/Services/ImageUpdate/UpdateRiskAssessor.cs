namespace Whiskers.Services.ImageUpdate;

/// <summary>
/// The part of an image that a container depends on to keep working. Everything here is declared in the
/// image itself and readable with a plain inspect — no guessing, no heuristics over release notes.
/// </summary>
public sealed record ImageContract(
    IReadOnlyList<string> Entrypoint,
    IReadOnlyList<string> Cmd,
    string? User,
    IReadOnlySet<string> ExposedPorts,
    IReadOnlySet<string> Volumes,
    string? WorkingDir,
    bool HasHealthcheck,
    string? Os,
    IReadOnlyDictionary<string, string>? Labels = null)
{
    /// <summary>Compose stamps <c>com.docker.compose.project</c> onto every image it BUILDS; a pulled image
    /// never carries it. The registry digest is no substitute — under the containerd image store a local
    /// build gets a RepoDigest too (AppServer, 2026-10-25).</summary>
    public bool IsComposeBuilt => Labels?.ContainsKey("com.docker.compose.project") == true;

    public static ImageContract Empty { get; } = new([], [], null, new HashSet<string>(), new HashSet<string>(),
        null, false, null);
}

public enum RiskLevel { None, Low, Medium, High }

/// <param name="What">What changed, in the image's own terms.</param>
/// <param name="WhyItMatters">What actually breaks — the sentence somebody needs to decide, not the diff.</param>
public sealed record RiskFinding(RiskLevel Level, string What, string WhyItMatters);

/// <param name="BlindSpots">What this assessment could NOT look at. Never empty: a config diff cannot see
/// inside the program.</param>
public sealed record UpdateRisk(
    RiskLevel Level,
    IReadOnlyList<RiskFinding> Findings,
    /// <summary>How many CVEs the update actually closes, or null when that could not be determined. Null is
    /// NOT zero: reporting an unscanned candidate as "closes 0" would argue against an update on the strength
    /// of a measurement nobody made.</summary>
    int? CvesClosed,
    IReadOnlyList<string> BlindSpots);

/// <summary>
/// Measures what an image update would change about the way a container runs (GAP-6, 2026-08-28).
///
/// <para>The question behind it is "would this break something?", and most of the honest answer is
/// computable: an image declares its entrypoint, its user, the ports it exposes, the volumes it expects and
/// whether it has a healthcheck. If the running container depends on one of those and the new image states it
/// differently, that is not a guess — it is a fact that can be read before anything is recreated.</para>
///
/// <para><b>What this deliberately does not pretend.</b> A configuration diff cannot see inside the program: a
/// schema migration, a changed config-file format, a dropped API. Those break just as hard and leave no trace
/// in the image metadata. So every assessment carries its blind spots, and a "low risk" verdict here means
/// "nothing detectable changed", never "safe". Whiskers has spent this project removing places where absence
/// of a signal read as good news; this would be a new one if the limits were not stated in the answer itself.</para>
/// </summary>
public static class UpdateRiskAssessor
{
    public static UpdateRisk Assess(
        ImageContract running, ImageContract candidate,
        string? currentTag, string? candidateTag, int? cvesClosed)
    {
        var findings = new List<RiskFinding>();

        // The container starts differently. Everything downstream of that is a guess, so it outranks the rest.
        if (!running.Entrypoint.SequenceEqual(candidate.Entrypoint))
            findings.Add(new RiskFinding(RiskLevel.High, "Entrypoint changed",
                $"Starts with [{string.Join(' ', candidate.Entrypoint)}] instead of " +
                $"[{string.Join(' ', running.Entrypoint)}]. Arguments and flags baked into the container may " +
                "no longer apply."));

        if (!running.Cmd.SequenceEqual(candidate.Cmd))
            findings.Add(new RiskFinding(RiskLevel.Medium, "Default command changed",
                $"Default arguments changed from [{string.Join(' ', running.Cmd)}] to " +
                $"[{string.Join(' ', candidate.Cmd)}]. Harmless if the container overrides the command, " +
                "breaking if it relies on the image's default."));

        // The classic silent breakage: same volumes, suddenly the wrong owner.
        if (!string.Equals(running.User ?? "root", candidate.User ?? "root", StringComparison.Ordinal))
            findings.Add(new RiskFinding(RiskLevel.High, "Runs as a different user",
                $"'{running.User ?? "root"}' becomes '{candidate.User ?? "root"}'. Existing volumes and files " +
                "keep their old ownership, so the new process can find itself unable to read or write its own " +
                "data — usually as a crash loop straight after the update."));

        foreach (var gone in running.ExposedPorts.Except(candidate.ExposedPorts))
            findings.Add(new RiskFinding(RiskLevel.High, $"Port {gone} is no longer exposed",
                "Anything pointing at that port — a reverse proxy, another container, a health check — stops " +
                "reaching this service."));

        foreach (var added in candidate.ExposedPorts.Except(running.ExposedPorts))
            findings.Add(new RiskFinding(RiskLevel.Low, $"New port {added} exposed",
                "Nothing breaks by itself, but a service now listens where it did not before."));

        foreach (var gone in running.Volumes.Except(candidate.Volumes))
            findings.Add(new RiskFinding(RiskLevel.High, $"Declared volume {gone} is gone",
                "The image no longer expects data at that path. Anything mounted there may end up ignored — " +
                "the container runs, and writes into nothing."));

        foreach (var added in candidate.Volumes.Except(running.Volumes))
            findings.Add(new RiskFinding(RiskLevel.Medium, $"New declared volume {added}",
                "Docker creates an anonymous volume for it unless the compose file names one. Data written " +
                "there survives nothing and is easy to lose track of."));

        // A monitoring tool should be the last to shrug at this one.
        if (running.HasHealthcheck && !candidate.HasHealthcheck)
            findings.Add(new RiskFinding(RiskLevel.Medium, "Healthcheck removed",
                "The container will report itself as running whatever state it is in. Nothing will notice " +
                "when it stops working."));

        if (!running.HasHealthcheck && candidate.HasHealthcheck)
            findings.Add(new RiskFinding(RiskLevel.Low, "Healthcheck added",
                "Good news, with one caveat: a check tuned for a different environment can mark a healthy " +
                "container unhealthy and trigger restarts."));

        if (!string.IsNullOrWhiteSpace(running.Os) && !string.IsNullOrWhiteSpace(candidate.Os)
            && !string.Equals(running.Os, candidate.Os, StringComparison.OrdinalIgnoreCase))
            findings.Add(new RiskFinding(RiskLevel.Medium, $"Base OS changed: {running.Os} → {candidate.Os}",
                "System libraries change with it. Anything the application shells out to may be a different " +
                "version, or absent."));

        if (!string.Equals(running.WorkingDir, candidate.WorkingDir, StringComparison.Ordinal))
            findings.Add(new RiskFinding(RiskLevel.Low, "Working directory changed",
                $"'{running.WorkingDir}' becomes '{candidate.WorkingDir}'. Relative paths in mounted config " +
                "or scripts resolve elsewhere."));

        findings.AddRange(TagFindings(currentTag, candidateTag));

        var level = findings.Count == 0 ? RiskLevel.None : findings.Max(f => f.Level);
        var blind = BlindSpots(candidateTag).ToList();
        if (cvesClosed is null)
            blind.Insert(0, "How many vulnerabilities this actually closes — the candidate image was not " +
                            "scanned, so the benefit side of the decision is missing.");
        return new UpdateRisk(level, findings, cvesClosed, blind);
    }

    private static IEnumerable<RiskFinding> TagFindings(string? currentTag, string? candidateTag)
    {
        if (string.Equals(currentTag, "latest", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidateTag, "latest", StringComparison.OrdinalIgnoreCase))
        {
            yield return new RiskFinding(RiskLevel.Medium, "Tag is :latest",
                "There is no version to compare. The jump could be a patch or two major releases, and " +
                "nothing in the image says which.");
            yield break;
        }

        if (MajorOf(currentTag) is { } from && MajorOf(candidateTag) is { } to && to > from)
            yield return new RiskFinding(RiskLevel.High, $"Major version jump {from} → {to}",
                "Major releases are where projects are allowed to break things on purpose. Read the release " +
                "notes before this one; nothing in the image metadata will warn you.");
    }

    /// <summary>The leading integer of a version tag, or null when the tag is not versioned that way. Tolerant
    /// on purpose — "v1.10.0", "1.10", "17-alpine" all answer, anything else declines rather than guesses.</summary>
    private static int? MajorOf(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.TrimStart('v', 'V');
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : null;
    }

    /// <summary>
    /// What the assessment cannot see. Always non-empty, and that is the point: this compares declarations,
    /// not behaviour, and a verdict that hid its own limits would be worse than no verdict.
    /// </summary>
    private static IReadOnlyList<string> BlindSpots(string? candidateTag)
    {
        var spots = new List<string>
        {
            "Changes inside the application — database migrations, a changed config-file format, a removed " +
            "API. These break just as hard and leave no trace in the image metadata.",
            "Environment variables the image expects. A new required variable looks like nothing here and " +
            "stops the container on first start.",
            "Data written by the old version that the new one reads differently."
        };

        if (string.Equals(candidateTag, "latest", StringComparison.OrdinalIgnoreCase))
            spots.Add("How far this update actually jumps — the tag does not say.");

        return spots;
    }
}
