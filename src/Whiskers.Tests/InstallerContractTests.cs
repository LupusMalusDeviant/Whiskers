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

    /// <summary>The pipeline releases only vX.Y.Z tags — exactly the versions the installer accepts — so no release can
    /// ship an installer that refuses its own default (a pre-release such as v1.0.0-rc.1 is refused at prepare).</summary>
    [Fact]
    public void The_pipeline_releases_exactly_the_versions_the_installer_accepts()
    {
        const string Core = @"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$";
        var release = DeploymentScripts.ReadRepoFile(".github", "workflows", "release.yml");
        Assert.Contains("grep -Eq '^v" + Core + "'", release);
        Assert.Contains("SEMVER_RE='^" + Core + "'", Script);
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
    [InlineData("--image", "Registry.example/whiskers:1.0.0")]
    [InlineData("--image", "example/whiskers:1.0.0&x")]
    [InlineData("--image", "example/whiskers:1.0.0#x")]
    [InlineData("--image", "example/whiskers:1.0.0\n    privileged: true")]
    [InlineData("--image", "example/whiskers@sha256:1234")]
    [InlineData("--version", "1.0.0-rc.1")]
    [InlineData("--verify", "maybe")]
    [InlineData("--bind", "localhost")]
    [InlineData("--bind", "1.2.3.256")]
    [InlineData("--bind", "127.0.0.1\n    privileged: true")]
    [InlineData("--port", "70000")]
    [InlineData("--port", "80x")]
    [InlineData("--data", "/srv/a:/etc")]
    [InlineData("--data", "data#x")]
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
    public void A_digest_reference_is_accepted_and_written_as_given()
    {
        var image = $"{Repository}@sha256:{new string('a', 64)}";
        var run = Install("--yes", "--image", image, "--verify", "off");
        Assert.True(run.Exit == 0, run.Output);
        Assert.Contains($"    image: {image}\n", File.ReadAllText(ComposePath));
    }

    [SkippableFact]
    public void An_ipv6_bind_address_in_brackets_is_accepted()
    {
        var run = Install("--yes", "--bind", "[::1]", "--port", "5200");
        Assert.True(run.Exit == 0, run.Output);
        Assert.Contains("      - \"[::1]:5200:8080\"\n", File.ReadAllText(ComposePath));
        Assert.Contains("http://[::1]:5200/healthz", run.CurlLog);
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

    /// <summary>An install of <c>:latest</c> by an older installer: the version comes from what the running Whiskers
    /// recorded at its last start; a newer one refuses the change, an unknown one needs the explicit override.</summary>
    [SkippableTheory]
    [InlineData("99.0.0", false, false, "Refusing to downgrade from 99.0.0")]
    [InlineData("99.0.0+abc123", false, false, "Refusing to downgrade from 99.0.0")]
    [InlineData("99.0.0", true, false, "Refusing to downgrade from 99.0.0")] // the override does not cover a known newer release
    [InlineData("", false, false, "Cannot tell which release")]
    [InlineData("0.0.1", false, true, null)]
    [InlineData("", true, true, null)]
    public void An_install_of_latest_is_only_changed_when_it_is_not_newer(string recorded, bool allowUnknown, bool changes, string? message)
    {
        var first = Install("--yes");
        Assert.True(first.Exit == 0, first.Output);
        var latest = File.ReadAllText(ComposePath).Replace($"{Repository}:{Release()}", $"{Repository}:latest", StringComparison.Ordinal);
        File.WriteAllText(ComposePath, latest);

        _lastStartedVersion = recorded;
        var run = allowUnknown ? Install("--yes", "--allow-unknown-current") : Install("--yes");

        if (changes)
        {
            Assert.True(run.Exit == 0, run.Output);
            Assert.Contains($"    image: {Repository}:{Release()}\n", File.ReadAllText(ComposePath));
        }
        else
        {
            Assert.NotEqual(0, run.Exit);
            Assert.Contains(message!, run.Output);
            Assert.Equal(latest, File.ReadAllText(ComposePath));
        }
    }

    [SkippableFact]
    public void The_recorded_version_also_stops_a_downgrade_below_a_pinned_compose_file()
    {
        Assert.True(Install("--yes", "--version", "1.0.0").Exit == 0);
        _lastStartedVersion = "2.0.0"; // someone ran 2.0.0 on this data by hand
        var run = Install("--yes", "--version", "1.5.0");
        Assert.NotEqual(0, run.Exit);
        Assert.Contains("Refusing to downgrade from 2.0.0 to 1.5.0", run.Output);
    }

    [SkippableTheory]
    [InlineData("foreign")]
    [InlineData("two-images")]
    public void A_compose_file_the_installer_cannot_own_is_left_alone(string kind)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ComposePath)!);
        var text = kind == "foreign"
            ? "services:\n  whiskers:\n    image: example/whiskers:1.0.0\n"
            : "# Generated by the Whiskers installer.\nservices:\n  whiskers:\n    image: a/b:1.0.0\n  other:\n    image: c/d:1.0.0\n";
        File.WriteAllText(ComposePath, text);
        var run = Install("--yes", "--version", "99.0.0");
        Assert.NotEqual(0, run.Exit);
        Assert.Contains(kind == "foreign" ? "was not written by this installer" : "exactly one 'image:' line", run.Output);
        Assert.Equal(text, File.ReadAllText(ComposePath));
        Assert.DoesNotContain("compose pull", run.DockerLog);
    }

    [SkippableFact]
    public void A_rerun_keeps_the_port_and_bind_of_the_install_and_says_new_values_are_ignored()
    {
        Assert.True(Install("--yes", "--port", "8080").Exit == 0);
        var compose = File.ReadAllText(ComposePath);

        var rerun = Install("--yes", "--version", "99.0.0");
        Assert.True(rerun.Exit == 0, rerun.Output);
        Assert.Contains("http://127.0.0.1:8080/healthz", rerun.CurlLog);
        Assert.DoesNotContain(":5100/", rerun.CurlLog);
        Assert.Contains("http://127.0.0.1:8080", rerun.Output);

        var ignored = Install("--yes", "--version", "99.0.0", "--port", "9000", "--bind", "0.0.0.0");
        Assert.True(ignored.Exit == 0, ignored.Output);
        Assert.Contains("--port --bind only apply to a new install and are ignored", ignored.Output);
        Assert.Contains("\"127.0.0.1:8080:8080\"", File.ReadAllText(ComposePath));
        Assert.Equal(compose.Replace($"{Repository}:{Release()}", $"{Repository}:99.0.0"), File.ReadAllText(ComposePath));
    }

    [SkippableFact]
    public void An_upgrade_keeps_the_compose_files_mode_and_warns_that_the_copy_may_hold_secrets()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no Unix file modes");
        Assert.True(Install("--yes").Exit == 0);
        File.SetUnixFileMode(ComposePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var run = Install("--yes", "--version", "99.0.0");
        Assert.True(run.Exit == 0, run.Output);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ComposePath));
        var copy = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(ComposePath)!, "docker-compose.yml.bak-*"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(copy));
        Assert.Contains("It may contain secrets", run.Output);
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
    public void A_good_signature_is_checked_against_exactly_this_release_and_its_digest_is_pinned()
    {
        var run = Install(cosignExit: 0, args: ["--yes", "--verify", "require"]);
        Assert.True(run.Exit == 0, run.Output);
        Assert.Contains("Signature verified", run.Output);
        var escaped = Release().Replace(".", "\\.", StringComparison.Ordinal);
        Assert.Contains($"cosign verify {Repository}:{Release()} --certificate-identity-regexp " +
            $"^https://github\\.com/LupusMalusDeviant/Whiskers/\\.github/workflows/release\\.yml@refs/tags/v{escaped}$ " +
            "--certificate-oidc-issuer https://token.actions.githubusercontent.com", run.CosignLog);
        // The verified digest is what the compose file names: a tag moved after the check pulls nothing else.
        Assert.Contains($"    image: {Repository}:{Release()}@{StubDigest}\n", File.ReadAllText(ComposePath));
    }

    [SkippableTheory]
    [InlineData("")]           // cosign said nothing about a digest
    [InlineData("two")]        // two different digests
    public void A_verification_without_exactly_one_digest_stops_the_install(string digests)
    {
        _cosignDigests = digests == "two" ? [StubDigest, "sha256:" + new string('b', 64)] : [];
        var run = Install(cosignExit: 0, args: ["--yes"]);
        Assert.NotEqual(0, run.Exit);
        Assert.Contains("did not name exactly one signed digest", run.Output);
        Assert.False(File.Exists(ComposePath));
    }

    [SkippableFact]
    public void A_verified_digest_other_than_the_one_named_stops_the_install()
    {
        var run = Install(cosignExit: 0, args: ["--yes", "--image", $"{Repository}@sha256:{new string('c', 64)}"]);
        Assert.NotEqual(0, run.Exit);
        Assert.Contains("not the one", run.Output);
        Assert.False(File.Exists(ComposePath));
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

    private sealed record InstallRun(int Exit, string Output, string DockerLog, string CosignLog, string CurlLog);

    private static readonly string StubDigest = "sha256:" + new string('d', 64);
    /// <summary>What the fake Whiskers recorded at its last start (<c>/app/data/.last-started-version</c>); empty = no file.</summary>
    private string _lastStartedVersion = "";
    /// <summary>The digests the fake cosign reports as verified.</summary>
    private string[] _cosignDigests = [StubDigest];

    private InstallRun Install(params string[] args) => Install(cosignExit: null, args: args);

    /// <summary>Runs the real installer in <see cref="WorkDir"/> (it writes <c>./whiskers</c>) with fake tools first on
    /// the PATH. <paramref name="cosignExit"/> null = no cosign at all: a real one is taken off the PATH, so every test
    /// decides what cosign answers and none is skipped for it.</summary>
    private InstallRun Install(int? cosignExit, string[] args)
    {
        var bash = DeploymentScripts.FindBash();
        if (bash is null && DeploymentScripts.InCi) Assert.Fail("bash is required to test deploy/install.sh");
        Skip.If(bash is null, "no bash available to run deploy/install.sh");

        Directory.CreateDirectory(WorkDir);
        var bin = Path.Combine(_root.FullName, "bin");
        Directory.CreateDirectory(bin);
        var dockerLog = Path.Combine(_root.FullName, "docker.log");
        var cosignLog = Path.Combine(_root.FullName, "cosign.log");
        var curlLog = Path.Combine(_root.FullName, "curl.log");
        foreach (var log in new[] { dockerLog, cosignLog, curlLog }) File.Delete(log); // each run reports its own calls
        DeploymentScripts.WriteExecutable(Path.Combine(bin, "docker"), """
            #!/bin/bash
            printf 'docker %s\n' "$*" >> "$STUB_DOCKER_LOG"
            case "$*" in
              *last-started-version*) if [ -n "$STUB_LAST_VERSION" ]; then printf '%s\n' "$STUB_LAST_VERSION"; exit 0; fi; exit 1;;
              exec*) printf 'TOKEN-FROM-STUB';;
              inspect*) exit 1;;
            esac
            exit 0
            """);
        DeploymentScripts.WriteExecutable(Path.Combine(bin, "curl"), "#!/bin/bash\nprintf 'curl %s\\n' \"$*\" >> \"$STUB_CURL_LOG\"\nexit 0\n");
        DeploymentScripts.WriteExecutable(Path.Combine(bin, "ss"), "#!/bin/bash\nexit 0\n");
        var cosign = Path.Combine(bin, "cosign");
        if (cosignExit is { } code)
        {
            var json = string.Join(",", _cosignDigests.Select(d => $"{{\"critical\":{{\"image\":{{\"docker-manifest-digest\":\"{d}\"}}}}}}"));
            DeploymentScripts.WriteExecutable(cosign,
                $"#!/bin/bash\nprintf 'cosign %s\\n' \"$*\" >> \"$STUB_COSIGN_LOG\"\n" +
                $"if [ {code} = 0 ]; then printf '%s\\n' '[{json}]'; echo 'Verification for x -- The following checks were performed' >&2; fi\nexit {code}\n");
        }
        else if (File.Exists(cosign))
            File.Delete(cosign);

        // A real cosign would verify against the network: its directories are left off the PATH.
        var path = string.Join(Path.PathSeparator,
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(dir => !File.Exists(Path.Combine(dir, "cosign")) && !File.Exists(Path.Combine(dir, "cosign.exe"))));
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = bin + Path.PathSeparator + path,
            ["STUB_DOCKER_LOG"] = DeploymentScripts.ForBash(dockerLog),
            ["STUB_COSIGN_LOG"] = DeploymentScripts.ForBash(cosignLog),
            ["STUB_CURL_LOG"] = DeploymentScripts.ForBash(curlLog),
            ["STUB_LAST_VERSION"] = _lastStartedVersion,
        };
        foreach (var name in new[] { "WHISKERS_VERSION", "WHISKERS_IMAGE", "WHISKERS_VERIFY", "WHISKERS_PORT", "WHISKERS_BIND",
                     "WHISKERS_DATA", "WHISKERS_DIR", "WHISKERS_YES", "WHISKERS_ALLOW_UNKNOWN_CURRENT" })
            env[name] = null;

        // The fakes go first on the PATH inside bash: Git for Windows' bash.exe puts its own /usr/bin (with a real curl)
        // in front of the PATH it was given.
        env["STUB_BIN"] = DeploymentScripts.ForBash(bin);
        var script = DeploymentScripts.ForBash(DeploymentScripts.RepoFile("deploy", "install.sh"));
        var (exit, stdout, stderr) = DeploymentScripts.Run(bash!,
            ["-c", "PATH=\"$(cd \"$STUB_BIN\" && pwd):$PATH\"; " +
                   "if command -v cosign >/dev/null && [ \"$(dirname \"$(command -v cosign)\")\" != \"$(cd \"$STUB_BIN\" && pwd)\" ]; then " +
                   "echo 'a real cosign is still on the PATH' >&2; exit 97; fi; exec bash \"$0\" \"$@\"", script, .. args], env, WorkDir);
        Assert.True(exit != 97, stderr);
        return new InstallRun(exit, stdout + stderr,
            File.Exists(dockerLog) ? File.ReadAllText(dockerLog) : "",
            File.Exists(cosignLog) ? File.ReadAllText(cosignLog) : "",
            File.Exists(curlLog) ? File.ReadAllText(curlLog) : "");
    }
}
