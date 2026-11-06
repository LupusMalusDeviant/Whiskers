using System.Diagnostics;

namespace Whiskers.Tests.ReleaseGate;

/// <summary>
/// Contract 0008, D16 (a) and R18, against the real base. CI's step "Release gate (compare with the base)" sets
/// <c>WHISKERS_GATE_BASE</c>: the pull request's base commit, or <c>previous-tag</c> in a release run (the highest
/// published release strictly below the version that is in the release commit's history, see BaseComparison.ChooseBase).
/// Without the variable there is no base to compare
/// with, and the test has nothing to do — the ordinary runs are not where this check lives.
/// </summary>
public class ReleaseGateBaseTests
{
    public const string BaseVariable = "WHISKERS_GATE_BASE";

    [Fact]
    public void The_records_keep_every_id_and_decision_of_the_base()
    {
        var reference = Environment.GetEnvironmentVariable(BaseVariable);
        var required = Environment.GetEnvironmentVariable(RequiredVariable);
        // CI's comparing step marks itself: there, an empty base (a lost variable, an event without a base SHA) is a
        // finding, never a pass (review m3). Outside that step there is nothing to compare with.
        Assert.False(string.IsNullOrEmpty(reference) && required == "true",
            $"{RequiredVariable}=true but {BaseVariable} is empty: the step meant to compare has no base, which is a finding, never a pass");
        if (string.IsNullOrEmpty(reference)) return;

        var current = new GateSnapshot(GateFiles.Read(GateFiles.Csproj), GateFiles.Read(GateFiles.Invariants),
            GateFiles.Read(GateFiles.ReleaseGateDoc), GateFiles.Read(GateFiles.Contract));
        var label = $"the pull request's base {reference}";
        string? tag = null;
        if (reference == "previous-tag")
        {
            var (exit, tags) = Git("tag", "--list", "v*");
            Assert.True(exit == 0, $"git tag failed: {tags}");
            var releasesFile = Environment.GetEnvironmentVariable(ReleasesVariable);
            var published = !string.IsNullOrEmpty(releasesFile) && File.Exists(releasesFile) ? Lines(File.ReadAllText(releasesFile)) : null;
            var choice = BaseComparison.ChooseBase(Lines(tags), published, IsAncestorOfHead, VersionAt, GateFiles.Version());
            Assert.True(choice.Problems.Count == 0 && choice.Tag is not null, "The previous release:\n" + string.Join("\n", choice.Problems));
            reference = tag = choice.Tag!;
            label = $"the previous release {tag}";
        }
        var (verify, output) = Git("rev-parse", "--verify", "--quiet", reference + "^{commit}");
        Assert.True(verify == 0, $"{label} cannot be read ({output.Trim()}): a missing base is a finding, never a pass");

        var baseline = new GateSnapshot(Show(reference, GateFiles.Csproj), Show(reference, GateFiles.Invariants),
            Show(reference, GateFiles.ReleaseGateDoc), Show(reference, GateFiles.Contract));
        var findings = BaseComparison.Compare(current, baseline, label, tag);
        Assert.True(findings.Count == 0, $"Against {label}:\n" + string.Join("\n", findings));
    }

    /// <summary>Set to <c>true</c> by the CI step that compares: there, a missing base fails.</summary>
    public const string RequiredVariable = "WHISKERS_GATE_BASE_REQUIRED";

    /// <summary>A file listing the published GitHub releases (one tag per line), written by the CI step in a release run.</summary>
    public const string ReleasesVariable = "WHISKERS_GATE_RELEASES";

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A git failure that is not a definite answer: the ancestry is unknown, so it fails instead of choosing.</summary>
    private static bool IsAncestorOfHead(string tag)
    {
        var (exit, output) = Git("merge-base", "--is-ancestor", tag + "^{commit}", "HEAD");
        if (exit is 0 or 1) return exit == 0;
        throw new InvalidOperationException($"git merge-base failed for {tag} ({output.Trim()}): the ancestry is unknown");
    }

    private static GateVersion? VersionAt(string tag)
    {
        try { return Show(tag, GateFiles.Csproj) is { } csproj ? ReleaseGate.ReadVersion(csproj) : null; }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// A file at a commit, or null when the commit's tree has no such path (review round 2 m-A). "Absent" is decided by
    /// <c>git ls-tree</c>, which answers with an empty list; any failure of either command (a missing object after a
    /// shallow fetch, a corrupt object, a bad reference) fails the test instead of reading as an empty base.
    /// </summary>
    private static string? Show(string reference, string path)
    {
        var (listed, entries) = Git("ls-tree", "--name-only", reference, "--", path);
        Assert.True(listed == 0, $"git ls-tree {reference} -- {path} failed ({entries.Trim()}): what the gate cannot read is a finding");
        if (entries.Trim().Length == 0) return null;
        var (exit, text) = Git("show", $"{reference}:{path}");
        Assert.True(exit == 0, $"git show {reference}:{path} failed ({text.Trim()}) although the path exists: what the gate cannot read is a finding");
        return text.Replace("\r\n", "\n");
    }

    private static (int Exit, string Output) Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = GateFiles.Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("git did not finish"); }
        return (process.ExitCode, process.ExitCode == 0 ? stdout.Result : stderr.Result);
    }
}
