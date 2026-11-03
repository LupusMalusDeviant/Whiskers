using System.Text.RegularExpressions;

namespace Whiskers.Tests;

/// <summary>
/// R1-46 (F-11, TA-49): the contract of <c>deploy/install.sh</c>. The installer runs for real under bash with fake
/// <c>docker</c>, <c>curl</c>, <c>ss</c> and (where a test says so) <c>cosign</c> on the PATH, so these tests see what it
/// writes and prints, not what its text claims:
///
/// <list type="bullet">
/// <item>it installs one released version — the release of this source tree by default, pinned in the compose file —
/// and refuses <c>latest</c> or an unpinned image; the release pipeline refuses a tag the installer would not install;</item>
/// <item>a new install gets a generated vault key in a 0600 file (0700 directory), handed to Whiskers as a secret file
/// (<c>VAULT_KEY_FILE</c>), never printed; a re-run keeps it and refuses an empty one;</item>
/// <item>the output says what to back up (the key and the data, apart, off the host) and where the guide is;</item>
/// <item>a re-run upgrades only the image line (keeping a copy of the compose file) and refuses a downgrade;</item>
/// <item>a bad signature stops the install before anything is pulled or written.</item>
/// </list>
/// </summary>
public class InstallerContractTests : IDisposable
{
    private const string Repository = "ghcr.io/lupusmalusdeviant/whiskers";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("whiskers-installer-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private static string Script => DeploymentScripts.ReadRepoFile("deploy", "install.sh");

    private static string Release()
    {
        var match = Regex.Match(Script, "^WHISKERS_RELEASE=\"(?<v>[^\"]*)\"$", RegexOptions.Multiline);
        Assert.True(match.Success, "install.sh must define WHISKERS_RELEASE=\"X.Y.Z\" on a line of its own");
        return match.Groups["v"].Value;
    }

    // ---- the text --------------------------------------------------------------------------------------------

    [Fact]
    public void The_default_release_is_the_release_of_this_source_tree()
    {
        var release = Release();
        Assert.Matches(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", release);

        var csproj = DeploymentScripts.ReadRepoFile("src", "Whiskers", "Whiskers.csproj");
        Assert.Equal(Regex.Match(csproj, "<Version>([^<]*)</Version>").Groups[1].Value, release);
        var chart = DeploymentScripts.ReadRepoFile("deploy", "helm", "whiskers", "Chart.yaml");
        Assert.Equal(Regex.Match(chart, "^appVersion:\\s*\"?([^\"\\n]*)\"?$", RegexOptions.Multiline).Groups[1].Value.Trim(), release);
    }

    [Fact]
    public void The_installer_never_names_a_latest_image()
    {
        Assert.DoesNotMatch(@"whiskers:latest", Script);
        Assert.DoesNotMatch(@"(?m)^IMAGE(_DEFAULT)?=.*latest", Script);
    }

    [Fact]
    public void The_release_pipeline_refuses_a_tag_the_installer_does_not_install()
    {
        var release = DeploymentScripts.ReadRepoFile(".github", "workflows", "release.yml");
        var verify = release[release.IndexOf("name: Verify tag commit and versions", StringComparison.Ordinal)..];
        verify = verify[..verify.IndexOf("- name:", 10, StringComparison.Ordinal)];
        Assert.Contains("deploy/install.sh", verify);
        Assert.Contains("WHISKERS_RELEASE", verify);
        Assert.Contains("install.sh WHISKERS_RELEASE=${INSTALL_VER}", verify); // checked in the same loop as the chart
    }

    [Fact]
    public void The_installer_is_bash_with_lf_line_endings()
    {
        var raw = File.ReadAllText(DeploymentScripts.RepoFile("deploy", "install.sh"));
        Assert.StartsWith("#!/usr/bin/env bash\n", raw);
        Assert.DoesNotContain("\r", raw);
    }

    // ---- the behaviour ---------------------------------------------------------------------------------------

    [SkippableFact]
    public void A_new_install_pins_the_release_and_hands_over_a_generated_vault_key_as_a_file()
    {
        var run = Install("--yes");
        Assert.True(run.Exit == 0, run.Output);

        var compose = File.ReadAllText(ComposePath);
        Assert.Contains($"    image: {Repository}:{Release()}\n", compose);
        Assert.DoesNotContain("latest", compose);
        Assert.Contains("      - VAULT_KEY_FILE=/run/secrets/whiskers_vault_key\n", compose);
        Assert.Contains("    secrets:\n      - whiskers_vault_key\n", compose);
        Assert.Contains("secrets:\n  whiskers_vault_key:\n    file: ./secrets/vault.key\n", compose);
        Assert.DoesNotMatch(@"(?m)^\s*- VAULT_KEY=", compose); // never as an environment value

        var key = File.ReadAllText(KeyPath);
        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.DoesNotContain(key, run.Output);
        Assert.DoesNotContain(key, compose);
        Assert.DoesNotContain(key, run.DockerLog);

        Assert.Contains("compose pull", run.DockerLog);
        Assert.Contains("compose up -d", run.DockerLog);
    }

    [SkippableFact]
    public void The_vault_key_file_is_private()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no Unix file modes");
        var run = Install("--yes");
        Assert.True(run.Exit == 0, run.Output);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(KeyPath)!));
    }

    [SkippableFact]
    public void The_output_says_what_to_back_up_and_where_the_guide_is()
    {
        var run = Install("--yes");
        Assert.True(run.Exit == 0, run.Output);

        Assert.Contains("Back up", run.Output);
        Assert.Matches(@"The vault key: \S*whiskers/secrets/vault\.key", run.Output);
        Assert.Contains("Without it the secret vault and every encrypted", run.Output);
        Assert.Contains("It is not shown here", run.Output);
        Assert.Contains("Settings → Backup & Restore", run.Output);
        Assert.Contains("off the host", run.Output);
        Assert.Contains("docs/operations/backup-and-recovery.md", run.Output);
        Assert.Contains("Setup token", run.Output);
        Assert.Contains("TOKEN-FROM-STUB", run.Output);
    }

    [SkippableTheory]
    [InlineData("--version", "latest")]
    [InlineData("--version", "1.2")]
    [InlineData("--version", "v1.2.3")]
    [InlineData("--image", "ghcr.io/lupusmalusdeviant/whiskers:latest")]
    [InlineData("--image", "ghcr.io/lupusmalusdeviant/whiskers")]
    [InlineData("--image", "registry.example:5000/whiskers")]
    [InlineData("--verify", "maybe")]
    public void An_unpinned_or_invalid_choice_is_refused_before_anything_happens(string option, string value)
    {
        var run = Install("--yes", option, value);
        Assert.NotEqual(0, run.Exit);
        Assert.False(File.Exists(ComposePath), "nothing may be written");
        Assert.False(File.Exists(KeyPath), "no key may be generated");
        Assert.DoesNotContain("compose pull", run.DockerLog);
    }

    [SkippableFact]
    public void A_pinned_custom_image_is_accepted()
    {
        var run = Install("--yes", "--image", "registry.example:5000/whiskers:1.2.3-fork");
        Assert.True(run.Exit == 0, run.Output);
        Assert.Contains("    image: registry.example:5000/whiskers:1.2.3-fork\n", File.ReadAllText(ComposePath));
        Assert.Contains("is not an image of", run.Output); // its signature cannot be the release's
    }

    [SkippableFact]
    public void A_rerun_upgrades_only_the_image_keeps_the_key_and_refuses_a_downgrade()
    {
        var first = Install("--yes");
        Assert.True(first.Exit == 0, first.Output);
        var key = File.ReadAllText(KeyPath);
        var before = File.ReadAllText(ComposePath);

        var upgrade = Install("--yes", "--version", "99.0.0");
        Assert.True(upgrade.Exit == 0, upgrade.Output);
        var after = File.ReadAllText(ComposePath);
        Assert.Equal(before.Replace($"{Repository}:{Release()}", $"{Repository}:99.0.0"), after);
        Assert.Equal(key, File.ReadAllText(KeyPath));
        var copy = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(ComposePath)!, "docker-compose.yml.bak-*"));
        Assert.Equal(before, File.ReadAllText(copy));
        Assert.Contains("Upgraded from", upgrade.Output);

        var downgrade = Install("--yes", "--version", "98.9.9");
        Assert.NotEqual(0, downgrade.Exit);
        Assert.Contains("Refusing to downgrade", downgrade.Output);
        Assert.Equal(after, File.ReadAllText(ComposePath));
    }

    [SkippableFact]
    public void An_empty_existing_key_file_is_refused()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllText(KeyPath, "");
        var run = Install("--yes");
        Assert.NotEqual(0, run.Exit);
        Assert.Contains("exists but is empty", run.Output);
        Assert.False(File.Exists(ComposePath));
    }

    [SkippableFact]
    public void A_bad_signature_stops_the_install_before_anything_is_pulled_or_written()
    {
        var run = Install(cosignExit: 1, args: ["--yes"]);
        Assert.NotEqual(0, run.Exit);
        Assert.Contains("Signature verification FAILED", run.Output);
        Assert.False(File.Exists(ComposePath));
        Assert.False(File.Exists(KeyPath));
        Assert.DoesNotContain("compose pull", run.DockerLog);
    }

    [SkippableFact]
    public void A_good_signature_is_checked_against_the_release_workflow_identity()
    {
        var run = Install(cosignExit: 0, args: ["--yes", "--verify", "require"]);
        Assert.True(run.Exit == 0, run.Output);
        Assert.Contains("Signature verified", run.Output);
        Assert.Contains($"cosign verify {Repository}:{Release()} --certificate-identity-regexp " +
            "^https://github.com/LupusMalusDeviant/Whiskers/.github/workflows/release.yml@refs/tags/v " +
            "--certificate-oidc-issuer https://token.actions.githubusercontent.com", run.CosignLog);
    }

    [SkippableFact]
    public void Require_without_cosign_is_refused()
    {
        var run = Install("--yes", "--verify", "require");
        Assert.NotEqual(0, run.Exit);
        Assert.Contains("cosign is not installed", run.Output);
        Assert.False(File.Exists(ComposePath));
    }

    // ---- harness ---------------------------------------------------------------------------------------------

    private string WorkDir => Path.Combine(_root.FullName, "work");
    private string ComposePath => Path.Combine(WorkDir, "whiskers", "docker-compose.yml");
    private string KeyPath => Path.Combine(WorkDir, "whiskers", "secrets", "vault.key");

    private sealed record InstallRun(int Exit, string Output, string DockerLog, string CosignLog);

    private InstallRun Install(params string[] args) => Install(cosignExit: null, args: args);

    /// <summary>Runs the real installer in <see cref="WorkDir"/> (it writes <c>./whiskers</c>) with fake tools first on
    /// the PATH. <paramref name="cosignExit"/> null = no fake cosign.</summary>
    private InstallRun Install(int? cosignExit, string[] args)
    {
        var bash = DeploymentScripts.FindBash();
        if (bash is null && DeploymentScripts.InCi) Assert.Fail("bash is required to test deploy/install.sh");
        Skip.If(bash is null, "no bash available to run deploy/install.sh");
        // A real cosign would verify against the network; these tests decide what cosign answers.
        Skip.If(cosignExit is null && DeploymentScripts.FindOnPath("cosign") is not null, "a real cosign is on the PATH");

        Directory.CreateDirectory(WorkDir);
        var bin = Path.Combine(_root.FullName, "bin");
        Directory.CreateDirectory(bin);
        var dockerLog = Path.Combine(_root.FullName, "docker.log");
        var cosignLog = Path.Combine(_root.FullName, "cosign.log");
        DeploymentScripts.WriteExecutable(Path.Combine(bin, "docker"), """
            #!/bin/bash
            printf 'docker %s\n' "$*" >> "$STUB_DOCKER_LOG"
            if [ "$1" = "exec" ]; then printf 'TOKEN-FROM-STUB'; fi
            exit 0
            """);
        DeploymentScripts.WriteExecutable(Path.Combine(bin, "curl"), "#!/bin/bash\nexit 0\n");
        DeploymentScripts.WriteExecutable(Path.Combine(bin, "ss"), "#!/bin/bash\nexit 0\n");
        var cosign = Path.Combine(bin, "cosign");
        if (cosignExit is { } code)
            DeploymentScripts.WriteExecutable(cosign, $"#!/bin/bash\nprintf 'cosign %s\\n' \"$*\" >> \"$STUB_COSIGN_LOG\"\nexit {code}\n");
        else if (File.Exists(cosign))
            File.Delete(cosign);

        var env = new Dictionary<string, string?>
        {
            ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["STUB_DOCKER_LOG"] = DeploymentScripts.ForBash(dockerLog),
            ["STUB_COSIGN_LOG"] = DeploymentScripts.ForBash(cosignLog),
        };
        foreach (var name in new[] { "WHISKERS_VERSION", "WHISKERS_IMAGE", "WHISKERS_VERIFY", "WHISKERS_PORT", "WHISKERS_BIND",
                     "WHISKERS_DATA", "WHISKERS_DIR", "WHISKERS_YES" })
            env[name] = null;

        // The fakes go first on the PATH inside bash: Git for Windows' bash.exe puts its own /usr/bin (with a real curl)
        // in front of the PATH it was given.
        env["STUB_BIN"] = DeploymentScripts.ForBash(bin);
        var script = DeploymentScripts.ForBash(DeploymentScripts.RepoFile("deploy", "install.sh"));
        var (exit, stdout, stderr) = DeploymentScripts.Run(bash!,
            ["-c", "PATH=\"$(cd \"$STUB_BIN\" && pwd):$PATH\"; exec bash \"$0\" \"$@\"", script, .. args], env, WorkDir);
        return new InstallRun(exit, stdout + stderr,
            File.Exists(dockerLog) ? File.ReadAllText(dockerLog) : "",
            File.Exists(cosignLog) ? File.ReadAllText(cosignLog) : "");
    }
}
