using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using YamlDotNet.RepresentationModel;

namespace Whiskers.Tests;

/// <summary>
/// R1-52 (F-10, F-19): the inputs of a build are fixed and every one of them has an update path.
///
/// <list type="bullet">
/// <item>Locked restore: every project in <c>Whiskers.slnx</c> has a <c>packages.lock.json</c>
/// (<c>Directory.Build.props</c> turns them on), CI restores with <c>--locked-mode</c> and builds without a second
/// restore, and so does the Dockerfile — a lock file that does not match its project fails the build.</item>
/// <item>Complete update coverage: Dependabot covers every project's NuGet directory, the SDK in <c>global.json</c>,
/// the Dockerfile's base images and the workflows' actions. A new project without coverage fails here.</item>
/// <item>Pinned bytes: every base image is <c>image:tag@sha256:…</c>, and the SDK image is the exact SDK of
/// <c>global.json</c> (the SDK writes its own framework packages into the lock files).</item>
/// </list>
///
/// The action SHA pins themselves are checked by <see cref="ReleaseWorkflowTests"/>. Every check here also runs
/// against a mutated copy of the real file, so none of them can pass by finding nothing.
/// </summary>
public class BuildReproducibilityTests
{
    // ---- the real files ------------------------------------------------------------------------------------

    [Fact]
    public void Every_project_under_src_is_in_the_solution()
    {
        var inSolution = BuildPolicy.SolutionProjects(Repo("Whiskers.slnx")).ToHashSet(StringComparer.Ordinal);
        var onDisk = Directory.EnumerateFiles(DeploymentScripts.RepoFile("src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(p => Path.GetRelativePath(DeploymentScripts.RepoRoot(), p).Replace('\\', '/'))
            .ToList();
        Assert.NotEmpty(onDisk);
        Assert.All(onDisk, p => Assert.Contains(p, inSolution));
    }

    [Fact]
    public void Every_project_has_a_lock_file_and_none_opts_out()
    {
        var projects = BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"));
        Assert.Equal(5, projects.Count); // the check must see the projects; raise with a new project
        Assert.Empty(BuildPolicy.LockFileViolations(Repo("Directory.Build.props"), projects,
            p => File.Exists(DeploymentScripts.RepoFile(p)) ? DeploymentScripts.ReadRepoFile(p) : null));
    }

    [Fact]
    public void Ci_restores_locked_and_never_restores_again()
    {
        var restores = BuildPolicy.WorkflowRestoreViolations(Repo(".github", "workflows", "ci.yml"), out var lockedRestores);
        Assert.True(lockedRestores > 0, "ci.yml has no locked `dotnet restore`");
        Assert.Empty(restores);
    }

    /// <summary>Every workflow file, also one added later: none may restore unlocked or set up another SDK.</summary>
    public static TheoryData<string> Workflows()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(DeploymentScripts.RepoFile(".github", "workflows"), "*.yml").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void No_workflow_restores_unlocked(string workflow)
        => Assert.Empty(BuildPolicy.WorkflowRestoreViolations(Repo(".github", "workflows", workflow), out _));

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Every_workflow_sets_up_the_sdk_of_global_json(string workflow)
        => Assert.Empty(BuildPolicy.CiSdkViolations(Repo(".github", "workflows", workflow)));

    [Fact]
    public void Ci_sets_up_the_sdk_at_all()
        => Assert.Contains("global-json-file: global.json", Repo(".github", "workflows", "ci.yml"));

    [Fact]
    public void Dockerfile_restores_locked_with_every_lock_file()
        => Assert.Empty(BuildPolicy.DockerfileRestoreViolations(Repo("Dockerfile"), BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"))));

    [Fact]
    public void Every_base_image_is_pinned_by_digest()
    {
        var dockerfile = Repo("Dockerfile");
        Assert.True(BuildPolicy.BaseImages(dockerfile).Count >= 3, "the Dockerfile's FROM lines were not found");
        Assert.Empty(BuildPolicy.UnpinnedBaseImages(dockerfile));
    }

    [Fact]
    public void Sdk_image_is_the_exact_sdk_of_global_json()
        => Assert.Empty(BuildPolicy.SdkPinViolations(Repo("global.json"), Repo("Dockerfile")));

    [Fact]
    public void Dependabot_covers_every_project_the_sdk_the_images_and_the_actions()
        => Assert.Empty(BuildPolicy.DependabotViolations(Repo(".github", "dependabot.yml"), BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"))));

    // ---- each check bites: the real file, mutated -----------------------------------------------------------

    [Theory]
    [InlineData("      - \"/src/Whiskers.Data\"\n", "")]                                   // a project without NuGet coverage
    [InlineData("package-ecosystem: \"dotnet-sdk\"", "package-ecosystem: \"bundler\"")]   // the SDK uncovered
    [InlineData("package-ecosystem: \"docker\"", "package-ecosystem: \"npm\"")]           // the base images uncovered
    [InlineData("package-ecosystem: \"github-actions\"", "package-ecosystem: \"pip\"")]   // the actions uncovered
    public void Dependabot_check_finds_a_gap(string original, string mutation)
    {
        var real = Repo(".github", "dependabot.yml");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.DependabotViolations(real.Replace(original, mutation), BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"))));
    }

    [Fact]
    public void Dependabot_check_finds_a_new_project_without_coverage()
    {
        var projects = BuildPolicy.SolutionProjects(Repo("Whiskers.slnx")).Append("src/Whiskers.New/Whiskers.New.csproj").ToList();
        Assert.Contains(BuildPolicy.DependabotViolations(Repo(".github", "dependabot.yml"), projects), v => v.Contains("Whiskers.New"));
    }

    [Fact]
    public void Lock_file_check_finds_a_missing_lock_file_and_an_opt_out()
    {
        var projects = BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"));
        string? Read(string p) => File.Exists(DeploymentScripts.RepoFile(p)) ? DeploymentScripts.ReadRepoFile(p) : null;

        Assert.Contains(BuildPolicy.LockFileViolations(Repo("Directory.Build.props"), projects,
                p => p == "src/Whiskers.Data/packages.lock.json" ? null : Read(p)),
            v => v.Contains("Whiskers.Data"));
        Assert.NotEmpty(BuildPolicy.LockFileViolations(
            Repo("Directory.Build.props").Replace("<RestorePackagesWithLockFile>true<", "<RestorePackagesWithLockFile>false<"), projects, Read));
        Assert.Contains(BuildPolicy.LockFileViolations(Repo("Directory.Build.props"), projects,
                p => p == "src/Whiskers.Tests/Whiskers.Tests.csproj"
                    ? Read(p)!.Replace("<IsPackable>false</IsPackable>", "<IsPackable>false</IsPackable><RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>")
                    : Read(p)),
            v => v.Contains("Whiskers.Tests"));
    }


    [Theory]
    [InlineData("run: bash .github/scripts/restore-locked.sh", "run: dotnet restore Whiskers.slnx")]                 // unlocked restore
    [InlineData("bash .github/scripts/restore-locked.sh\n", "bash .github/scripts/restore-locked.sh || true\n")]     // the script's failure ignored
    [InlineData("bash .github/scripts/restore-locked.sh\n", "bash .github/scripts/restore-locked.sh --no-lock\n")]   // anything but the plain call
    [InlineData("dotnet build Whiskers.slnx --no-restore --configuration Release", "dotnet build Whiskers.slnx --configuration Release")] // implicit restore
    public void Workflow_restore_check_finds_an_unlocked_restore(string original, string mutation)
    {
        var real = Repo(".github", "workflows", "ci.yml");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.WorkflowRestoreViolations(real.Replace(original, mutation), out _));
    }

    /// <summary>A restore step cannot be told to pass on a failure (continue-on-error, set +e).</summary>
    [Theory]
    [MemberData(nameof(Workflows))]
    public void No_restore_step_ignores_its_failure(string workflow)
        => Assert.Empty(BuildPolicy.RestoreStepViolations(Repo(".github", "workflows", workflow)));

    [Theory]
    [InlineData("      - name: Restore\n        run: bash", "      - name: Restore\n        continue-on-error: true\n        run: bash")]
    [InlineData("          set -euo pipefail\n          bash .github/scripts/restore-locked.sh\n          dotnet build src/Whiskers.Tests",
                "          set -euo pipefail\n          set +e\n          bash .github/scripts/restore-locked.sh\n          dotnet build src/Whiskers.Tests")]
    [InlineData("  build-test:\n    runs-on: ubuntu-latest\n", "  build-test:\n    runs-on: ubuntu-latest\n    continue-on-error: true\n")]
    public void Restore_step_check_finds_an_ignored_failure(string original, string mutation)
    {
        var real = Repo(".github", "workflows", "ci.yml");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.RestoreStepViolations(real.Replace(original, mutation)));
    }

    // ---- the restore script and the Dockerfile's restore retry only what a retry can fix --------------------------

    [Fact]
    public void Restore_script_restores_locked_and_fails_closed()
        => Assert.Empty(BuildPolicy.RestoreScriptViolations(Repo(".github", "scripts", "restore-locked.sh")));

    [Fact]
    public void Every_workflow_restore_goes_through_the_script_or_is_locked()
    {
        foreach (var workflow in new[] { "ci.yml", "security.yml" })
        {
            Assert.Empty(BuildPolicy.WorkflowRestoreViolations(Repo(".github", "workflows", workflow), out var locked));
            Assert.True(locked > 0, $"{workflow} has no locked restore");
        }
    }

    [Theory]
    [InlineData("then echo \"::error::the locked restore failed three times (last exit $status)\"; exit 1; fi",
                "then echo \"::error::the locked restore failed three times (last exit $status)\"; break; fi")]     // third failure falls through
    [InlineData("does not match (exit $status), not retried\"; exit 1; fi", "does not match (exit $status), not retried\"; break; fi")]       // a lock mismatch passes
    [InlineData("does not match (exit $status), not retried\"; exit 1; fi", "does not match (exit $status), not retried\"; continue; fi")]    // a lock mismatch is retried
    [InlineData("missing on the source (exit $status), not retried\"; exit 1; fi", "missing on the source (exit $status), not retried\"; continue; fi")] // a missing package is retried
    [InlineData("'NU1004|NU1005|NU1403'", "'NU1004|NU1005'")]                                                     // a hash mismatch is retried
    [InlineData("'NU1101|NU1102' \"$log\"", "'NU1101' \"$log\"")]                                                 // an unlisted version is retried
    [InlineData("&& ! grep -Eq 'NU1301|NU1801'", "&& grep -Eq 'NU1301|NU1801'")]                                  // a missing package is retried without a source failure
    [InlineData("! grep -Eq 'NU1301|NU1801'", "! grep -Eq 'NU1301|NU1801|NU1102'")]                               // an unlisted version counts as a source failure
    [InlineData("if [ \"$status\" -eq 0 ]; then exit 0; fi", "if [ \"$status\" -ge 0 ]; then exit 0; fi")]        // every failure succeeds
    [InlineData("if [ \"$status\" -eq 0 ]; then exit 0; fi", "if true; then exit 0; fi")]                         // every failure succeeds
    [InlineData("  status=0\n", "  status=0\n  [ -f \"$log\" ] && exit 0\n")]                                     // another way out with success
    [InlineData("done\nexit 1\n", "done\nexit 0\n")]                                                              // the end of the loop succeeds
    [InlineData("for attempt in 1 2 3; do", "for attempt in 1 2 3 4 5 6 7 8 9; do")]                              // retries without bound
    [InlineData("set -euo pipefail", "set -eu")]                                                                  // tee hides the exit code
    [InlineData(" || status=$?", "; status=0")]                                                                   // the exit code is dropped
    [InlineData("dotnet restore Whiskers.slnx --locked-mode", "dotnet restore Whiskers.slnx")]                    // unlocked
    [InlineData("dotnet restore Whiskers.slnx --locked-mode", "dotnet restore src/Whiskers/Whiskers.csproj --locked-mode")] // not the solution
    public void Restore_script_check_finds_a_fail_open_or_unlocked_script(string original, string mutation)
    {
        var real = Repo(".github", "scripts", "restore-locked.sh");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.RestoreScriptViolations(real.Replace(original, mutation)));
    }

    [Fact]
    public void Dockerfile_restore_retries_only_what_a_retry_can_fix()
        => Assert.Empty(BuildPolicy.DockerfileRestoreRetryViolations(Repo("Dockerfile")));

    [Theory]
    [InlineData("then echo \"the locked restore failed three times (last exit $status)\"; exit 1; fi",
                "then echo \"the locked restore failed three times (last exit $status)\"; break; fi")]
    [InlineData("does not match (exit $status), not retried\"; exit 1; fi", "does not match (exit $status), not retried\"; break; fi")]
    [InlineData("missing on the source (exit $status), not retried\"; exit 1; fi", "missing on the source (exit $status), not retried\"; continue; fi")]
    [InlineData("'NU1004|NU1005|NU1403'", "'NU1004|NU1005'")]
    [InlineData("&& ! grep -Eq 'NU1301|NU1801'", "&& grep -Eq 'NU1301|NU1801'")]
    [InlineData("if [ \"$status\" -eq 0 ]; then exit 0; fi", "if [ \"$status\" -ge 0 ]; then exit 0; fi")]
    [InlineData("done; \\\n    exit 1", "done; \\\n    exit 0")]
    [InlineData(" || status=$?", "; status=0")]
    [InlineData("RUN for attempt in 1 2 3; do", "RUN --mount=type=cache,target=/root/.nuget/packages for attempt in 1 2 3; do")] // a NuGet cache
    public void Dockerfile_restore_retry_check_finds_a_fail_open_loop(string original, string mutation)
    {
        var real = Repo("Dockerfile");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.DockerfileRestoreRetryViolations(real.Replace(original, mutation)));
    }

    // ---- no NuGet cache, in any form ----------------------------------------------------------------------------

    /// <summary>No NuGet cache in any workflow: a cache hit skips NuGet's content-hash and signature checks (NuGet checks
    /// a package only when it downloads and extracts it), so the cache entry would become a trust root nothing re-checks.
    /// That includes a BuildKit layer cache holding the Dockerfile's restore layer.</summary>
    [Theory]
    [MemberData(nameof(Workflows))]
    public void No_workflow_caches_nuget_packages(string workflow)
        => Assert.Empty(BuildPolicy.NuGetCacheViolations(Repo(".github", "workflows", workflow), workflow, Repo("Dockerfile")));

    [Fact]
    public void The_restore_runs_in_the_build_stage()
        => Assert.Equal("build", BuildPolicy.RestoreStage(Repo("Dockerfile")));

    [Theory]
    [InlineData("          global-json-file: global.json\n", "          global-json-file: global.json\n          cache: true\n")]
    [InlineData("          global-json-file: global.json\n", "          global-json-file: global.json\n          cache: ${{ github.event_name != 'schedule' }}\n")]
    [InlineData("      - name: Restore\n",
                "      - uses: actions/cache@0057852bfaa89a56745cba8c7296529d2fc39830 # v4\n        with:\n          path: ~/.nuget/packages\n          key: nuget\n\n      - name: Restore\n")]
    [InlineData("      - name: Restore\n",
                "      - uses: actions/cache/restore@0057852bfaa89a56745cba8c7296529d2fc39830 # v4\n        with:\n          path: |\n            ${{ github.workspace }}/.nuget/packages\n          key: nuget\n\n      - name: Restore\n")]
    [InlineData("      - name: Restore\n", "      - name: Restore\n        env:\n          NUGET_PACKAGES: /tmp/pkgs\n")]          // packages folder moved
    [InlineData("          no-cache-filters: build\n", "")]                                                                     // restore layer from the cache
    [InlineData("          no-cache-filters: build\n", "          no-cache-filters: runtime\n")]                                // the wrong stage
    public void NuGet_cache_check_finds_a_cache(string original, string mutation)
    {
        var real = Repo(".github", "workflows", "ci.yml");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.NuGetCacheViolations(real.Replace(original, mutation), "ci.yml", Repo("Dockerfile")));
    }

    [Theory]
    [InlineData("          platforms: linux/arm64\n", "          platforms: linux/arm64\n          cache-from: type=gha,scope=release-arm64\n")]
    [InlineData("          platforms: linux/arm64\n", "          platforms: linux/arm64\n          cache-from: type=gha,scope=release-arm64\n          no-cache-filters: build\n")]
    [InlineData("          platforms: linux/amd64\n", "          platforms: linux/amd64\n          cache-to: type=gha,scope=release-amd64,mode=max\n")]
    public void NuGet_cache_check_finds_a_layer_cache_in_a_release(string original, string mutation)
    {
        var real = Repo(".github", "workflows", "release.yml");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.NuGetCacheViolations(real.Replace(original, mutation), "release.yml", Repo("Dockerfile")));
    }

    // ---- the vulnerable-packages gate: missing data is retried, a finding never ----------------------------------

    [Fact]
    public void Vulnerable_gate_retries_only_missing_data()
        => Assert.Empty(BuildPolicy.VulnerableGateViolations(Repo(".github", "workflows", "security.yml")));

    [Theory]
    [InlineData("              exit 1\n            fi\n            echo \"::warning::no complete", "              break\n            fi\n            echo \"::warning::no complete")] // missing data passes
    [InlineData(" && [ \"$PROJECTS\" -ge 5 ]", "")]                                                                    // too few projects pass
    [InlineData(" && [ \"$PROBLEMS\" -eq 0 ]", "")]                                                                    // problems pass
    [InlineData("[ \"$status\" -eq 0 ] && [ \"$PROJECTS\"", "[ \"$PROJECTS\"")]                                       // a failed listing passes
    [InlineData("[ \"$PROJECTS\" -ge 5 ]", "[ \"$PROJECTS\" -ge 0 ]")]                                                 // no projects pass
    [InlineData("            if [ \"$FOUND\" -gt 0 ]; then break; fi\n", "")]                                         // a finding is retried
    [InlineData("[ \"$PROJECTS\" -lt 5 ]", "[ \"$PROJECTS\" -lt 0 ]")]                                                 // the assertion after the loop weakened
    [InlineData("          if [ \"$status\" -ne 0 ] || [ \"$PROJECTS\" -lt 5 ]", "          if false && [ \"$status\" -ne 0 ] || [ \"$PROJECTS\" -lt 5 ]")]
    [InlineData("--format json > vulnerable.json || status=$?", "--format json > vulnerable.json || true")]             // the exit code is dropped
    [InlineData("for attempt in 1 2 3; do\n            status=0\n            dotnet list", "for attempt in 1 2 3 4 5; do\n            status=0\n            dotnet list")]
    public void Vulnerable_gate_check_finds_a_fail_open_loop(string original, string mutation)
    {
        var real = Repo(".github", "workflows", "security.yml");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.VulnerableGateViolations(real.Replace(original, mutation)));
    }

    // ---- the PGDG signing key is committed and pinned, never downloaded ---------------------------------------

    [Fact]
    public void Pgdg_key_is_committed_pinned_and_never_downloaded()
        => Assert.Empty(BuildPolicy.PgdgKeyViolations(Repo(".github", "workflows", "ci.yml"), Repo("Dockerfile"), Repo(BuildPolicy.PgdgKeyPath)));

    [Fact]
    public void Committed_pgdg_key_has_the_pinned_fingerprint_and_sha256()
    {
        Assert.Equal(BuildPolicy.PgdgFingerprint, BuildPolicy.ArmoredKeyFingerprint(Repo(BuildPolicy.PgdgKeyPath)));
        // The bytes on disk, as sha256sum sees them in CI and in the image.
        var bytes = File.ReadAllBytes(DeploymentScripts.RepoFile(BuildPolicy.PgdgKeyPath.Split('/')));
        Assert.Equal(BuildPolicy.PgdgKeySha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public void Pgdg_key_check_finds_a_download_and_a_wrong_key()
    {
        var ci = Repo(".github", "workflows", "ci.yml");
        var dockerfile = Repo("Dockerfile");
        var key = Repo(BuildPolicy.PgdgKeyPath);
        const string download = "curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc";

        // Downloaded again, in CI or in the image.
        var ciCall = "sudo gpg --dearmor -o /usr/share/keyrings/postgresql-archive-keyring.gpg deploy/keys/pgdg-ACCC4CF8.asc";
        Assert.Contains(ciCall, ci);
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(
            ci.Replace(ciCall, $"{download} | sudo gpg --dearmor -o /usr/share/keyrings/postgresql-archive-keyring.gpg"), dockerfile, key));
        var imageCopy = "COPY deploy/keys/pgdg-ACCC4CF8.asc /tmp/pgdg-ACCC4CF8.asc";
        Assert.Contains(imageCopy, dockerfile);
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci, dockerfile.Replace(imageCopy, $"RUN {download} -o /tmp/pgdg-ACCC4CF8.asc"), key));
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci, dockerfile.Replace("deploy/keys/pgdg-ACCC4CF8.asc", "pgdg.asc"), key));

        // The fingerprint check or the file's SHA-256 check dropped, or a grep that may cut gpg off.
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci.Replace(BuildPolicy.PgdgFingerprint, "0000"), dockerfile, key));
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci, dockerfile.Replace(BuildPolicy.PgdgFingerprint, "0000"), key));
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci.Replace(BuildPolicy.PgdgKeySha256, new string('0', 64)), dockerfile, key));
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci, dockerfile.Replace(BuildPolicy.PgdgKeySha256, new string('0', 64)), key));
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci.Replace(" | sha256sum -c -", " | true"), dockerfile, key));
        Assert.Contains("| grep '^fpr", ci);
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci.Replace("| grep '^fpr", "| grep -q '^fpr"), dockerfile, key));
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci, dockerfile.Replace("| grep '^fpr", "| grep -q '^fpr"), key));

        // Not a public key block, or another key.
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci, dockerfile, key.Replace("PUBLIC KEY BLOCK", "PRIVATE KEY BLOCK")));
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci, dockerfile, "not a key"));
        var lines = key.Replace("\r\n", "\n").Split('\n');
        var body = Array.FindIndex(lines, l => l.Length == 64);
        Assert.True(body > 0, "no base64 line in the key");
        var c = lines[body][10] == 'A' ? 'B' : 'A';
        lines[body] = lines[body][..10] + c + lines[body][11..];
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci, dockerfile, string.Join('\n', lines)));

        // Any byte changed — even one the parser ignores (the armor's CRC line, a trailing newline).
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci, dockerfile, key + "\n"));
        Assert.NotEmpty(BuildPolicy.PgdgKeyViolations(ci, dockerfile, Regex.Replace(key, @"\n=....\n", "\n=AAAA\n")));
    }

    [Fact]
    public void A_second_key_appended_inside_the_armor_is_caught()
    {
        var key = Repo(BuildPolicy.PgdgKeyPath);
        var data = BuildPolicy.ArmoredKeyData(key);
        // Re-armored as it is, the parser still reads the key (the check is not refusing everything) ...
        Assert.Equal(BuildPolicy.PgdgFingerprint, BuildPolicy.ArmoredKeyFingerprint(BuildPolicy.Armor(data)));
        // ... and with a second transferable public key appended — here the same key again; the first packet is unchanged.
        var doubled = BuildPolicy.Armor([.. data, .. data]);
        Assert.Throws<FormatException>(() => BuildPolicy.ArmoredKeyFingerprint(doubled));
        var violations = BuildPolicy.PgdgKeyViolations(Repo(".github", "workflows", "ci.yml"), Repo("Dockerfile"), doubled);
        Assert.Contains(violations, v => v.Contains("SHA-256", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("not exactly one", StringComparison.Ordinal));
    }

    [Fact]
    public void Ci_sdk_check_finds_a_floating_sdk()
    {
        var real = Repo(".github", "workflows", "ci.yml");
        Assert.Contains("global-json-file: global.json", real);
        Assert.NotEmpty(BuildPolicy.CiSdkViolations(real.Replace("global-json-file: global.json", "dotnet-version: \"10.0.x\"")));
    }

    [Theory]
    [InlineData("dotnet restore ./Whiskers/Whiskers.csproj --locked-mode >", "dotnet restore ./Whiskers/Whiskers.csproj >")]
    [InlineData("--no-restore -o /app/publish", "-o /app/publish")]
    [InlineData(" src/Whiskers.Data/packages.lock.json ./Whiskers.Data/", " ./Whiskers.Data/")]
    [InlineData("COPY Directory.Build.props ./\n", "")]
    public void Dockerfile_restore_check_finds_an_unlocked_build(string original, string mutation)
    {
        var real = Repo("Dockerfile");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.DockerfileRestoreViolations(real.Replace(original, mutation), BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"))));
    }

    /// <summary>The Dockerfile's base images as they are today (read, not written down here: an update must not break
    /// the tests that prove the checks bite).</summary>
    public static TheoryData<string> BaseImageReferences()
    {
        var data = new TheoryData<string>();
        foreach (var image in BuildPolicy.BaseImages(DeploymentScripts.ReadRepoFile("Dockerfile"))) data.Add(image);
        return data;
    }

    [Theory]
    [MemberData(nameof(BaseImageReferences))]
    public void Digest_check_finds_an_unpinned_image(string image)
    {
        var real = Repo("Dockerfile");
        Assert.Contains(image, real);
        var digest = image[(image.IndexOf("@sha256:", StringComparison.Ordinal) + 8)..];
        Assert.NotEmpty(BuildPolicy.UnpinnedBaseImages(real.Replace(image, image.Replace("@sha256:" + digest, ""))));
        // A shortened digest and a digest without a tag are no pin either.
        Assert.NotEmpty(BuildPolicy.UnpinnedBaseImages(real.Replace(image, image.Replace(digest, digest[..12]))));
        var (name, _, _) = BuildPolicy.SplitImage(image);
        Assert.NotEmpty(BuildPolicy.UnpinnedBaseImages(real.Replace(image, $"{name}@sha256:{digest}")));
    }

    [Fact]
    public void A_coordinated_update_passes_and_drift_fails()
    {
        var global = Repo("global.json");
        var dockerfile = Repo("Dockerfile");
        var sdk = BuildPolicy.GlobalJsonSdk(global);
        var newSdk = BuildPolicy.NextVersion(sdk);

        // Every base image to a new tag and digest, the SDK image and global.json together — as Dependabot plus the
        // documented SDK move would: every check still passes.
        var updated = dockerfile;
        foreach (var image in BuildPolicy.BaseImages(dockerfile))
        {
            var (name, tag, _) = BuildPolicy.SplitImage(image);
            var newTag = name == "mcr.microsoft.com/dotnet/sdk" ? newSdk : BuildPolicy.NextVersion(tag);
            updated = updated.Replace(image, $"{name}:{newTag}@sha256:{BuildPolicy.FakeDigest(image)}");
        }
        var updatedGlobal = global.Replace($"\"{sdk}\"", $"\"{newSdk}\"");
        Assert.NotEqual(dockerfile, updated);
        Assert.NotEqual(global, updatedGlobal);
        Assert.Empty(BuildPolicy.UnpinnedBaseImages(updated));
        Assert.Empty(BuildPolicy.SdkPinViolations(updatedGlobal, updated));
        Assert.Empty(ImageRuntimeToolsTests.EngineViolations(updated));

        // Only one side moved: drift.
        Assert.NotEmpty(BuildPolicy.SdkPinViolations(updatedGlobal, dockerfile));
        Assert.NotEmpty(BuildPolicy.SdkPinViolations(global, updated));
        Assert.NotEmpty(BuildPolicy.SdkPinViolations(global.Replace("\"disable\"", "\"latestFeature\""), dockerfile));
    }

    [Fact]
    public void Every_compose_and_chart_image_is_covered_by_dependabot()
    {
        var (compose, charts) = BuildPolicy.ImageFiles(DeploymentScripts.RepoRoot());
        Assert.True(compose.Count >= 5, "the compose files were not found");
        Assert.NotEmpty(charts);
        Assert.Empty(BuildPolicy.ImageCoverageViolations(Repo(".github", "dependabot.yml"), compose, charts, out var images));
        // postgres, docker-socket-proxy, node-exporter, victoria-metrics, the tailscale sidecar
        Assert.True(images >= 5, $"only {images} image references were seen");
    }

    [Theory]
    [InlineData("      - \"/deploy/telemetry/host\"\n", "", "node-exporter")]       // a compose directory without coverage
    [InlineData("      - \"/\"\n      - \"/deploy\"\n", "      - \"/deploy\"\n", "docker-socket-proxy")]
    [InlineData("package-ecosystem: \"helm\"", "package-ecosystem: \"maven\"", "tailscale")] // the chart uncovered
    public void Image_coverage_check_finds_an_uncovered_image(string original, string mutation, string expected)
    {
        var real = Repo(".github", "dependabot.yml");
        Assert.Contains(original, real);
        var (compose, charts) = BuildPolicy.ImageFiles(DeploymentScripts.RepoRoot());
        Assert.Contains(BuildPolicy.ImageCoverageViolations(real.Replace(original, mutation), compose, charts, out _),
            v => v.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("redis:latest")]
    [InlineData("redis")]
    [InlineData("${REDIS_IMAGE}")]
    public void Image_coverage_check_finds_an_image_no_update_can_move(string image)
    {
        var (compose, charts) = BuildPolicy.ImageFiles(DeploymentScripts.RepoRoot());
        var withNew = compose.Append(("deploy/docker-compose.extra.yml", $"services:\n  cache:\n    image: {image}\n")).ToList();
        Assert.Contains(BuildPolicy.ImageCoverageViolations(Repo(".github", "dependabot.yml"), withNew, charts, out _),
            v => v.Contains("docker-compose.extra.yml", StringComparison.Ordinal));
    }

    private static string Repo(params string[] parts) => DeploymentScripts.ReadRepoFile(parts);
}

/// <summary>The checks behind <see cref="BuildReproducibilityTests"/>; each returns the violations it found.</summary>
internal static class BuildPolicy
{
    private static readonly Regex FromLine = new(@"^FROM\s+(?:--platform=\S+\s+)?(?<image>\S+)(?:\s+AS\s+(?<stage>\S+))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex PinnedImage = new(@"^[a-z0-9][a-z0-9._/-]*(?::[A-Za-z0-9._-]+)@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex DotnetCommand = new(@"\bdotnet\s+(?<verb>restore|build|publish|test|run|pack)\b(?<rest>[^\n]*)", RegexOptions.CultureInvariant);

    /// <summary>The project paths of <c>Whiskers.slnx</c>, repository-relative with forward slashes.</summary>
    public static IReadOnlyList<string> SolutionProjects(string slnx) =>
        XDocument.Parse(slnx).Descendants("Project")
            .Select(p => (string?)p.Attribute("Path"))
            .OfType<string>()
            .Select(p => p.Replace('\\', '/'))
            .ToList();

    private static string ProjectDirectory(string project) => project[..project.LastIndexOf('/')];

    // ---- lock files ----

    public static IReadOnlyList<string> LockFileViolations(string directoryBuildProps, IReadOnlyList<string> projects, Func<string, string?> read)
    {
        var violations = new List<string>();
        var props = XDocument.Parse(directoryBuildProps);
        if (!props.Descendants("RestorePackagesWithLockFile").Any(e => e.Value.Trim() == "true" && e.Attribute("Condition") is null))
            violations.Add("Directory.Build.props does not set RestorePackagesWithLockFile=true unconditionally");

        foreach (var project in projects)
        {
            var csproj = read(project);
            if (csproj is null) { violations.Add($"{project} does not exist"); continue; }
            if (XDocument.Parse(csproj).Descendants("RestorePackagesWithLockFile").Any(e => e.Value.Trim() != "true"))
                violations.Add($"{project} switches its lock file off");

            var lockPath = ProjectDirectory(project) + "/packages.lock.json";
            var lockFile = read(lockPath);
            if (lockFile is null) { violations.Add($"{lockPath} is missing"); continue; }
            using var json = JsonDocument.Parse(lockFile);
            if (!json.RootElement.TryGetProperty("version", out _) || !json.RootElement.TryGetProperty("dependencies", out var deps)
                || !deps.EnumerateObject().Any())
                violations.Add($"{lockPath} is not a NuGet lock file with dependencies");
        }
        return violations;
    }

    // ---- restores in workflows and the Dockerfile ----

    /// <summary>The locked restore of the whole solution, retried only for transient failures
    /// (<see cref="RestoreScriptViolations"/>). A workflow calls it exactly as this command, alone on its line.</summary>
    public const string RestoreScript = ".github/scripts/restore-locked.sh";
    public const string RestoreScriptCall = "bash " + RestoreScript;

    /// <summary>Every <c>dotnet restore</c> is locked; every other dotnet verb that would restore implicitly says
    /// <c>--no-restore</c> or <c>--no-build</c>. A call of <see cref="RestoreScript"/> counts as a locked restore when it is
    /// exactly <see cref="RestoreScriptCall"/> (a <c>run:</c> value or a line of a <c>run</c> block): anything appended
    /// (<c>|| true</c>, arguments) is a violation.</summary>
    public static IReadOnlyList<string> WorkflowRestoreViolations(string text, out int lockedRestores)
    {
        var violations = new List<string>();
        lockedRestores = 0;
        foreach (var line in JoinContinuations(text))
        {
            if (line.TrimStart().StartsWith('#')) continue;
            if (line.Contains(RestoreScript, StringComparison.Ordinal))
            {
                var command = line.Trim();
                if (command.StartsWith("- ", StringComparison.Ordinal)) command = command[2..].TrimStart();
                if (command.StartsWith("run:", StringComparison.Ordinal)) command = command[4..].Trim();
                if (command == RestoreScriptCall) lockedRestores++;
                else violations.Add($"the restore script is not called as exactly '{RestoreScriptCall}': '{line.Trim()}'");
            }
            foreach (Match m in DotnetCommand.Matches(line))
            {
                var verb = m.Groups["verb"].Value;
                var rest = m.Groups["rest"].Value;
                if (verb == "restore")
                {
                    if (rest.Contains("--locked-mode", StringComparison.Ordinal)) lockedRestores++;
                    else violations.Add($"unlocked restore: '{m.Value.Trim()}'");
                }
                else if (!rest.Contains("--no-restore", StringComparison.Ordinal) && !rest.Contains("--no-build", StringComparison.Ordinal))
                {
                    violations.Add($"'{m.Value.Trim()}' restores implicitly (unlocked); add --no-restore or --no-build");
                }
            }
        }
        return violations;
    }

    public static IReadOnlyList<string> CiSdkViolations(string ciYaml)
    {
        var violations = new List<string>();
        var root = (YamlMappingNode)Load(ciYaml);
        foreach (var (job, steps) in JobSteps(root))
        {
            foreach (var step in steps)
            {
                if (Scalar(step, "uses")?.StartsWith("actions/setup-dotnet@", StringComparison.Ordinal) != true) continue;
                var with = step.Children.TryGetValue(new YamlScalarNode("with"), out var w) ? w as YamlMappingNode : null;
                if (with is null || Scalar(with, "global-json-file") != "global.json")
                    violations.Add($"job '{job}' sets up .NET without global-json-file: global.json");
                if (with is not null && with.Children.ContainsKey(new YamlScalarNode("dotnet-version")))
                    violations.Add($"job '{job}' names a dotnet-version next to global.json");
            }
        }
        return violations;
    }

    public static IReadOnlyList<string> DockerfileRestoreViolations(string dockerfile, IReadOnlyList<string> projects)
    {
        var violations = new List<string>();
        var lines = JoinContinuations(dockerfile).Where(l => !l.TrimStart().StartsWith('#')).ToList();
        var restores = 0;
        foreach (var line in lines.Where(l => l.TrimStart().StartsWith("RUN ", StringComparison.Ordinal)))
        {
            foreach (Match m in DotnetCommand.Matches(line))
            {
                var verb = m.Groups["verb"].Value;
                var rest = m.Groups["rest"].Value;
                if (verb == "restore")
                {
                    restores++;
                    if (!rest.Contains("--locked-mode", StringComparison.Ordinal)) violations.Add($"unlocked restore: '{m.Value.Trim()}'");
                }
                else if (!rest.Contains("--no-restore", StringComparison.Ordinal) && !rest.Contains("--no-build", StringComparison.Ordinal))
                {
                    violations.Add($"'{m.Value.Trim()}' restores implicitly (unlocked); add --no-restore");
                }
            }
        }
        if (restores == 0) violations.Add("the Dockerfile has no dotnet restore");
        if (dockerfile.Contains("type=cache", StringComparison.Ordinal))
            violations.Add("the Dockerfile mounts a build cache: a cached NuGet package skips NuGet's hash and signature checks");

        // What the restore layer sees: the COPY sources before the first restore.
        var copies = lines.TakeWhile(l => !(l.TrimStart().StartsWith("RUN ", StringComparison.Ordinal) && l.Contains("dotnet restore", StringComparison.Ordinal)))
            .Where(l => l.TrimStart().StartsWith("COPY ", StringComparison.Ordinal) && !l.Contains("--from=", StringComparison.Ordinal))
            .SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)).ToHashSet(StringComparer.Ordinal);
        if (!copies.Contains("Directory.Build.props"))
            violations.Add("the Dockerfile does not copy Directory.Build.props before the restore (no lock files without it)");
        // Every project the image builds (all but the test project) brings its lock file into the restore layer.
        foreach (var project in projects.Where(p => !p.Contains(".Tests/", StringComparison.Ordinal)))
        {
            var lockFile = ProjectDirectory(project) + "/packages.lock.json";
            if (!copies.Contains(lockFile)) violations.Add($"the Dockerfile does not copy {lockFile} into the restore layer");
        }
        return violations;
    }

    // ---- the restore retry: only what a retry can fix, and the last failure fails ----

    /// <summary>Always fail at once: the lock file does not match (NU1004, NU1005), a content hash differs (NU1403).</summary>
    public static readonly IReadOnlyList<string> MismatchRestoreErrors = ["NU1004", "NU1005", "NU1403"];
    /// <summary>Fail at once unless the same log shows a source failure (<see cref="SourceFailureErrors"/>): a package or
    /// the locked version does not exist (NU1101, NU1102).</summary>
    public static readonly IReadOnlyList<string> MissingPackageErrors = ["NU1101", "NU1102"];
    /// <summary>A source that could not be reached (NU1301, NU1801): with these in the log, NU1101/NU1102 are retried.</summary>
    public static readonly IReadOnlyList<string> SourceFailureErrors = ["NU1301", "NU1801"];

    private static readonly Regex SuccessBranch = new(@"if \[ ""\$status"" -eq 0 \]; then exit 0; fi", RegexOptions.CultureInvariant);
    private static readonly Regex MismatchBranch = new(
        @"if grep -Eq '(?<codes>[^'\n]+)' [^;&|\n]+; then echo ""[^""\n]*""; exit 1; fi", RegexOptions.CultureInvariant);
    private static readonly Regex MissingBranch = new(
        @"if grep -Eq '(?<codes>[^'\n]+)' [^;&|\n]+ && ! grep -Eq '(?<source>[^'\n]+)' [^;&|\n]+; then echo ""[^""\n]*""; exit 1; fi",
        RegexOptions.CultureInvariant);
    private static readonly Regex LastTryBranch = new(
        @"if \[ ""\$attempt"" -eq 3 \]; then echo ""[^""\n]*""; exit 1; fi", RegexOptions.CultureInvariant);
    private static readonly Regex LoopEndsFailing = new(@"\bdone;?[ \t]*(?:\\?\n[ \t]*)?exit 1[ \t]*\n?$", RegexOptions.CultureInvariant);

    /// <summary>A restore retry loop (the script, the Dockerfile's RUN): exactly three tries; the restore's exit code is
    /// kept (<c>|| status=$?</c>); the only way out with success is exactly <c>if [ "$status" -eq 0 ]; then exit 0; fi</c>;
    /// <see cref="MismatchRestoreErrors"/> always fail at once with <c>exit 1</c>; <see cref="MissingPackageErrors"/> fail at
    /// once unless the log shows exactly the <see cref="SourceFailureErrors"/>; the third failure exits 1; and the text
    /// after the loop exits 1, so no path out of it passes.</summary>
    private static List<string> RetryLoopViolations(string loop, string where)
    {
        var violations = new List<string>();
        if (!loop.Contains("for attempt in 1 2 3; do", StringComparison.Ordinal))
            violations.Add($"{where}: the retry loop is not exactly three tries ('for attempt in 1 2 3; do')");
        var restoreLine = loop.Split('\n').FirstOrDefault(l => DotnetCommand.Match(l) is { Success: true } m && m.Groups["verb"].Value == "restore");
        if (restoreLine is null || !restoreLine.Contains(" || status=$?", StringComparison.Ordinal))
            violations.Add($"{where}: the restore's exit code is not kept ('|| status=$?')");
        if (SuccessBranch.Matches(loop).Count != 1 || Regex.Matches(loop, @"\bexit 0\b").Count != 1)
            violations.Add($"{where}: the only success is not exactly 'if [ \"$status\" -eq 0 ]; then exit 0; fi'");

        static HashSet<string> Codes(Match m, string group) => m.Groups[group].Value.Split('|').ToHashSet(StringComparer.Ordinal);
        var missing = MissingBranch.Match(loop);
        var mismatch = MismatchBranch.Matches(loop).FirstOrDefault(m => !missing.Success || m.Index != missing.Index);
        if (mismatch is null)
            violations.Add($"{where}: no branch that fails at once (exit 1) on a lock-file or content-hash mismatch");
        else
            foreach (var code in MismatchRestoreErrors.Where(c => !Codes(mismatch, "codes").Contains(c)))
                violations.Add($"{where}: {code} is retried instead of failing at once");
        if (!missing.Success)
            violations.Add($"{where}: no branch that fails at once (exit 1) on a missing package without a source failure");
        else
        {
            foreach (var code in MissingPackageErrors.Where(c => !Codes(missing, "codes").Contains(c)))
                violations.Add($"{where}: {code} is retried instead of failing at once");
            if (!Codes(missing, "source").SetEquals(SourceFailureErrors))
                violations.Add($"{where}: a missing package is retried on '{missing.Groups["source"].Value}', not exactly on {string.Join('|', SourceFailureErrors)}");
        }
        if (!LastTryBranch.IsMatch(loop)) violations.Add($"{where}: the third failure does not end in exit 1");
        if (!LoopEndsFailing.IsMatch(loop)) violations.Add($"{where}: the loop is not followed by exit 1");
        return violations;
    }

    /// <summary><see cref="RestoreScript"/>: strict mode (with <c>pipefail</c>, or <c>| tee</c> would hide the restore's
    /// exit code), exactly one restore — <c>dotnet restore Whiskers.slnx --locked-mode</c> — and a retry loop that fails
    /// closed (<see cref="RetryLoopViolations"/>).</summary>
    public static IReadOnlyList<string> RestoreScriptViolations(string script)
    {
        var violations = new List<string>();
        var code = string.Join('\n', script.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith('#'))) + "\n";
        if (!code.Split('\n').Any(l => l.Trim() == "set -euo pipefail")) violations.Add("the script does not run with 'set -euo pipefail'");
        violations.AddRange(WorkflowRestoreViolations(code, out var locked));
        if (locked != 1) violations.Add($"the script has {locked} locked restores, not exactly one");
        if (!code.Contains("dotnet restore Whiskers.slnx --locked-mode", StringComparison.Ordinal))
            violations.Add("the script does not restore the whole solution ('dotnet restore Whiskers.slnx --locked-mode')");
        violations.AddRange(RetryLoopViolations(code.TrimEnd('\n') + "\n", "restore-locked.sh"));
        return violations;
    }

    /// <summary>The Dockerfile's restore RUN, from <c>RUN</c> to its last continuation line, checked like the script.</summary>
    public static IReadOnlyList<string> DockerfileRestoreRetryViolations(string dockerfile)
    {
        var lines = dockerfile.Replace("\r\n", "\n").Split('\n');
        string? run = null;
        for (var start = 0; start < lines.Length && run is null; start++)
        {
            if (!lines[start].StartsWith("RUN ", StringComparison.Ordinal)) continue;
            var end = start;
            while (end < lines.Length - 1 && lines[end].TrimEnd().EndsWith('\\')) end++;
            var block = string.Join('\n', lines[start..(end + 1)]) + "\n";
            if (block.Contains("dotnet restore", StringComparison.Ordinal)) run = block;
        }
        if (run is null) return ["the Dockerfile has no RUN with a restore"];
        var violations = RetryLoopViolations(run, "Dockerfile");
        if (run.Contains("--mount", StringComparison.Ordinal))
            violations.Add("Dockerfile: the restore mounts a cache: a cached NuGet package skips NuGet's hash and signature checks");
        return violations;
    }

    /// <summary>The Dockerfile stage (<c>FROM … AS &lt;stage&gt;</c>) whose RUN does the <c>dotnet restore</c>.</summary>
    public static string? RestoreStage(string dockerfile)
    {
        string? stage = null;
        foreach (var line in JoinContinuations(dockerfile).Where(l => !l.TrimStart().StartsWith('#')))
        {
            if (FromLine.Match(line.Trim()) is { Success: true } from)
                stage = from.Groups["stage"].Success ? from.Groups["stage"].Value : null;
            else if (line.TrimStart().StartsWith("RUN ", StringComparison.Ordinal) && line.Contains("dotnet restore", StringComparison.Ordinal))
                return stage;
        }
        return null;
    }

    /// <summary>No workflow caches NuGet packages, in any form — a cache hit skips NuGet's hash and signature checks:
    /// <list type="bullet">
    /// <item>no <c>cache</c> input on <c>actions/setup-dotnet</c> (any value, also an expression);</item>
    /// <item>no <c>actions/cache</c> step whose path names <c>.nuget</c>, and no <c>NUGET_PACKAGES</c> anywhere (it could
    /// point the packages folder at any cached directory);</item>
    /// <item>a <c>docker/build-push-action</c> with a layer cache (<c>cache-from</c>) names the Dockerfile's restore stage in
    /// <c>no-cache-filters</c>, so the restore layer is never taken from the cache;</item>
    /// <item><c>release.yml</c> builds have no layer cache at all (<c>cache-from</c>/<c>cache-to</c>): a release builds fresh.</item>
    /// </list></summary>
    public static IReadOnlyList<string> NuGetCacheViolations(string workflowYaml, string workflowName, string dockerfile)
    {
        var violations = new List<string>();
        var restoreStage = RestoreStage(dockerfile);
        if (restoreStage is null) violations.Add("the Dockerfile's restore is not in a named stage (FROM … AS <stage>)");
        if (workflowYaml.Replace("\r\n", "\n").Split('\n').Any(l => !l.TrimStart().StartsWith('#') && l.Contains("NUGET_PACKAGES", StringComparison.Ordinal)))
            violations.Add($"{workflowName} sets NUGET_PACKAGES: the packages folder could be a cached directory");
        var root = (YamlMappingNode)Load(workflowYaml);
        foreach (var (job, steps) in JobSteps(root))
        {
            foreach (var step in steps)
            {
                var uses = Scalar(step, "uses") ?? "";
                var with = step.Children.TryGetValue(new YamlScalarNode("with"), out var w) ? w as YamlMappingNode : null;
                bool Has(string key) => with is not null && with.Children.ContainsKey(new YamlScalarNode(key));
                if (uses.StartsWith("actions/setup-dotnet@", StringComparison.Ordinal) && (Has("cache") || Has("cache-dependency-path")))
                    violations.Add($"job '{job}': actions/setup-dotnet caches NuGet packages (a cache hit skips NuGet's hash and signature checks)");
                if (uses.StartsWith("actions/cache", StringComparison.Ordinal) && with is not null
                    && (Scalar(with, "path") ?? "").Contains(".nuget", StringComparison.OrdinalIgnoreCase))
                    violations.Add($"job '{job}': {uses.Split('@')[0]} caches NuGet packages (a cache hit skips NuGet's hash and signature checks)");
                if (!uses.StartsWith("docker/build-push-action@", StringComparison.Ordinal)) continue;
                var stepName = Scalar(step, "name") ?? uses;
                if (workflowName == "release.yml" && (Has("cache-from") || Has("cache-to")))
                    violations.Add($"release.yml job '{job}', '{stepName}': a release build has a layer cache; it must build fresh");
                if (Has("cache-from"))
                {
                    var filters = (Scalar(with!, "no-cache-filters") ?? "").Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (restoreStage is not null && !filters.Contains(restoreStage, StringComparer.Ordinal))
                        violations.Add($"job '{job}', '{stepName}': cache-from without no-cache-filters: {restoreStage} — the restore layer would come from the cache");
                }
            }
        }
        return violations;
    }

    /// <summary>A step that restores (the script or <c>dotnet restore</c>) cannot be made to pass on a failure: no
    /// <c>continue-on-error</c> on the step or its job, and no <c>set +e</c> in its script.</summary>
    public static IReadOnlyList<string> RestoreStepViolations(string workflowYaml)
    {
        var violations = new List<string>();
        var root = (YamlMappingNode)Load(workflowYaml);
        if (!root.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobsNode) || jobsNode is not YamlMappingNode jobs) return violations;
        foreach (var (key, jobNode) in jobs.Children)
        {
            if (jobNode is not YamlMappingNode job) continue;
            var jobName = ((YamlScalarNode)key).Value;
            var steps = job.Children.TryGetValue(new YamlScalarNode("steps"), out var s) && s is YamlSequenceNode seq
                ? seq.Children.OfType<YamlMappingNode>().ToList() : [];
            foreach (var step in steps)
            {
                var run = Scalar(step, "run") ?? "";
                if (!run.Contains(RestoreScript, StringComparison.Ordinal) && !Regex.IsMatch(run, @"\bdotnet\s+restore\b")) continue;
                var name = Scalar(step, "name") ?? "(unnamed)";
                if (step.Children.ContainsKey(new YamlScalarNode("continue-on-error")) || job.Children.ContainsKey(new YamlScalarNode("continue-on-error")))
                    violations.Add($"job '{jobName}', step '{name}': continue-on-error on a restore");
                if (Regex.IsMatch(run, @"(^|[\s;&|])set\s+(\+e|\+o\s+errexit)\b", RegexOptions.Multiline))
                    violations.Add($"job '{jobName}', step '{name}': 'set +e' in a restore step");
            }
        }
        return violations;
    }

    // ---- the vulnerable-packages gate (security.yml) ----

    private const string FoundBreak = "if [ \"$FOUND\" -gt 0 ]; then break; fi";
    private const string CompleteBreak = "if [ \"$status\" -eq 0 ] && [ \"$PROJECTS\" -ge 5 ] && [ \"$PROBLEMS\" -eq 0 ]; then break; fi";
    private static readonly Regex CompleteAfterLoop = new(
        @"^\s*if \[ ""\$status"" -ne 0 \] \|\| \[ ""\$PROJECTS"" -lt 5 \] \|\| \[ ""\$PROBLEMS"" -ne 0 \]; then echo ""[^""\n]*""; exit 1; fi\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex VulnerableLastTry = new(
        @"^\s*if \[ ""\$attempt"" -eq 3 \]; then\n(?<body>(?:(?!\s*fi\n)[^\n]*\n)*?)\s*fi\n", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>The <c>Vulnerable packages (gate)</c> step retries only missing data and never a finding: exactly three
    /// tries; <c>FOUND</c> is computed once per try, inside the loop, and a finding ends the loop at once
    /// (<c>if [ "$FOUND" -gt 0 ]; then break; fi</c>); otherwise the loop ends only on complete data — exit 0, at least 5
    /// projects, no problems — or the third try exits 1; after the loop a finding fails, and with no finding the data is
    /// asserted complete again.</summary>
    public static IReadOnlyList<string> VulnerableGateViolations(string securityYaml)
    {
        var violations = new List<string>();
        var root = (YamlMappingNode)Load(securityYaml);
        var run = JobSteps(root).SelectMany(j => j.Steps).Where(s => Scalar(s, "name") == "Vulnerable packages (gate)")
            .Select(s => Scalar(s, "run")).FirstOrDefault();
        if (run is null) return ["security.yml has no 'Vulnerable packages (gate)' step"];
        run = run.Replace("\r\n", "\n");
        var loopStart = run.IndexOf("for attempt in 1 2 3; do\n", StringComparison.Ordinal);
        var loopEnd = run.IndexOf("\n          done\n", StringComparison.Ordinal) is var e && e >= 0 ? e : run.IndexOf("\ndone\n", StringComparison.Ordinal);
        if (loopStart < 0 || loopEnd < loopStart) return ["the vulnerable-packages gate has no 'for attempt in 1 2 3; do … done' loop"];
        var loop = run[loopStart..loopEnd];
        var after = run[loopEnd..];
        var lines = loop.Split('\n').Select(l => l.Trim()).ToList();

        var list = lines.FirstOrDefault(l => l.StartsWith("dotnet list Whiskers.slnx package --vulnerable", StringComparison.Ordinal));
        if (list is null || !list.EndsWith(" || status=$?", StringComparison.Ordinal))
            violations.Add("the vulnerability listing's exit code is not kept ('|| status=$?')");
        if (Regex.Matches(run, @"\bFOUND=").Count != 1 || !Regex.IsMatch(loop, @"\bFOUND="))
            violations.Add("FOUND is not computed exactly once per try, inside the loop");
        var foundAt = lines.IndexOf(FoundBreak);
        if (foundAt < 0) violations.Add($"a finding does not end the loop at once ('{FoundBreak}')");
        var completeAt = lines.IndexOf(CompleteBreak);
        if (completeAt < 0) violations.Add($"the loop does not end exactly on complete data ('{CompleteBreak}')");
        if (foundAt >= 0 && completeAt >= 0 && foundAt > completeAt) violations.Add("the finding check comes after the completeness check");
        if (lines.Count(l => l.Contains("break", StringComparison.Ordinal)) != 2) violations.Add("the loop has another way out than a finding or complete data");
        var lastTry = VulnerableLastTry.Match(loop + "\n");
        if (!lastTry.Success || lastTry.Groups["body"].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() != "exit 1")
            violations.Add("the third try without complete data does not end in exit 1");
        var report = after.IndexOf("if [ \"$FOUND\" -gt 0 ]; then\n", StringComparison.Ordinal);
        var assert = CompleteAfterLoop.Match(after);
        if (report < 0) violations.Add("a finding does not fail after the loop");
        if (!assert.Success || assert.Index < report) violations.Add("with no finding, the data is not asserted complete again after the loop");
        return violations;
    }

    // ---- the PGDG repository key ----

    public const string PgdgKeyPath = "deploy/keys/pgdg-ACCC4CF8.asc";
    public const string PgdgFingerprint = "B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8";
    /// <summary>SHA-256 of the whole committed key file: a second key appended inside the armor, or any other byte
    /// changed, does not match. Move it only together with the file, after checking a new key out of band.</summary>
    public const string PgdgKeySha256 = "0144068502a1eddd2a0280ede10ef607d1ec592ce819940991203941564e8e76";

    /// <summary>CI and the image take the PostgreSQL (PGDG) repository key from <see cref="PgdgKeyPath"/>, check the
    /// whole file's SHA-256 (<c>sha256sum -c</c>) and the key's fingerprint before trusting it, and download no key from
    /// postgresql.org; the committed file has <see cref="PgdgKeySha256"/> and is exactly one PGP public key with
    /// <see cref="PgdgFingerprint"/>.</summary>
    public static IReadOnlyList<string> PgdgKeyViolations(string ciYaml, string dockerfile, string key)
    {
        var violations = new List<string>();
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.Replace("\r\n", "\n")))).ToLowerInvariant();
        if (sha != PgdgKeySha256) violations.Add($"{PgdgKeyPath} has SHA-256 {sha}, not the pinned {PgdgKeySha256}");
        foreach (var (name, text, path) in new[] { ("ci.yml", ciYaml, PgdgKeyPath), ("Dockerfile", dockerfile, "/tmp/pgdg-ACCC4CF8.asc") })
        {
            var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith('#')).ToList();
            var check = $"echo \"{PgdgKeySha256}  {path}\" | sha256sum -c -";
            if (!lines.Any(l => l.Contains(check, StringComparison.Ordinal)))
                violations.Add($"{name} does not check the key file's SHA-256 ('{check}')");
            foreach (var line in lines.Where(l => Regex.IsMatch(l, @"\b(curl|wget)\b") && l.Contains("postgresql.org", StringComparison.Ordinal)))
                violations.Add($"{name} downloads from postgresql.org: '{line.Trim()}'");
            if (lines.Any(l => l.Contains("www.postgresql.org", StringComparison.Ordinal) || l.Contains("/media/keys/", StringComparison.Ordinal)))
                violations.Add($"{name} names the key's download URL");
            if (!lines.Any(l => l.Contains(PgdgKeyPath, StringComparison.Ordinal)))
                violations.Add($"{name} does not use the committed key {PgdgKeyPath}");
            var fpr = lines.Where(l => l.Contains($"'^fpr:::::::::{PgdgFingerprint}:$'", StringComparison.Ordinal)).ToList();
            if (fpr.Count == 0)
                violations.Add($"{name} does not check the key's fingerprint {PgdgFingerprint}");
            // grep -q may quit before gpg has written everything: gpg then dies of SIGPIPE (exit 141) under pipefail.
            if (fpr.Any(l => l.Contains("grep -q", StringComparison.Ordinal) || !l.TrimEnd(' ', '\\').EndsWith(">/dev/null", StringComparison.Ordinal)))
                violations.Add($"{name}: the fingerprint grep must read all of gpg's output ('grep … >/dev/null', not 'grep -q')");
        }
        try
        {
            var fingerprint = ArmoredKeyFingerprint(key);
            if (fingerprint != PgdgFingerprint) violations.Add($"{PgdgKeyPath} has fingerprint {fingerprint}, not {PgdgFingerprint}");
        }
        catch (FormatException e)
        {
            violations.Add($"{PgdgKeyPath} is not an armored PGP public key: {e.Message}");
        }
        return violations;
    }

    /// <summary>The binary packets of an ASCII-armored PGP public key block (the armor's CRC line is not part of them).
    /// Throws <see cref="FormatException"/> when there is no such block or its body is not base64.</summary>
    public static byte[] ArmoredKeyData(string armored)
    {
        var lines = armored.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        var begin = lines.IndexOf("-----BEGIN PGP PUBLIC KEY BLOCK-----");
        var end = lines.IndexOf("-----END PGP PUBLIC KEY BLOCK-----");
        if (begin < 0 || end < begin) throw new FormatException("no PGP PUBLIC KEY BLOCK");
        if (lines.Count(l => l.StartsWith("-----BEGIN ", StringComparison.Ordinal)) != 1) throw new FormatException("more than one armor block");
        var i = begin + 1;
        while (i < end && lines[i].Length > 0) i++; // armor headers end at the first blank line
        var base64 = string.Concat(lines.Skip(i + 1).Take(end - i - 1).TakeWhile(l => !l.StartsWith('=')));
        try { return Convert.FromBase64String(base64); }
        catch (FormatException) { throw new FormatException("the armor body is not base64"); }
    }

    /// <summary>Wraps packets in an armor block (no CRC line; the parser here does not need one).</summary>
    public static string Armor(byte[] data) =>
        "-----BEGIN PGP PUBLIC KEY BLOCK-----\n\n" + string.Join('\n', Convert.ToBase64String(data).Chunk(64).Select(c => new string(c)))
        + "\n-----END PGP PUBLIC KEY BLOCK-----\n";

    /// <summary>One OpenPGP packet header at <paramref name="at"/> (old or new format): its tag, and where its body starts
    /// and how long it is.</summary>
    private static (int Tag, int Offset, int Length) Packet(byte[] data, int at)
    {
        if (at + 2 > data.Length) throw new FormatException("a truncated packet header");
        int tag, length, offset;
        if ((data[at] & 0xC0) == 0xC0)
        {
            tag = data[at] & 0x3F;
            var b = data[at + 1];
            if (b < 192) { length = b; offset = at + 2; }
            else if (b < 224 && at + 3 <= data.Length) { length = ((b - 192) << 8) + data[at + 2] + 192; offset = at + 3; }
            else if (b == 255 && at + 6 <= data.Length) { length = (data[at + 2] << 24) | (data[at + 3] << 16) | (data[at + 4] << 8) | data[at + 5]; offset = at + 6; }
            else throw new FormatException("a partial or truncated packet length");
        }
        else if ((data[at] & 0x80) == 0x80)
        {
            tag = (data[at] >> 2) & 0x0F;
            (length, offset) = (data[at] & 0x03) switch
            {
                0 => ((int)data[at + 1], at + 2),
                1 when at + 3 <= data.Length => ((data[at + 1] << 8) | data[at + 2], at + 3),
                2 when at + 5 <= data.Length => ((data[at + 1] << 24) | (data[at + 2] << 16) | (data[at + 3] << 8) | data[at + 4], at + 5),
                _ => throw new FormatException("an indeterminate or truncated packet length"),
            };
        }
        else throw new FormatException("not an OpenPGP packet");
        if (length < 0 || offset + length > data.Length) throw new FormatException($"a truncated packet (tag {tag})");
        return (tag, offset, length);
    }

    /// <summary>The v4 fingerprint (RFC 4880 §12.2: SHA-1 over 0x99, the two-octet length and the body of the primary
    /// public-key packet) of an ASCII-armored PGP public key block holding exactly one public key: every packet is walked,
    /// and a second public-key packet (tag 6) — a second key appended inside the armor — is refused. Throws
    /// <see cref="FormatException"/> on anything else.</summary>
    public static string ArmoredKeyFingerprint(string armored)
    {
        var data = ArmoredKeyData(armored);
        if (data.Length < 3) throw new FormatException("the key is empty");
        var packets = new List<(int Tag, int Offset, int Length)>();
        for (var at = 0; at < data.Length;)
        {
            var p = Packet(data, at);
            packets.Add(p);
            at = p.Offset + p.Length;
        }
        var (tag, offset, length) = packets[0];
        if (tag != 6) throw new FormatException($"the first packet is tag {tag}, not a public key (6)");
        if (packets.Count(p => p.Tag == 6) != 1) throw new FormatException($"{packets.Count(p => p.Tag == 6)} public keys, not exactly one");
        if (length > 0xFFFF) throw new FormatException("the primary key packet is too long");
        if (data[offset] != 4) throw new FormatException($"key version {data[offset]}, not 4");

        var hashed = new byte[3 + length];
        hashed[0] = 0x99;
        hashed[1] = (byte)(length >> 8);
        hashed[2] = (byte)length;
        Array.Copy(data, offset, hashed, 3, length);
#pragma warning disable CA5350 // SHA-1 is what defines an OpenPGP v4 fingerprint; this identifies a key, it secures nothing
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(hashed));
#pragma warning restore CA5350
    }

    // ---- base images ----

    /// <summary>The FROM references that are images (a FROM naming an earlier stage is not one).</summary>
    public static IReadOnlyList<string> BaseImages(string dockerfile)
    {
        var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var images = new List<string>();
        foreach (Match m in FromLine.Matches(dockerfile))
        {
            var image = m.Groups["image"].Value;
            if (!stages.Contains(image)) images.Add(image);
            if (m.Groups["stage"].Success) stages.Add(m.Groups["stage"].Value);
        }
        return images;
    }

    public static IReadOnlyList<string> UnpinnedBaseImages(string dockerfile) =>
        BaseImages(dockerfile).Where(i => !PinnedImage.IsMatch(i))
            .Select(i => $"base image '{i}' is not pinned as image:tag@sha256:<64 hex>")
            .ToList();

    public static IReadOnlyList<string> SdkPinViolations(string globalJson, string dockerfile)
    {
        var violations = new List<string>();
        using var json = JsonDocument.Parse(globalJson);
        var sdk = json.RootElement.GetProperty("sdk");
        var version = sdk.GetProperty("version").GetString();
        if (!sdk.TryGetProperty("rollForward", out var rf) || rf.GetString() != "disable")
            violations.Add("global.json must not roll forward: another SDK writes other framework packages into the lock files");
        var sdkImages = BaseImages(dockerfile).Where(i => i.StartsWith("mcr.microsoft.com/dotnet/sdk:", StringComparison.Ordinal)).ToList();
        if (sdkImages.Count == 0) violations.Add("the Dockerfile has no mcr.microsoft.com/dotnet/sdk stage");
        foreach (var image in sdkImages)
        {
            var tag = image["mcr.microsoft.com/dotnet/sdk:".Length..].Split('@')[0];
            if (tag != version) violations.Add($"the SDK image tag '{tag}' is not global.json's SDK {version}");
        }
        return violations;
    }

    /// <summary><c>name[:tag][@sha256:digest]</c> into its parts (empty when absent).</summary>
    public static (string Name, string Tag, string Digest) SplitImage(string image)
    {
        var at = image.IndexOf('@');
        var digest = at >= 0 ? image[(at + 1)..] : "";
        var nameTag = at >= 0 ? image[..at] : image;
        var colon = nameTag.LastIndexOf(':');
        return colon > nameTag.LastIndexOf('/') ? (nameTag[..colon], nameTag[(colon + 1)..], digest) : (nameTag, "", digest);
    }

    public static string GlobalJsonSdk(string globalJson)
    {
        using var json = JsonDocument.Parse(globalJson);
        return json.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
    }

    /// <summary>The version with its last number raised by one (<c>10.0.402</c> → <c>10.0.403</c>,
    /// <c>29.9.3-cli</c> → <c>29.9.4-cli</c>).</summary>
    public static string NextVersion(string version)
    {
        var m = Regex.Match(version, @"(\d+)(\D*)$");
        Assert.True(m.Success, $"no number in '{version}'");
        return version[..m.Index] + (long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 1) + m.Groups[2].Value;
    }

    public static string FakeDigest(string seed) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

    // ---- images in compose files and the Helm chart ----

    private static readonly Regex ComposeFileName = new(@"^(docker-)?compose(\.[A-Za-z0-9_-]+)*\.ya?ml$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Whiskers' own image: versioned by the release itself, not by Dependabot.</summary>
    private const string OwnImage = "ghcr.io/lupusmalusdeviant/whiskers";

    /// <summary>The repository's compose files and Helm charts (repository-relative paths, forward slashes).</summary>
    public static (IReadOnlyList<(string Path, string Content)> Compose, IReadOnlyList<(string ChartDir, string Values)> Charts) ImageFiles(string root)
    {
        static bool Skipped(string rel) => rel.Split('/').Any(p => p is ".git" or "bin" or "obj" or "node_modules" or ".claude" or "TestResults");
        var compose = new List<(string, string)>();
        var charts = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (Skipped(rel)) continue;
            var name = Path.GetFileName(rel);
            if (ComposeFileName.IsMatch(name)) compose.Add((rel, File.ReadAllText(file).Replace("\r\n", "\n")));
            if (name == "Chart.yaml")
            {
                var dir = rel[..rel.LastIndexOf('/')];
                var values = Path.Combine(Path.GetDirectoryName(file)!, "values.yaml");
                charts.Add((dir, File.Exists(values) ? File.ReadAllText(values).Replace("\r\n", "\n") : ""));
            }
        }
        return (compose, charts);
    }

    /// <summary>Every image a compose file or chart names can be moved by Dependabot: its directory is covered by
    /// <c>docker-compose</c> / <c>helm</c>, and the reference has a version tag (not <c>latest</c>, not a variable).</summary>
    public static IReadOnlyList<string> ImageCoverageViolations(string dependabotYaml,
        IReadOnlyList<(string Path, string Content)> composeFiles, IReadOnlyList<(string ChartDir, string Values)> charts, out int images)
    {
        var violations = new List<string>();
        var covered = Coverage(dependabotYaml);
        bool Covers(string ecosystem, string dir) => covered.TryGetValue(ecosystem, out var d) && d.Contains(dir);
        var count = 0;

        void CheckReference(string where, string image)
        {
            count++;
            var (_, tag, digest) = SplitImage(image);
            if (image.Contains('$'))
                violations.Add($"{where}: image '{image}' is a variable — no update can move it");
            else if (tag.Length == 0 || tag == "latest")
                violations.Add($"{where}: image '{image}' has no version tag — no update can move it");
            else if (digest.Length > 0 && !Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$"))
                violations.Add($"{where}: image '{image}' has a malformed digest");
        }

        foreach (var (path, content) in composeFiles)
        {
            var dir = path.Contains('/') ? "/" + path[..path.LastIndexOf('/')] : "/";
            if (Load(content) is YamlMappingNode root && root.Children.TryGetValue(new YamlScalarNode("services"), out var s) && s is YamlMappingNode services)
                foreach (var (_, service) in services.Children)
                    if (service is YamlMappingNode svc && Scalar(svc, "image") is { } image)
                    {
                        CheckReference(path, image);
                        if (!Covers("docker-compose", dir)) violations.Add($"{path}: image '{image}' — docker-compose does not cover {dir}");
                    }
        }

        foreach (var (chartDir, values) in charts)
        {
            var chartCovered = Covers("helm", "/" + chartDir);
            if (!chartCovered) violations.Add($"{chartDir}: helm does not cover /{chartDir}");
            if (values.Length == 0) continue;
            foreach (var (where, image) in ChartImages(Load(values), chartDir + "/values.yaml"))
            {
                CheckReference(where, image);
                if (!chartCovered) violations.Add($"{where}: image '{image}' — helm does not cover /{chartDir}");
            }
        }
        images = count;
        return violations;
    }

    private static IEnumerable<(string Where, string Image)> ChartImages(YamlNode node, string path)
    {
        if (node is YamlMappingNode map)
        {
            foreach (var (k, v) in map.Children)
            {
                var key = ((YamlScalarNode)k).Value!;
                if (key == "image" && v is YamlScalarNode s && !string.IsNullOrEmpty(s.Value))
                    yield return ($"{path}: {key}", s.Value!);
                else if (key == "image" && v is YamlMappingNode m && Scalar(m, "repository") is { } repo)
                {
                    // Whiskers' own image follows the release (tag "" = the chart's appVersion).
                    if (repo != OwnImage) yield return ($"{path}: {key}", $"{repo}:{Scalar(m, "tag")}");
                }
                else
                    foreach (var inner in ChartImages(v, path)) yield return inner;
            }
        }
        else if (node is YamlSequenceNode seq)
            foreach (var item in seq.Children)
                foreach (var inner in ChartImages(item, path)) yield return inner;
    }

    // ---- Dependabot ----

    public static IReadOnlyList<string> DependabotViolations(string dependabotYaml, IReadOnlyList<string> projects)
    {
        var violations = new List<string>();
        var covered = Coverage(dependabotYaml, violations);
        bool Covers(string ecosystem, string dir) => covered.TryGetValue(ecosystem, out var d) && d.Contains(dir);
        foreach (var project in projects)
        {
            var dir = "/" + ProjectDirectory(project);
            if (!Covers("nuget", dir)) violations.Add($"nuget does not cover {dir} ({project})");
        }
        if (!Covers("dotnet-sdk", "/")) violations.Add("dotnet-sdk does not cover / (global.json)");
        if (!Covers("docker", "/")) violations.Add("docker does not cover / (the Dockerfile's base images)");
        if (!Covers("github-actions", "/")) violations.Add("github-actions does not cover / (.github/workflows)");
        return violations;
    }

    /// <summary>Ecosystem → the directories its entries cover; an entry without a schedule is reported.</summary>
    private static Dictionary<string, HashSet<string>> Coverage(string dependabotYaml, List<string>? violations = null)
    {
        var root = (YamlMappingNode)Load(dependabotYaml);
        var updates = (YamlSequenceNode)root.Children[new YamlScalarNode("updates")];
        var covered = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        static string Normal(string d) => d.TrimEnd('/') is "" ? "/" : d.TrimEnd('/');
        foreach (var update in updates.Children.OfType<YamlMappingNode>())
        {
            var ecosystem = Scalar(update, "package-ecosystem") ?? "";
            if (!covered.TryGetValue(ecosystem, out var dirs)) covered[ecosystem] = dirs = new HashSet<string>(StringComparer.Ordinal);
            if (Scalar(update, "directory") is { } dir) dirs.Add(Normal(dir));
            if (update.Children.TryGetValue(new YamlScalarNode("directories"), out var list) && list is YamlSequenceNode seq)
                foreach (var d in seq.Children.OfType<YamlScalarNode>())
                    dirs.Add(Normal(d.Value!));
            if (!update.Children.ContainsKey(new YamlScalarNode("schedule")))
                violations?.Add($"the {ecosystem} entry has no schedule");
        }
        return covered;
    }

    // ---- helpers ----

    /// <summary>The text as logical lines: a trailing backslash continues the line (shell and Dockerfile).</summary>
    private static IEnumerable<string> JoinContinuations(string text)
    {
        var current = new System.Text.StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.EndsWith('\\')) { current.Append(line[..^1]).Append(' '); continue; }
            current.Append(line);
            yield return current.ToString();
            current.Clear();
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static YamlNode Load(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents[0].RootNode;
    }

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;

    private static IEnumerable<(string Job, IReadOnlyList<YamlMappingNode> Steps)> JobSteps(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobs) || jobs is not YamlMappingNode map) yield break;
        foreach (var (key, job) in map.Children)
        {
            if (job is not YamlMappingNode jobMap) continue;
            var steps = jobMap.Children.TryGetValue(new YamlScalarNode("steps"), out var s) && s is YamlSequenceNode seq
                ? seq.Children.OfType<YamlMappingNode>().ToList()
                : new List<YamlMappingNode>();
            yield return (((YamlScalarNode)key).Value!, steps);
        }
    }
}
