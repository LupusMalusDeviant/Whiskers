using System.Diagnostics;

namespace Whiskers.Tests;

/// <summary>Runs the repository's deployment scripts and tools (bash, sh, helm) for
/// <see cref="InstallerContractTests"/> and <see cref="HelmDeploymentTests"/>.</summary>
internal static class DeploymentScripts
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"could not find Whiskers.slnx above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    public static string RepoFile(params string[] parts) => Path.Combine([RepoRoot(), .. parts]);

    public static string ReadRepoFile(params string[] parts) => File.ReadAllText(RepoFile(parts)).Replace("\r\n", "\n");

    /// <summary>True on a CI runner (GitHub Actions sets <c>CI=true</c>): a missing tool fails there instead of
    /// skipping, so a check cannot disappear silently.</summary>
    public static bool InCi => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>bash: /bin/bash on Unix, Git for Windows' bash.exe on Windows (System32\bash.exe would be WSL).</summary>
    public static string? FindBash()
    {
        if (!OperatingSystem.IsWindows()) return File.Exists("/bin/bash") ? "/bin/bash" : null;
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "Git", "bin", "bash.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The first <paramref name="tool"/> on the PATH (with the Windows executable extensions).</summary>
    public static string? FindOnPath(string tool)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool + ".cmd", tool } : new[] { tool };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>A path as bash sees it: forward slashes (Git Bash reads <c>C:/…</c>).</summary>
    public static string ForBash(string path) => path.Replace('\\', '/');

    public static void WriteExecutable(string path, string body)
    {
        File.WriteAllText(path, body.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static (int Exit, string Stdout, string Stderr) Run(string file, IEnumerable<string> args,
        IReadOnlyDictionary<string, string?>? env = null, string? workingDirectory = null, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // no TTY: the installer must not prompt
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (key, value) in env ?? new Dictionary<string, string?>())
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{file} did not finish within {timeoutMs} ms");
        }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
