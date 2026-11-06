using System.Diagnostics;

namespace Whiskers.Tests.ReleaseGate;

/// <summary>
/// Contract 0008, D16 (a) and R18, against the real base. CI's step "Release gate (compare with the base)" sets
/// <c>WHISKERS_GATE_BASE</c>: the pull request's base commit, or <c>previous-tag</c> in a release run (the highest
/// vX.Y.Z tag strictly below the version, tags fetched by that step). Without the variable there is no base to compare
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
            Assert.True(!string.IsNullOrEmpty(releasesFile) && File.Exists(releasesFile),
                $"{ReleasesVariable} names no file of published releases: without it a pushed tag could choose the base");
            var previous = BaseComparison.PreviousRelease(Lines(tags), Lines(File.ReadAllText(releasesFile!)), GateFiles.Version());
            Assert.True(previous is not null, $"no published release below {GateFiles.Version()} was found: a missing base is a finding, never a pass (were the tags fetched?)");
            reference = tag = previous!;
            label = $"the previous release {previous}";
            // A published release that is not in the release commit's history is not its predecessor (review M3).
            var (ancestor, why) = Git("merge-base", "--is-ancestor", previous + "^{commit}", "HEAD");
            Assert.True(ancestor == 0, $"{label} is not an ancestor of the release commit ({why.Trim()}): not a base (was the history fetched?)");
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

    private static string? Show(string reference, string path)
    {
        var (exit, text) = Git("show", $"{reference}:{path}");
        return exit == 0 ? text.Replace("\r\n", "\n") : null;
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
