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
        if (string.IsNullOrEmpty(reference)) return;

        var current = new GateSnapshot(GateFiles.Read(GateFiles.Csproj), GateFiles.Read(GateFiles.Invariants),
            GateFiles.Read(GateFiles.ReleaseGateDoc), GateFiles.Read(GateFiles.Contract));
        var label = $"the pull request's base {reference}";
        if (reference == "previous-tag")
        {
            var (exit, tags) = Git("tag", "--list", "v*");
            Assert.True(exit == 0, $"git tag failed: {tags}");
            var previous = BaseComparison.PreviousTag(tags.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), GateFiles.Version());
            Assert.True(previous is not null, $"no release tag below {GateFiles.Version()} was found: a missing base is a finding, never a pass (were the tags fetched?)");
            reference = previous!;
            label = $"the previous release tag {previous}";
        }
        var (verify, output) = Git("rev-parse", "--verify", "--quiet", reference + "^{commit}");
        Assert.True(verify == 0, $"{label} cannot be read ({output.Trim()}): a missing base is a finding, never a pass");

        var baseline = new GateSnapshot(Show(reference, GateFiles.Csproj), Show(reference, GateFiles.Invariants),
            Show(reference, GateFiles.ReleaseGateDoc), Show(reference, GateFiles.Contract));
        var findings = BaseComparison.Compare(current, baseline, label);
        Assert.True(findings.Count == 0, $"Against {label}:\n" + string.Join("\n", findings));
    }

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
