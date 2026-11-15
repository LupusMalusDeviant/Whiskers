using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Whiskers.Tests;

/// <summary>Contract 0017 part I3: no Python in the repository. Fails when a tracked file
/// <list type="bullet">
/// <item>has a <c>.py</c> extension;</item>
/// <item>has a python shebang; or</item>
/// <item>is a script or C# source outside the tests and the guardrail matcher's interpreter list
/// (<c>ProtectedResourceMatcher.cs</c>) that invokes <c>python</c>/<c>python3</c>.</item>
/// </list>
/// Comments may name Python; code may not call it.</summary>
public class NoPythonGuardTests
{
    /// <summary>Exempt from the invocation rule: the tests, and the matcher whose interpreter list must name python
    /// to refuse it.</summary>
    private static readonly string[] Exempt = ["src/Whiskers.Tests/", "src/Whiskers/Services/Guardrails/ProtectedResourceMatcher.cs"];

    private static readonly string[] ScriptExtensions = [".sh", ".bash", ".ps1", ".psm1", ".cmd", ".bat", ".cs", ".csx"];

    // "python", "python3", "python3.12" as a word of its own (not "plpython3u", not "python_xyz" identifiers).
    private static readonly Regex Invocation = new(@"(?<![\w.-])python[0-9.]*(?![\w-])", RegexOptions.CultureInvariant);

    [Fact]
    public void No_tracked_file_is_python_or_calls_python()
    {
        var root = DeploymentScripts.RepoRoot();
        var offenders = new List<string>();
        foreach (var path in TrackedFiles(root))
        {
            var full = Path.Combine(root, path);
            if (!File.Exists(full)) continue;   // deleted in the working tree
            offenders.AddRange(Findings(path, File.ReadAllText(full)));
        }
        Assert.True(offenders.Count == 0, "Python in the repository (contract 0017 I3):\n" + string.Join('\n', offenders));
    }

    [Theory]
    [InlineData("tools/x.py", "print(1)\n", true)]
    [InlineData("deploy/x", "#!/usr/bin/env python3\nprint(1)\n", true)]
    [InlineData("deploy/x", "#!/usr/bin/python\n", true)]
    [InlineData("deploy/x.sh", "#!/bin/sh\necho x | python3 -\n", true)]
    [InlineData("deploy/x", "#!/bin/bash\nrepo=$(python -c 'print(1)')\n", true)]
    [InlineData("src/Whiskers/Services/X.cs", "var c = \"echo b64 | base64 -d | python3 -\";\n", true)]
    [InlineData(".github/workflows/ci.yml", "    run: python3 check.py\n", true)]
    [InlineData("deploy/x.sh", "#!/bin/sh\n# no Python here, python3 is not called\necho ok\n", false)]
    [InlineData("src/Whiskers/Services/X.cs", "// a former python3 step\nvar c = \"base64 -w0 f\";\n", false)]
    [InlineData("src/Whiskers/Services/Guardrails/ProtectedResourceMatcher.cs", "\"python\", \"python3\",\n", false)]
    [InlineData("src/Whiskers.Tests/X.cs", "[InlineData(\"python3 stop.py\")]\n", false)]
    [InlineData("docs/x.md", "Run python3 to …\n", false)]
    [InlineData("src/Whiskers/Services/X.cs", "\"LANGUAGE plpython3u\"\n", false)]
    public void The_guard_finds_python_files_shebangs_and_calls_and_ignores_comments_and_exemptions(string path, string text, bool flagged) =>
        Assert.Equal(flagged, Findings(path, text).Any());

    internal static IEnumerable<string> Findings(string path, string text)
    {
        path = path.Replace('\\', '/');
        if (path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{path}: Python file";
            yield break;
        }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var shebang = lines.Length > 0 && lines[0].StartsWith("#!", StringComparison.Ordinal);
        if (shebang && lines[0].Contains("python", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{path}:1: python shebang";
            yield break;
        }
        if (Exempt.Any(e => path.StartsWith(e, StringComparison.Ordinal))) yield break;
        var isWorkflow = path.StartsWith(".github/workflows/", StringComparison.Ordinal)
                         && (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal));
        var isScript = shebang || isWorkflow || path.EndsWith("Dockerfile", StringComparison.Ordinal)
                       || ScriptExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        if (!isScript) yield break;
        var csharp = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);
        for (var i = shebang ? 1 : 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimStart();
            var comment = csharp ? t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith('*') : t.StartsWith('#');
            if (!comment && Invocation.IsMatch(lines[i]))
                yield return $"{path}:{i + 1}: {lines[i].Trim()}";
        }
    }

    private static IEnumerable<string> TrackedFiles(string root)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "core.quotepath=off", "ls-files", "-z" }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("git ls-files did not finish"); }
        Assert.True(process.ExitCode == 0, $"git ls-files failed: {stderr.Result}");
        var files = stdout.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(files.Length > 100, "git ls-files listed almost nothing; the guard would check nothing");
        return files;
    }
}
