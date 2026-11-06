namespace Whiskers.Tests;

/// <summary>
/// Raw and verbatim C# strings keep the line endings of their source file, and many of them become shell scripts and
/// compose files on Linux hosts (onboarding, mTLS renewal guard, allow-list scripts). A CR in such a script is a
/// syntax error in <c>sh</c> — for the renewal guard that means the dead-man rollback never runs. <c>.gitattributes</c>
/// checks <c>*.cs</c>/<c>*.razor</c> out with LF; this test fails the build when a source file the compiler sees
/// still has a CR (a CRLF commit, or a Windows checkout made before that rule — refresh it once).
/// </summary>
public sealed class SourceLineEndingTests
{
    [Fact]
    public void No_compiled_source_file_contains_a_carriage_return()
    {
        var root = DeploymentScripts.RepoRoot();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".razor", StringComparison.Ordinal))
            .Select(p => (Full: p, Relative: Path.GetRelativePath(root, p).Replace('\\', '/')))
            .Where(f => !IsBuildOutput(f.Relative))
            .ToList();
        Assert.True(sources.Count > 500, $"only {sources.Count} source files found under src/ — the scan is not looking at the sources");

        var offenders = sources
            .Where(f => File.ReadAllBytes(f.Full).Contains((byte)'\r'))
            .Select(f => f.Relative)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "CR line endings in source files. Commit or stash your work first, then refresh the checkout once: "
            + "git rm --cached -r -q . && git reset --hard\n"
            + string.Join('\n', offenders.Take(20)));
    }

    [Fact]
    public void The_image_build_refuses_CR_sources_before_publishing()
    {
        var lines = File.ReadAllLines(Path.Combine(DeploymentScripts.RepoRoot(), "Dockerfile"));
        var guard = Array.FindIndex(lines, l => l.Contains("grep -rl --include='*.cs' --include='*.razor'", StringComparison.Ordinal)
                                                && l.Contains("printf '\\r'", StringComparison.Ordinal));
        var lastSourceCopy = Array.FindLastIndex(lines, l => l.StartsWith("COPY src/", StringComparison.Ordinal));
        var publish = Array.FindIndex(lines, l => l.Contains("dotnet publish", StringComparison.Ordinal));

        Assert.True(guard > lastSourceCopy && guard < publish,
            $"the CR guard must run after the last 'COPY src/' (line {lastSourceCopy + 1}) and before 'dotnet publish' (line {publish + 1}); found at line {guard + 1}");
        Assert.Contains("-ne 1 ]", lines[guard + 1], StringComparison.Ordinal);   // only "no match" passes; grep errors fail
    }

    // Relative to the repository root, so a clone under a directory called bin/ or obj/ is still scanned.
    private static bool IsBuildOutput(string relativePath) =>
        relativePath.StartsWith("bin/", StringComparison.Ordinal) || relativePath.Contains("/bin/", StringComparison.Ordinal)
        || relativePath.StartsWith("obj/", StringComparison.Ordinal) || relativePath.Contains("/obj/", StringComparison.Ordinal);
}
