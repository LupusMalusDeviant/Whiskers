using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Whiskers.Tests;

/// <summary>
/// R1-45 (TA-15/16, F-03/04/23): the workflows that build and publish Whiskers are part of its supply chain.
/// These tests read the three workflow files and pin the properties a release depends on:
///
/// <list type="bullet">
/// <item>every action is pinned to a full commit SHA with its release named in a comment, so a moved tag cannot
/// run foreign code with GHCR push rights and the cosign OIDC identity;</item>
/// <item>the workflows hold no write permission themselves and only the named jobs hold the writes they need;</item>
/// <item>no event or dispatch input is pasted into a shell script;</item>
/// <item>a release is built from the validated tag commit, gated on the test suite and on a Trivy scan of both
/// architectures, publishes exactly the scanned digests and never replaces an existing version;</item>
/// <item>scan exceptions live in <c>.trivyignore</c> with a reason and an expiry date, not behind a blanket
/// <c>ignore-unfixed</c>.</item>
/// </list>
///
/// Every check has negative tests that mutate the real file, so none of them can pass by finding nothing.
/// </summary>
public class ReleaseWorkflowTests
{
    private static readonly string[] Workflows = ["ci.yml", "chart-ci.yml", "release.yml"];

    /// <summary>Jobs allowed to hold a write permission, and which ones. Everything else is read-only.</summary>
    private static readonly Dictionary<string, Dictionary<string, string[]>> AllowedWrites = new()
    {
        ["ci.yml"] = new(),
        ["chart-ci.yml"] = new(),
        ["release.yml"] = new()
        {
            ["build"] = ["packages"],                   // stage the untagged per-arch digests
            ["publish-image"] = ["packages", "id-token"], // tag the scanned digests, cosign keyless signing
            ["publish-chart"] = ["packages"],
            ["github-release"] = ["contents"],
        },
    };

    // ---- the real files ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("chart-ci.yml")]
    [InlineData("release.yml")]
    public void Every_action_is_pinned_to_a_full_commit_sha(string file)
    {
        var text = Workflow(file);
        Assert.NotEmpty(WorkflowPolicy.UsesValues(text)); // the check must see something
        Assert.Empty(WorkflowPolicy.UnpinnedActions(text));
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("chart-ci.yml")]
    [InlineData("release.yml")]
    public void Permissions_are_least_privilege(string file)
        => Assert.Empty(WorkflowPolicy.PermissionViolations(Workflow(file), AllowedWrites[file]));

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("chart-ci.yml")]
    [InlineData("release.yml")]
    public void No_event_or_input_is_interpolated_into_a_shell_script(string file)
    {
        var text = Workflow(file);
        Assert.NotEmpty(WorkflowPolicy.RunScripts(text));
        Assert.Empty(WorkflowPolicy.UnsafeRunInterpolations(text));
    }

    [Fact]
    public void Release_builds_the_tag_commit_and_publishes_only_after_tests_and_scan()
        => Assert.Empty(WorkflowPolicy.ReleaseGateViolations(Workflow("release.yml"), Workflow("ci.yml")));

    [Fact]
    public void Both_architectures_are_scanned_by_digest_and_exactly_those_digests_are_published()
        => Assert.Empty(WorkflowPolicy.ScanViolations(Workflow("release.yml")));

    [Fact]
    public void Versioned_artifacts_are_checked_for_existence_before_they_are_pushed()
    {
        Assert.Empty(WorkflowPolicy.ImmutabilityViolations(Workflow("release.yml")));
        var script = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "scripts", "assert-not-published.sh"));
        Assert.Contains("already exists", script);
        Assert.Contains("refusing to publish", script);
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("chart-ci.yml")]
    [InlineData("release.yml")]
    public void No_workflow_hides_findings_behind_ignore_unfixed(string file)
        => Assert.DoesNotMatch(new Regex(@"ignore-unfixed\s*:\s*['""]?true", RegexOptions.IgnoreCase), Workflow(file));

    [Fact]
    public void Every_scan_exception_has_a_reason_and_an_expiry_date()
    {
        var path = Path.Combine(RepoRoot(), ".trivyignore");
        Assert.True(File.Exists(path), ".trivyignore (the recorded scan-exception policy) is missing");
        var text = Normalize(File.ReadAllText(path));
        Assert.Contains("ignore-unfixed", text); // the file documents the policy decision itself
        Assert.Empty(WorkflowPolicy.TrivyIgnoreViolations(text));
    }

    // ---- negative tests: each mutation of the real file must be caught ---------------------------------------

    [Theory]
    [InlineData("uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1", "uses: actions/checkout@v7")]
    [InlineData("uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1", "uses: actions/checkout@3d3c42e # v7.0.1")]
    [InlineData("uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1", "uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1")]
    [InlineData("uses: aquasecurity/trivy-action@ed142fd0673e97e23eac54620cfb913e5ce36c25 # v0.36.0", "uses: aquasecurity/trivy-action@master # v0.36.0")]
    [InlineData("uses: ./.github/workflows/ci.yml", "uses: LupusMalusDeviant/Whiskers/.github/workflows/ci.yml@main")]
    public void An_unpinned_action_is_caught(string from, string to)
        => Assert.NotEmpty(WorkflowPolicy.UnpinnedActions(Mutate(Workflow("release.yml"), from, to)));

    [Theory]
    [InlineData("permissions: {}\n\n# One release", "permissions:\n  contents: write\n\n# One release")]
    [InlineData("permissions: {}\n\n# One release", "permissions: write-all\n\n# One release")]
    [InlineData("permissions: {}\n\n# One release", "# One release")] // no workflow-level declaration
    [InlineData("      contents: read\n      packages: write   # push the chart to GHCR", "      contents: write\n      packages: write")]
    [InlineData("      packages: write   # push the chart to GHCR", "      packages: write\n      id-token: write")]
    [InlineData("      contents: read\n      packages: read    # pull the staged digests", "      contents: read\n      packages: write")]
    [InlineData("    permissions:\n      contents: read\n      packages: read\n    outputs:", "    outputs:")] // prepare without permissions
    public void A_permission_escalation_is_caught(string from, string to)
        => Assert.NotEmpty(WorkflowPolicy.PermissionViolations(Mutate(Workflow("release.yml"), from, to), AllowedWrites["release.yml"]));

    [Fact]
    public void A_write_permission_in_ci_is_caught()
        => Assert.NotEmpty(WorkflowPolicy.PermissionViolations(
            Mutate(Workflow("ci.yml"), "    permissions:\n      contents: read", "    permissions:\n      contents: write"),
            AllowedWrites["ci.yml"]));

    [Theory]
    [InlineData("TAG=\"$INPUT_TAG\"", "TAG=\"${{ inputs.tag }}\"")]
    [InlineData("TAG=\"$INPUT_TAG\"", "TAG=\"${{ github.event.inputs.tag }}\"")]
    [InlineData("TAG=\"$INPUT_TAG\"", "TAG=\"${{github.event.head_commit.message}}\"")]
    [InlineData("TAG=\"$INPUT_TAG\"", "TAG=\"${{ github.ref_name }}\"")]
    [InlineData("helm lint deploy/helm/whiskers", "helm lint deploy/helm/whiskers --version ${{ needs.prepare.outputs.ver }}")]
    public void An_interpolated_expression_in_a_script_is_caught(string from, string to)
        => Assert.NotEmpty(WorkflowPolicy.UnsafeRunInterpolations(Mutate(Workflow("release.yml"), from, to)));

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("chart-ci.yml")]
    [InlineData("release.yml")]
    public void No_step_output_is_written_from_an_unchecked_command_substitution(string file)
        => Assert.Empty(WorkflowPolicy.SwallowedOutputFailures(Workflow(file)));

    [Fact]
    public void A_step_output_written_from_a_command_substitution_is_caught()
        => Assert.NotEmpty(WorkflowPolicy.SwallowedOutputFailures(Mutate(Workflow("release.yml"),
            "echo \"amd64_image=${AMD64_IMAGE}\" >> \"$GITHUB_OUTPUT\"",
            "echo \"amd64_image=$(image_digest \"$AMD64_INDEX\" amd64)\" >> \"$GITHUB_OUTPUT\"")));

    [Theory]
    [InlineData("    needs: [prepare, test, build, scan]", "    needs: [prepare, build, scan]")]         // publish-image without tests
    [InlineData("    needs: [prepare, test, build, scan]", "    needs: [prepare, test, build]")]         // publish-image without scan
    [InlineData("    needs: [prepare, test, scan, publish-image]", "    needs: [prepare, test, publish-image]")]
    [InlineData("    needs: [prepare, test, build, scan, publish-image, publish-chart]", "    needs: [prepare, build, scan, publish-image, publish-chart]")]
    [InlineData("PUBLISHED=\"$(gh release list", "PUBLISHED=\"$(git tag -l 'v*'; true) #")] // latest from git tags, not published releases
    [InlineData("    needs: [prepare, test]\n    runs-on", "    needs: [prepare]\n    runs-on")]       // build before tests
    [InlineData("      ref: ${{ needs.prepare.outputs.sha }}\n\n  # Build each", "      ref: ${{ github.sha }}\n\n  # Build each")] // tests on another commit
    [InlineData("      - name: Checkout the tag commit\n        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n        with:\n          ref: ${{ needs.prepare.outputs.sha }}\n\n      - name: Set up QEMU",
                "      - name: Checkout the tag commit\n        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n\n      - name: Set up QEMU")] // build checks out the dispatched ref
    [InlineData("          ref: refs/tags/${{ steps.tag.outputs.tag }}", "          ref: ${{ github.ref }}")]
    [InlineData("if [ \"$GITHUB_REF\" != \"refs/tags/${TAG}\" ]; then", "if false; then")] // dispatch from any ref
    [InlineData("CSPROJ_VER=", "IGNORED_VER=")] // tag no longer compared with the csproj version
    public void A_broken_release_gate_is_caught(string from, string to)
        => Assert.NotEmpty(WorkflowPolicy.ReleaseGateViolations(Mutate(Workflow("release.yml"), from, to), Workflow("ci.yml")));

    [Theory]
    [InlineData("  workflow_call:", "  workflow_dispatch:")]
    [InlineData("dotnet test src/Whiskers.Tests/Whiskers.Tests.csproj", "echo skipped")]
    [InlineData("          ref: ${{ inputs.ref }}", "          ref: main")]
    public void A_ci_workflow_that_cannot_gate_the_release_is_caught(string from, string to)
        => Assert.NotEmpty(WorkflowPolicy.ReleaseGateViolations(Workflow("release.yml"), Mutate(Workflow("ci.yml"), from, to)));

    [Theory]
    [InlineData("          image-ref: ${{ env.IMAGE }}@${{ needs.build.outputs.arm64_image }}", "          image-ref: ${{ env.IMAGE }}@${{ needs.build.outputs.amd64_image }}")] // arm64 unscanned
    [InlineData("          image-ref: ${{ env.IMAGE }}@${{ needs.build.outputs.amd64_image }}", "          image-ref: ${{ env.IMAGE }}:scan")] // by tag, not by digest
    [InlineData("          severity: CRITICAL\n          vuln-type: os,library\n          trivyignores: .trivyignore\n\n      - name: Trivy scan arm64",
                "          severity: CRITICAL\n          vuln-type: os,library\n          ignore-unfixed: true\n          trivyignores: .trivyignore\n\n      - name: Trivy scan arm64")]
    [InlineData("          exit-code: \"1\"\n          severity: CRITICAL\n          vuln-type: os,library\n          trivyignores: .trivyignore\n\n  # Tag exactly",
                "          exit-code: \"0\"\n          severity: CRITICAL\n          vuln-type: os,library\n          trivyignores: .trivyignore\n\n  # Tag exactly")] // a scan that never fails
    [InlineData("          trivyignores: .trivyignore\n\n  # Tag exactly", "\n  # Tag exactly")] // exceptions file not used
    [InlineData("          platforms: linux/arm64", "          platforms: linux/amd64,linux/arm64")]
    [InlineData("          platforms: linux/arm64", "          platforms: linux/amd64")]
    [InlineData("outputs: type=image,name=${{ env.IMAGE }},push-by-digest=true,name-canonical=true,push=true\n          cache-from: type=gha,scope=release-amd64",
                "push: true\n          tags: ${{ env.IMAGE }}:latest\n          cache-from: type=gha,scope=release-amd64")] // tagged before the scan
    [InlineData("      - name: Refuse an already-published image version",
                "      - name: Rebuild\n        uses: docker/build-push-action@c3c9e263c25d99ce0380d002d59b67737d91b0dc # v7.4.0\n        with:\n          platforms: linux/amd64\n          push: true\n\n      - name: Refuse an already-published image version")] // rebuild after the scan
    [InlineData("          AMD64_INDEX: ${{ needs.build.outputs.amd64_index }}\n          ARM64_INDEX: ${{ needs.build.outputs.arm64_index }}\n          AMD64_IMAGE",
                "          AMD64_INDEX: ${{ needs.build.outputs.amd64_index }}\n          AMD64_IMAGE")] // publishes something other than the scanned arm64 digest
    public void A_weakened_scan_gate_is_caught(string from, string to)
        => Assert.NotEmpty(WorkflowPolicy.ScanViolations(Mutate(Workflow("release.yml"), from, to)));

    [Fact]
    public void Removing_the_arm64_scan_step_is_caught()
    {
        var text = Workflow("release.yml");
        var start = text.IndexOf("      - name: Trivy scan arm64", StringComparison.Ordinal);
        var end = text.IndexOf("  # Tag exactly the scanned digests", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        Assert.NotEmpty(WorkflowPolicy.ScanViolations(text[..start] + text[end..]));
    }

    [Theory]
    [InlineData("run: bash .github/scripts/assert-not-published.sh image-matching \"${IMAGE}:${VER}\" \"${AMD64_IMAGE}\" \"${ARM64_IMAGE}\"", "run: echo skipped")]
    [InlineData("run: bash .github/scripts/assert-not-published.sh image-matching \"${IMAGE}:${VER}\" \"${AMD64_IMAGE}\" \"${ARM64_IMAGE}\"", "run: bash .github/scripts/assert-not-published.sh image-matching \"${IMAGE}:${VER}\" \"${AMD64_INDEX}\" \"${ARM64_IMAGE}\"")]
    [InlineData("run: bash .github/scripts/assert-not-published.sh chart \"${CHART}\" \"${VER}\"", "run: echo skipped")]
    [InlineData("run: bash .github/scripts/assert-not-published.sh release \"${TAG}\"", "run: echo skipped")]
    [InlineData("if [ \"$STATE\" = \"absent\" ]; then", "if true; then")]                       // re-tags an existing version
    [InlineData("          STATE: ${{ steps.existing.outputs.state }}\n", "")]                  // state of the check not used
    [InlineData("bash .github/scripts/assert-not-published.sh image \"${IMAGE}:${VER}\"\n", "true\n")] // prepare no longer strict
    [InlineData("bash .github/scripts/assert-not-published.sh image \"${IMAGE}:${VER}\"\n",
                "bash .github/scripts/assert-not-published.sh image-matching \"${IMAGE}:${VER}\" x y\n")] // fresh run accepts an existing version
    public void A_missing_existence_check_is_caught(string from, string to)
        => Assert.NotEmpty(WorkflowPolicy.ImmutabilityViolations(Mutate(Workflow("release.yml"), from, to)));

    [Fact]
    public void An_existence_check_after_the_push_is_caught()
    {
        // Move the chart check behind the push step.
        var text = Workflow("release.yml");
        const string check = "      - name: Refuse an already-published chart version\n        env:\n          VER: ${{ needs.prepare.outputs.ver }}\n        run: bash .github/scripts/assert-not-published.sh chart \"${CHART}\" \"${VER}\"\n\n";
        text = Mutate(text, check, "");
        text = Mutate(text, "          helm push \"dist-chart/whiskers-${VER}.tgz\" \"${CHART_REPO}\"\n",
            "          helm push \"dist-chart/whiskers-${VER}.tgz\" \"${CHART_REPO}\"\n\n" + check.TrimEnd('\n') + "\n");
        Assert.NotEmpty(WorkflowPolicy.ImmutabilityViolations(text));
    }

    [Theory]
    [InlineData("# No exceptions are recorded.", "CVE-2026-00001 exp:2027-01-01")]                       // no reason
    [InlineData("# No exceptions are recorded.", "# reason: base image, no fix upstream\nCVE-2026-00001")] // no expiry
    [InlineData("# No exceptions are recorded.", "# reason:\nCVE-2026-00001 exp:2027-01-01")]              // empty reason
    public void A_scan_exception_without_reason_or_expiry_is_caught(string from, string to)
    {
        var text = Normalize(File.ReadAllText(Path.Combine(RepoRoot(), ".trivyignore")));
        Assert.NotEmpty(WorkflowPolicy.TrivyIgnoreViolations(Mutate(text, from, to)));
    }

    [Fact]
    public void A_recorded_scan_exception_is_accepted()
    {
        var text = Normalize(File.ReadAllText(Path.Combine(RepoRoot(), ".trivyignore")));
        var withEntry = Mutate(text, "# No exceptions are recorded.",
            "# reason: only in the build stage, not reachable at runtime (decided by the maintainer)\nCVE-2026-00001 exp:2027-01-01");
        Assert.Empty(WorkflowPolicy.TrivyIgnoreViolations(withEntry));
    }

    // ---- assert-not-published.sh, run against stub docker/helm/gh ------------------------------------------------

    private const string Amd = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Arm = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Att = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static string Index(params (string Os, string Arch, string Digest)[] manifests)
        => "{\"manifests\":[" + string.Join(",", manifests.Select(m =>
            $"{{\"digest\":\"{m.Digest}\",\"platform\":{{\"os\":\"{m.Os}\",\"architecture\":\"{m.Arch}\"}}}}")) + "]}";

    public static TheoryData<string, int, string, string, int, string?> ScriptCases() => new()
    {
        // kind, stub exit code, stub stderr, stub --raw output, expected exit code, expected stdout fragment
        { "image", 0, "", "", 1, null },                                                          // exists
        { "image", 1, "ERROR: ghcr.io/o/whiskers:9.9.9: not found", "", 0, "not published yet" },  // registry: absent
        { "image", 1, "ERROR: manifest unknown", "", 0, "not published yet" },
        { "image", 1, "denied: permission_denied: read_package", "", 1, null },                  // auth error
        { "image", 127, "docker: command not found", "", 1, null },                             // tool missing
        { "image", 1, "error getting credentials - err: exec: \"docker-credential-x\": executable file not found in $PATH", "", 1, null },
        { "image", 1, "Get \"https://ghcr.io/v2/\": dial tcp: lookup ghcr.io: no such host", "", 1, null }, // outage
        { "chart", 0, "", "", 1, null },
        { "chart", 1, "Error: ghcr.io/o/charts/whiskers:9.9.9: not found", "", 0, "not published yet" },
        { "chart", 1, "Error: unexpected status from HEAD request: 500", "", 1, null },
        { "release", 0, "", "", 1, null },
        { "release", 1, "release not found", "", 0, "not published yet" },
        { "release", 1, "HTTP 401: Bad credentials", "", 1, null },
        { "release", 126, "gh: Permission denied", "", 1, null },
        // image-matching: absent, identical (attestations ignored), different content, extra platform, broken probe
        { "image-matching", 1, "ERROR: ghcr.io/o/whiskers:9.9.9: not found", "", 0, "state=absent" },
        { "image-matching", 0, "", Index(("linux", "amd64", Amd), ("linux", "arm64", Arm), ("unknown", "unknown", Att)), 0, "state=identical" },
        { "image-matching", 0, "", Index(("linux", "arm64", Arm), ("linux", "amd64", Amd)), 0, "state=identical" },
        { "image-matching", 0, "", Index(("linux", "amd64", Amd), ("linux", "arm64", Att)), 1, null },
        { "image-matching", 0, "", Index(("linux", "amd64", Amd)), 1, null },
        { "image-matching", 0, "", Index(("linux", "amd64", Amd), ("linux", "arm64", Arm), ("linux", "riscv64", Att)), 1, null },
        { "image-matching", 127, "docker: command not found", "", 1, null },
    };

    [SkippableTheory]
    [MemberData(nameof(ScriptCases))]
    public void The_existence_check_fails_closed(string kind, int stubExit, string stubStderr, string stubRaw, int expectedExit, string? expectedOutput)
    {
        var bash = FindBash();
        Skip.If(bash is null, "no bash available to run .github/scripts/assert-not-published.sh");
        if (kind == "image-matching")
            Skip.If(RunBash(bash!, ["-c", "command -v jq"], new Dictionary<string, string?>()).Exit != 0, "jq is not installed");

        var stubDir = Directory.CreateTempSubdirectory("whiskers-stub-");
        try
        {
            const string stub = "#!/bin/bash\nfor a in \"$@\"; do if [ \"$a\" = \"--raw\" ]; then printf '%s' \"$STUB_RAW\"; exit 0; fi; done\nprintf '%s\\n' \"$STUB_OUT\" >&2\nexit \"$STUB_RC\"\n";
            foreach (var tool in new[] { "docker", "helm", "gh" })
            {
                var path = Path.Combine(stubDir.FullName, tool);
                File.WriteAllText(path, stub);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            string[] args = kind switch
            {
                "image" => ["ghcr.io/o/whiskers:9.9.9"],
                "chart" => ["oci://ghcr.io/o/charts/whiskers", "9.9.9"],
                "release" => ["v9.9.9"],
                _ => ["ghcr.io/o/whiskers:9.9.9", Amd, Arm],
            };
            var script = Path.Combine(RepoRoot(), ".github", "scripts", "assert-not-published.sh").Replace('\\', '/');
            var env = new Dictionary<string, string?>
            {
                ["PATH"] = stubDir.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                ["STUB_RC"] = stubExit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["STUB_OUT"] = stubStderr,
                ["STUB_RAW"] = stubRaw,
                ["GITHUB_REPOSITORY"] = "o/r",
                ["GITHUB_OUTPUT"] = null, // never write into the real step output when this runs inside CI
            };
            var (exit, output) = RunBash(bash!, [script, kind, .. args], env);

            Assert.True(expectedExit == exit, $"exit {exit}, expected {expectedExit}; output:\n{output}");
            if (expectedOutput is not null) Assert.Contains(expectedOutput, output);
        }
        finally
        {
            stubDir.Delete(recursive: true);
        }
    }

    private static string? FindBash()
    {
        if (!OperatingSystem.IsWindows()) return File.Exists("/bin/bash") ? "/bin/bash" : null;
        // Git for Windows; System32\bash.exe would be WSL, which does not see these paths.
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "Git", "bin", "bash.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static (int Exit, string Output) RunBash(string bash, string[] args, Dictionary<string, string?> env)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(bash)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (key, value) in env)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("bash did not finish"); }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static string Mutate(string text, string from, string to)
    {
        Assert.True(text.Contains(from, StringComparison.Ordinal), $"mutation anchor not found: {from}");
        var index = text.IndexOf(from, StringComparison.Ordinal);
        return text[..index] + to + text[(index + from.Length)..];
    }

    private static string Workflow(string file)
        => Normalize(File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", file)));

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"could not find Whiskers.slnx above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }
}

/// <summary>The checks behind <see cref="ReleaseWorkflowTests"/>; each returns the violations it found.</summary>
internal static class WorkflowPolicy
{
    private static readonly Regex FullShaPin = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex UsesLine = new(@"^\s*(?:-\s+)?uses:\s*(?<value>\S+)(?<rest>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionComment = new(@"^\s+#\s*v\d+(\.\d+)*\s*$", RegexOptions.CultureInvariant);
    // No expression at all inside a shell script: event data, refs and step/job outputs reach scripts via env: only.
    private static readonly Regex UnsafeExpression = new(@"\$\{\{[^\n]*?(\}\}|$)", RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex TrivyIgnoreEntry = new(@"^\S+\s+exp:\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex ReasonLine = new(@"^#\s*reason:\s*\S", RegexOptions.CultureInvariant);

    private const string CiWorkflow = "./.github/workflows/ci.yml";
    private const string PrepareSha = "needs.prepare.outputs.sha";

    // ---- pins ----

    public static IReadOnlyList<string> UsesValues(string yaml)
    {
        var root = Parse(yaml);
        var values = new List<string>();
        foreach (var (_, job) in Jobs(root))
        {
            if (Scalar(Child(job, "uses")) is { } jobUses) values.Add(jobUses);
            values.AddRange(Steps(job).Select(s => Scalar(Child(s, "uses"))).OfType<string>());
        }
        return values;
    }

    public static IReadOnlyList<string> UnpinnedActions(string yaml)
    {
        var violations = new List<string>();
        foreach (var uses in UsesValues(yaml))
        {
            if (uses.StartsWith("./", StringComparison.Ordinal)) continue;
            if (!FullShaPin.IsMatch(uses)) violations.Add($"'{uses}' is not pinned to a full 40-character commit SHA");
        }
        // The parser drops comments; the version comment is checked on the raw lines.
        foreach (var line in yaml.Split('\n'))
        {
            var m = UsesLine.Match(line);
            if (!m.Success || m.Groups["value"].Value.StartsWith("./", StringComparison.Ordinal)) continue;
            if (!VersionComment.IsMatch(m.Groups["rest"].Value))
                violations.Add($"'{line.Trim()}' does not name its release in a trailing '# vX.Y.Z' comment");
        }
        return violations;
    }

    // ---- permissions ----

    public static IReadOnlyList<string> PermissionViolations(string yaml, IReadOnlyDictionary<string, string[]> allowedWrites)
    {
        var root = Parse(yaml);
        var violations = new List<string>();

        var top = Child(root, "permissions");
        if (top is null) violations.Add("the workflow does not declare permissions (the repository default could be write)");
        else violations.AddRange(Writes(top).Select(w => $"workflow-level permission '{w}' is write"));

        foreach (var (id, job) in Jobs(root))
        {
            var perms = Child(job, "permissions");
            if (perms is null) { violations.Add($"job '{id}' does not declare its permissions"); continue; }
            var allowed = allowedWrites.TryGetValue(id, out var a) ? a : [];
            foreach (var scope in Writes(perms))
                if (!allowed.Contains(scope))
                    violations.Add($"job '{id}' holds '{scope}: write' but may not");
        }
        return violations;
    }

    private static IEnumerable<string> Writes(YamlNode permissions) => permissions switch
    {
        YamlScalarNode s when s.Value is "read-all" or "" or null => [],
        YamlScalarNode s => [s.Value!], // write-all or anything unknown counts as write
        YamlMappingNode m => m.Children
            .Where(kv => Scalar(kv.Value) is not ("read" or "none"))
            .Select(kv => Scalar(kv.Key) ?? "?"),
        _ => ["<unparseable>"],
    };

    // ---- shell-safe inputs ----

    public static IReadOnlyList<string> RunScripts(string yaml)
        => Jobs(Parse(yaml)).SelectMany(j => Steps(j.Job)).Select(s => Scalar(Child(s, "run"))).OfType<string>().ToList();

    public static IReadOnlyList<string> UnsafeRunInterpolations(string yaml)
        => RunScripts(yaml)
            .Where(run => UnsafeExpression.IsMatch(run))
            .Select(run => $"a run script interpolates an expression directly (pass it via env:): {UnsafeExpression.Match(run).Value}")
            .ToList();

    /// <summary>
    /// <c>echo "x=$(cmd)" &gt;&gt; "$GITHUB_OUTPUT"</c> exits 0 even when <c>cmd</c> fails and writes an empty
    /// output; the failure then surfaces in a later job. Assign first (set -e aborts), then write.
    /// </summary>
    public static IReadOnlyList<string> SwallowedOutputFailures(string yaml)
        => RunScripts(yaml)
            .SelectMany(run => run.Split('\n'))
            .Where(line => line.Contains("$(", StringComparison.Ordinal) && line.Contains("GITHUB_OUTPUT", StringComparison.Ordinal))
            .Select(line => $"a step output is written from an unchecked command substitution: {line.Trim()}")
            .ToList();

    // ---- release gates ----

    public static IReadOnlyList<string> ReleaseGateViolations(string releaseYaml, string ciYaml)
    {
        var root = Parse(releaseYaml);
        var jobs = Jobs(root).ToDictionary(j => j.Id, j => j.Job);
        var violations = new List<string>();

        if (!jobs.TryGetValue("prepare", out var prepare)) return ["the release has no 'prepare' job"];

        // The tests: ci.yml, called on the tag commit.
        var testJobs = jobs.Where(j => Scalar(Child(j.Value, "uses")) == CiWorkflow).Select(j => j.Key).ToList();
        if (testJobs.Count != 1) violations.Add($"expected exactly one job that calls {CiWorkflow}, found {testJobs.Count}");
        var test = testJobs.FirstOrDefault() ?? "test";
        if (testJobs.Count == 1 && Scalar(Child(Child(jobs[test], "with"), "ref"))?.Contains(PrepareSha) != true)
            violations.Add($"job '{test}' does not test the tag commit ({PrepareSha})");

        var ci = Parse(ciYaml);
        if (Child(Child(ci, "on"), "workflow_call") is null) violations.Add("ci.yml cannot be called by the release (no workflow_call)");
        var ciJobs = Jobs(ci).ToList();
        if (!ciJobs.SelectMany(j => Steps(j.Job)).Any(s => Scalar(Child(s, "run"))?.Contains("dotnet test") == true))
            violations.Add("ci.yml does not run 'dotnet test'");
        foreach (var (id, job) in ciJobs)
            foreach (var checkout in Steps(job).Where(s => Scalar(Child(s, "uses"))?.StartsWith("actions/checkout@") == true))
                if (Scalar(Child(Child(checkout, "with"), "ref"))?.Contains("inputs.ref") != true)
                    violations.Add($"ci.yml job '{id}' ignores the ref the release passes in");

        // Who builds, scans and publishes.
        var builders = JobsUsing(jobs, "docker/build-push-action@");
        var scanners = JobsUsing(jobs, "aquasecurity/trivy-action@");
        if (builders.Count == 0) violations.Add("no job builds the image");
        if (scanners.Count == 0) violations.Add("no job scans the image");
        var publishers = jobs.Where(j => Child(j.Value, "permissions") is { } p && Writes(p).Any() && !builders.Contains(j.Key))
            .Select(j => j.Key).ToList();
        if (publishers.Count == 0) violations.Add("no publishing job found");

        foreach (var b in builders)
            if (!Needs(jobs[b]).Contains(test)) violations.Add($"build job '{b}' does not need the test job '{test}'");
        foreach (var p in publishers)
        {
            var needs = Needs(jobs[p]);
            if (!needs.Contains(test)) violations.Add($"publish job '{p}' does not need the test job '{test}'");
            foreach (var s in scanners)
                if (!needs.Contains(s)) violations.Add($"publish job '{p}' does not need the scan job '{s}'");
        }

        // Every checkout is the validated tag commit.
        foreach (var (id, job) in jobs)
        foreach (var checkout in Steps(job).Where(s => Scalar(Child(s, "uses"))?.StartsWith("actions/checkout@") == true))
        {
            var reference = Scalar(Child(Child(checkout, "with"), "ref")) ?? "";
            var ok = id == "prepare" ? reference.StartsWith("refs/tags/", StringComparison.Ordinal) : reference.Contains(PrepareSha);
            if (!ok) violations.Add($"job '{id}' checks out '{(reference.Length == 0 ? "the dispatched ref" : reference)}', not the validated tag commit");
        }

        // The prepare job validates the tag against the run ref and the versions in the source.
        var prepareRuns = string.Join("\n", Steps(prepare).Select(s => Scalar(Child(s, "run"))).OfType<string>());
        if (Child(Child(root, "on"), "workflow_dispatch") is not null && !prepareRuns.Contains("\"$GITHUB_REF\" != \"refs/tags/"))
            violations.Add("a manual run is not refused when it is not started from the tag it names");
        foreach (var required in new[] { "^{commit}", "GITHUB_SHA", "Whiskers.csproj", "Chart.yaml" })
            if (!prepareRuns.Contains(required)) violations.Add($"the prepare job does not verify '{required}'");
        if (!Regex.IsMatch(prepareRuns, @"CSPROJ_VER=") || !Regex.IsMatch(prepareRuns, @"CHART_VER="))
            violations.Add("the prepare job does not compare the tag with the csproj and chart versions");
        if (!prepareRuns.Contains("gh release list") || prepareRuns.Contains("git tag -l"))
            violations.Add("`latest` is not decided from the published releases");

        return violations;
    }

    // ---- scan ----

    public static IReadOnlyList<string> ScanViolations(string releaseYaml)
    {
        var jobs = Jobs(Parse(releaseYaml)).ToDictionary(j => j.Id, j => j.Job);
        var violations = new List<string>();
        var builders = JobsUsing(jobs, "docker/build-push-action@");
        var scanners = JobsUsing(jobs, "aquasecurity/trivy-action@");

        // Build: one step per architecture, staged by digest, never tagged.
        var built = new List<string>();
        foreach (var b in builders)
        foreach (var step in Steps(jobs[b]).Where(s => Scalar(Child(s, "uses"))?.StartsWith("docker/build-push-action@") == true))
        {
            var with = Child(step, "with");
            var platforms = (Scalar(Child(with, "platforms")) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (platforms.Length != 1) violations.Add($"a build step in '{b}' builds {platforms.Length} platforms; each architecture must be its own scanned digest");
            built.AddRange(platforms);
            if (Scalar(Child(with, "outputs"))?.Contains("push-by-digest=true") != true || Child(with, "tags") is not null || Scalar(Child(with, "push")) == "true")
                violations.Add($"a build step in '{b}' tags or pushes by name before the scan; it must stage by digest only");
        }
        foreach (var arch in new[] { "linux/amd64", "linux/arm64" })
            if (!built.Contains(arch)) violations.Add($"{arch} is not built as its own image");

        // Nothing is built after the scan.
        foreach (var b in builders)
        foreach (var s in scanners)
            if (Ancestors(jobs, b).Contains(s)) violations.Add($"job '{b}' builds after the scan '{s}'; published images would not be the scanned ones");

        // Scan: both architectures, by digest, failing on CRITICAL, exceptions only from .trivyignore.
        var scannedRefs = new List<string>();
        foreach (var s in scanners)
        foreach (var step in Steps(jobs[s]).Where(x => Scalar(Child(x, "uses"))?.StartsWith("aquasecurity/trivy-action@") == true))
        {
            var with = Child(step, "with");
            var imageRef = Scalar(Child(with, "image-ref")) ?? "";
            scannedRefs.Add(imageRef);
            if (!Regex.IsMatch(imageRef, @"@\$\{\{\s*needs\.[A-Za-z0-9_-]+\.outputs\."))
                violations.Add($"scan '{imageRef}' is not by the digest of the build job");
            if (Scalar(Child(with, "exit-code")) != "1") violations.Add($"scan of '{imageRef}' does not fail the run");
            if ((Scalar(Child(with, "severity")) ?? "").Split(',').All(x => x.Trim() != "CRITICAL"))
                violations.Add($"scan of '{imageRef}' does not gate on CRITICAL");
            if (Scalar(Child(with, "ignore-unfixed")) is { } unfixed && unfixed.Equals("true", StringComparison.OrdinalIgnoreCase))
                violations.Add($"scan of '{imageRef}' hides unfixed findings (ignore-unfixed) instead of recording them in .trivyignore");
            if (Scalar(Child(with, "trivyignores")) != ".trivyignore")
                violations.Add($"scan of '{imageRef}' does not take its exceptions from .trivyignore");
        }
        foreach (var arch in new[] { "amd64", "arm64" })
            if (!scannedRefs.Any(r => r.Contains(arch, StringComparison.Ordinal))) violations.Add($"the {arch} image is not scanned");

        // Publish: the image index is assembled from the build job's digests, verified against both scanned ones.
        var assembles = jobs.SelectMany(j => Steps(j.Value).Select(s => (Job: j.Key, Step: s)))
            .Where(x => Scalar(Child(x.Step, "run"))?.Contains("imagetools create -t \"${IMAGE}:${VER}\"") == true).ToList();
        if (assembles.Count != 1) violations.Add($"expected exactly one step that publishes the versioned image, found {assembles.Count}");
        foreach (var (job, step) in assembles)
        {
            var env = string.Join("\n", (Child(step, "env") as YamlMappingNode)?.Children.Select(kv => Scalar(kv.Value)) ?? []);
            foreach (var arch in new[] { "amd64", "arm64" })
            {
                if (!Regex.IsMatch(env, $@"needs\.[A-Za-z0-9_-]+\.outputs\.{arch}_index"))
                    violations.Add($"'{job}' does not publish the staged {arch} digest");
                if (!Regex.IsMatch(env, $@"needs\.[A-Za-z0-9_-]+\.outputs\.{arch}_image"))
                    violations.Add($"'{job}' does not verify the published {arch} manifest against the scanned digest");
            }
        }
        return violations;
    }

    // ---- immutability ----

    public static IReadOnlyList<string> ImmutabilityViolations(string releaseYaml)
    {
        var jobs = Jobs(Parse(releaseYaml)).ToList();
        var violations = new List<string>();

        // A fresh run refuses every existing version strictly, before anything is built.
        var prepare = jobs.FirstOrDefault(j => j.Id == "prepare").Job;
        var prepareRuns = prepare is null ? "" : string.Join("\n", Steps(prepare).Select(s => Scalar(Child(s, "run"))).OfType<string>());
        foreach (var kind in new[] { "image", "chart", "release" })
            if (!prepareRuns.Contains($"assert-not-published.sh {kind} "))
                violations.Add($"the prepare job does not strictly refuse an already-published {kind}");

        // Right before each push, the publishing job checks again. The image check accepts only an X.Y.Z that
        // already holds exactly the scanned digests (finishing a broken run), never other content.
        (string Kind, string Check, Func<YamlMappingNode, bool> IsPush)[] artifacts =
        [
            ("image", "image-matching", s => Scalar(Child(s, "run"))?.Contains("imagetools create -t \"${IMAGE}:${VER}\"") == true),
            ("chart", "chart", s => Scalar(Child(s, "run"))?.Contains("helm push") == true),
            ("release", "release", s => Scalar(Child(s, "uses"))?.StartsWith("softprops/action-gh-release@") == true),
        ];
        foreach (var (kind, check, isPush) in artifacts)
        {
            var pushes = jobs.Where(j => Steps(j.Job).Any(isPush)).ToList();
            if (pushes.Count == 0) violations.Add($"no step publishes the {kind}");
            foreach (var (id, job) in pushes)
            {
                var steps = Steps(job).ToList();
                var pushAt = steps.FindIndex(s => isPush(s));
                var checkAt = steps.FindIndex(s => Scalar(Child(s, "run"))?.Contains($"assert-not-published.sh {check} ") == true);
                if (checkAt < 0) { violations.Add($"job '{id}' publishes the {kind} without checking that the version does not exist"); continue; }
                if (checkAt > pushAt) violations.Add($"job '{id}' checks the {kind} only after publishing it");
                if (kind == "image") violations.AddRange(ImageCheckViolations(id, steps[checkAt], steps[pushAt]));
            }
        }
        return violations;
    }

    private static IEnumerable<string> ImageCheckViolations(string job, YamlMappingNode check, YamlMappingNode push)
    {
        var run = Scalar(Child(check, "run")) ?? "";
        var env = Child(check, "env");
        if (!run.Contains("\"${AMD64_IMAGE}\" \"${ARM64_IMAGE}\"")
            || Scalar(Child(env, "AMD64_IMAGE"))?.Contains(".outputs.amd64_image") != true
            || Scalar(Child(env, "ARM64_IMAGE"))?.Contains(".outputs.arm64_image") != true)
            yield return $"job '{job}' does not compare an existing image with the scanned amd64/arm64 digests";

        var checkId = Scalar(Child(check, "id"));
        var state = Scalar(Child(Child(push, "env"), "STATE")) ?? "";
        if (checkId is null || !Regex.IsMatch(state, $@"^\$\{{\{{\s*steps\.{Regex.Escape(checkId)}\.outputs\.state\s*\}}\}}$"))
            yield return $"job '{job}' does not take the result of its existence check into the publish step";
        if (!Regex.IsMatch(Scalar(Child(push, "run")) ?? "", @"if \[ ""\$STATE"" = ""absent"" \]; then\s*\n\s*docker buildx imagetools create -t"))
            yield return $"job '{job}' tags X.Y.Z even when the version already exists";
    }

    // ---- .trivyignore ----

    public static IReadOnlyList<string> TrivyIgnoreViolations(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        var violations = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!TrivyIgnoreEntry.IsMatch(line)) violations.Add($"'{line}' has no expiry ('<id> exp:YYYY-MM-DD')");
            if (i == 0 || !ReasonLine.IsMatch(lines[i - 1])) violations.Add($"'{line}' is not preceded by a '# reason: ...' line");
        }
        return violations;
    }

    // ---- YAML helpers ----

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlNode? Child(YamlNode? node, string key)
        => node is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    private static string? Scalar(YamlNode? node) => (node as YamlScalarNode)?.Value;

    private static IEnumerable<(string Id, YamlMappingNode Job)> Jobs(YamlMappingNode root)
        => (Child(root, "jobs") as YamlMappingNode)?.Children
               .Select(kv => (Scalar(kv.Key)!, (YamlMappingNode)kv.Value))
           ?? [];

    private static IEnumerable<YamlMappingNode> Steps(YamlMappingNode job)
        => (Child(job, "steps") as YamlSequenceNode)?.Children.OfType<YamlMappingNode>() ?? [];

    private static List<string> Needs(YamlMappingNode job) => Child(job, "needs") switch
    {
        YamlScalarNode s => [s.Value!],
        YamlSequenceNode q => q.Children.Select(Scalar).OfType<string>().ToList(),
        _ => [],
    };

    private static HashSet<string> Ancestors(Dictionary<string, YamlMappingNode> jobs, string id)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>(Needs(jobs[id]));
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!seen.Add(next) || !jobs.TryGetValue(next, out var job)) continue;
            foreach (var n in Needs(job)) stack.Push(n);
        }
        return seen;
    }

    private static List<string> JobsUsing(Dictionary<string, YamlMappingNode> jobs, string actionPrefix)
        => jobs.Where(j => Steps(j.Value).Any(s => Scalar(Child(s, "uses"))?.StartsWith(actionPrefix, StringComparison.Ordinal) == true))
            .Select(j => j.Key).ToList();
}
