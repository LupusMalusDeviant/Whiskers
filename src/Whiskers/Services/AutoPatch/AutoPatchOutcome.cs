using System.Text.RegularExpressions;

namespace Whiskers.Services.AutoPatch;

public enum AutoPatchResultKind
{
    /// <summary>Recreated on the newer image and verified.</summary>
    Updated,
    /// <summary>Already on the newest image for its tag.</summary>
    NoChange,
    /// <summary>Dry run: would have been updated.</summary>
    WouldUpdate,
    /// <summary>The newer image is a new major version — left for a person.</summary>
    SkippedMajor,
    /// <summary>Floating tag without a readable version — a major jump cannot be ruled out.</summary>
    SkippedUnknownMajor,
    /// <summary>The image was built on the host — no registry to pull a newer one from.</summary>
    SkippedLocalBuild,
    /// <summary>Stopped before anything changed (compose drift, unverified dump, no compose labels).</summary>
    Aborted,
    /// <summary>The new image failed its check; the old one runs again and passed the check.</summary>
    RolledBack,
    /// <summary>The new image failed AND the rollback could not be verified. Needs a person now.</summary>
    RollbackNotVerified,
    /// <summary>The script ended without a result line (killed, timed out, host unreachable).</summary>
    Unknown,
}

/// <param name="Detail">The rest of the result line, e.g. "v2.11.4 -> v2.11.7" or "compose-drift run=… file=…".</param>
public sealed record AutoPatchOutcome(AutoPatchResultKind Kind, string Detail)
{
    /// <summary>True when the patch went wrong: nothing verified on the new image, or the old one is back.</summary>
    public bool Failed => IsFailure(Kind);

    public static bool IsFailure(AutoPatchResultKind kind) => kind is AutoPatchResultKind.RolledBack
        or AutoPatchResultKind.RollbackNotVerified or AutoPatchResultKind.Unknown;

    /// <summary>True when the container was actually touched — a history row is owed.</summary>
    public bool Attempted => Kind is AutoPatchResultKind.Updated or AutoPatchResultKind.RolledBack
        or AutoPatchResultKind.RollbackNotVerified;

    /// <summary>True when a person should hear about it tonight, not read it tomorrow.</summary>
    public bool NeedsAttention => Kind is AutoPatchResultKind.RolledBack or AutoPatchResultKind.RollbackNotVerified
        or AutoPatchResultKind.Unknown
        || (Kind is AutoPatchResultKind.Aborted && Detail.StartsWith("db-dump-unverified", StringComparison.Ordinal));

    private static readonly Regex ResultLine = new(@"^UPD-(?<kind>[A-Z-]+?)(?:[ \t]+(?<detail>.*))?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Reads the LAST result line of an img-update.sh log. The last, because a rollback prints the
    /// failure after the attempt; the first would report the attempt.</summary>
    public static AutoPatchOutcome Parse(string? log)
    {
        if (string.IsNullOrWhiteSpace(log))
            return new(AutoPatchResultKind.Unknown, "no output");

        var matches = ResultLine.Matches(log.Replace("\r\n", "\n"));
        if (matches.Count == 0)
            return new(AutoPatchResultKind.Unknown, "no result line");

        var m = matches[^1];
        var detail = m.Groups["detail"].Value.Trim();
        var kind = m.Groups["kind"].Value switch
        {
            "OK" => AutoPatchResultKind.Updated,
            "NOCHANGE" => AutoPatchResultKind.NoChange,
            "WOULD-UPDATE" => AutoPatchResultKind.WouldUpdate,
            "SKIP-MAJOR" => AutoPatchResultKind.SkippedMajor,
            "SKIP-UNKNOWN-MAJOR" => AutoPatchResultKind.SkippedUnknownMajor,
            "SKIP-LOCAL-BUILD" => AutoPatchResultKind.SkippedLocalBuild,
            "ABORT" => AutoPatchResultKind.Aborted,
            "FAILED-ROLLBACK-VERIFIED" => AutoPatchResultKind.RolledBack,
            "FAILED-ROLLBACK-NOT-VERIFIED" => AutoPatchResultKind.RollbackNotVerified,
            _ => AutoPatchResultKind.Unknown,
        };
        return new(kind, detail);
    }
}
