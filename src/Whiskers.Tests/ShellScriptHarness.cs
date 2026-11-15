namespace Whiskers.Tests;

/// <summary>Runs a repository shell script under a PATH that holds only stubs and the host tools a test names
/// (contract 0017 part I: the Python-free scripts). A tool left out of both is missing for the script, so the
/// missing-tool paths are testable on any machine. Every wrapped host tool appends its argv to
/// <c>argv.log</c> in <see cref="Root"/>, so a test can prove a secret never stood in a program's arguments. Needs bash (Git Bash on Windows); on CI a missing bash or host
/// tool fails instead of skipping.</summary>
internal sealed class ShellScriptHarness : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("whiskers-shell-");

    public ShellScriptHarness() => Directory.CreateDirectory(Bin);

    public string Root => _root.FullName;
    public string Bin => Path.Combine(Root, "bin");

    /// <summary>A path below <see cref="Root"/>, as bash sees it.</summary>
    public string BashPath(params string[] parts) => DeploymentScripts.ForBash(Path.Combine([Root, .. parts]));

    public void Stub(string name, string body) => DeploymentScripts.WriteExecutable(Path.Combine(Bin, name), body);

    public void Unstub(string name) => File.Delete(Path.Combine(Bin, name));

    /// <summary>Runs <paramref name="script"/> (repository path parts) with <paramref name="shell"/> ("bash" or "sh").
    /// <paramref name="hostTools"/> are wrapped from the host; everything else on the PATH is a stub.</summary>
    public (int Exit, string Stdout, string Stderr) Run(string shell, string[] script, string[] args, IEnumerable<string> hostTools,
        IReadOnlyDictionary<string, string?>? env = null)
    {
        var bash = DeploymentScripts.FindBash();
        if (bash is null && DeploymentScripts.InCi) Assert.Fail("bash is required to test the deploy scripts");
        Skip.If(bash is null, "no bash available to run the deploy scripts");

        var environment = new Dictionary<string, string?>(env ?? new Dictionary<string, string?>())
        {
            ["STUB_BIN"] = DeploymentScripts.ForBash(Bin),
            ["STUB_DIR"] = DeploymentScripts.ForBash(Root),
            ["HOST_TOOLS"] = string.Join(' ', hostTools),
            ["RUN_SHELL"] = shell,
        };
        // Wrap the host tools first (with the full PATH), then narrow the PATH to the stub directory and start the
        // script with an absolute shell, so nothing outside the directory is found by name.
        const string preamble =
            "B=\"$(cd \"$STUB_BIN\" && pwd)\"; " +
            "for t in $HOST_TOOLS; do [ -e \"$B/$t\" ] && continue; " +
            "p=$(command -v \"$t\") || { echo \"host tool missing: $t\" >&2; exit 98; }; " +
            "printf '#!/bin/bash\\necho \"%s $*\" >> \"$STUB_DIR/argv.log\"\\nexec \"%s\" \"$@\"\\n' \"$t\" \"$p\" > \"$B/$t\"; chmod +x \"$B/$t\"; done; " +
            "if [ \"$RUN_SHELL\" = sh ]; then S=$(command -v sh); else S=$BASH; fi; " +
            "PATH=\"$B\"; export PATH; exec \"$S\" \"$0\" \"$@\"";
        var path = DeploymentScripts.ForBash(DeploymentScripts.RepoFile(script));
        var result = DeploymentScripts.Run(bash!, ["-c", preamble, path, .. args], environment, Root);
        if (result.Exit == 98 && result.Stderr.Contains("host tool missing", StringComparison.Ordinal))
        {
            if (DeploymentScripts.InCi) Assert.Fail(result.Stderr);
            Skip.If(true, result.Stderr.Trim());
        }
        return result;
    }

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
