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
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".razor", StringComparison.Ordinal))
            .Where(p => !IsBuildOutput(p))
            .Where(p => File.ReadAllBytes(p).Contains((byte)'\r'))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "CR line endings in source files. Commit or stash your work first, then refresh the checkout once: "
            + "git rm --cached -r -q . && git reset --hard\n"
            + string.Join('\n', offenders.Take(20)));
    }

    private static bool IsBuildOutput(string path)
    {
        var p = path.Replace('\\', '/');
        return p.Contains("/bin/", StringComparison.Ordinal) || p.Contains("/obj/", StringComparison.Ordinal);
    }
}
