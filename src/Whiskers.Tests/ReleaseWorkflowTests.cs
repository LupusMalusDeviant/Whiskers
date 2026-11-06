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
    private static readonly string[] Workflows = ["ci.yml", "chart-ci.yml", "release.yml", "security.yml"];

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
            ["move-tags"] = ["packages"],                 // move X.Y and latest after the GitHub release exists
            ["security"] = ["security-events"],           // R1-51: calls security.yml, whose CodeQL job uploads its SARIF
        },
        ["security.yml"] = new()
        {
            ["codeql"] = ["security-events"],             // upload the SARIF to code scanning
        },
    };

    // ---- R1-47: mandatory integration and artifact checks --------------------------------------------------------

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("chart-ci.yml")]
    [InlineData("release.yml")]
    [InlineData("security.yml")]
    public void Every_job_is_time_limited(string file)
        => Assert.Empty(WorkflowPolicy.TimeLimitViolations(Workflow(file)));

    [Fact]
    public void A_job_without_a_time_limit_is_caught()
        => Assert.NotEmpty(WorkflowPolicy.TimeLimitViolations(Mutate(Workflow("ci.yml"), "    timeout-minutes: 45\n", "")));

    [Fact]
    public void The_suite_and_the_artifact_are_checked_fail_closed()
        => Assert.Empty(WorkflowPolicy.IntegrationGateViolations(Workflow("release.yml"), Workflow("ci.yml"), Workflow("security.yml")));

    [Theory]
    [InlineData("ci.yml", "          bash .github/scripts/check-test-results.sh TestResults/docker.trx 70 --no-skips --require .github/required-tests/docker.txt\n", "")]  // skips of the PostgreSQL suites pass
    [InlineData("ci.yml", "TestResults/docker.trx 70 --no-skips", "TestResults/docker.trx 70")]                                // skipped tests allowed
    [InlineData("ci.yml", "--filter \"Category=RequiresDocker\"", "--filter \"Category=Nothing\"")]                           // the suite never runs
    [InlineData("ci.yml", "          bash .github/scripts/check-test-results.sh TestResults/in-process.trx 3000 --require .github/required-tests/in-process.txt\n", "")]  // only "some test passed"
    [InlineData("ci.yml", " --require .github/required-tests/docker.txt", "")]                                                 // the Identity test may vanish behind the floor
    [InlineData("ci.yml", " --require .github/required-tests/in-process.txt", "")]                                             // the endpoint matrix may vanish behind the floor
    [InlineData("ci.yml", "--require .github/required-tests/docker.txt", "--require .github/required-tests/in-process.txt")]   // the wrong manifest for the run
    [InlineData("ci.yml", "artifact-checks.sh previous-release \"$VER\")", "artifact-checks.sh previous-release)")]             // N-1 may be the release under test
    [InlineData("ci.yml", "        run: bash .github/scripts/artifact-checks.sh self-test", "        run: echo skipped")]             // log disclosure unproven
    [InlineData("release.yml", "        run: bash .github/scripts/artifact-checks.sh self-test", "        run: echo skipped")]
    [InlineData("ci.yml", "        run: bash .github/scripts/artifact-checks.sh hardened \"$ARTIFACT\"", "        run: echo skipped")]
    [InlineData("ci.yml", "          bash .github/scripts/artifact-checks.sh upgrade \"$ARTIFACT\" \"$PREVIOUS\"", "          echo skipped")]
    [InlineData("ci.yml", "      - name: Trivy scan (gate, fail on CRITICAL)\n        uses: aquasecurity", "      - name: Trivy scan (gate, fail on CRITICAL)\n        if: false\n        uses: aquasecurity")]
    [InlineData("release.yml", "    needs: [prepare, test, security, build, scan, artifact-test]", "    needs: [prepare, test, security, build, scan]")] // publish before the artifact test
    [InlineData("release.yml", "        run: bash .github/scripts/artifact-checks.sh prepare \"${IMAGE}@${AMD64_IMAGE}\" \"$ARTIFACT\"",
                "        run: docker build -t \"$ARTIFACT\" .")]                                                          // tests a rebuild, not the staged digest
    [InlineData("release.yml", "          AMD64_IMAGE: ${{ needs.build.outputs.amd64_image }}\n        run: bash .github/scripts/artifact-checks.sh prepare",
                "          AMD64_IMAGE: ${{ needs.build.outputs.amd64_index }}\n        run: bash .github/scripts/artifact-checks.sh prepare")]
    [InlineData("release.yml", "        run: bash .github/scripts/artifact-checks.sh prepare \"${IMAGE}@${AMD64_IMAGE}\" \"$ARTIFACT\"",
                "        run: |\n          docker pull \"${IMAGE}@${AMD64_IMAGE}\"\n          bash .github/scripts/artifact-checks.sh prepare \"${IMAGE}@${AMD64_IMAGE}\" \"$ARTIFACT\"")] // raw docker output in the log
    [InlineData("ci.yml", "        run: bash .github/scripts/artifact-checks.sh self-test",
                "        run: |\n          docker image ls\n          bash .github/scripts/artifact-checks.sh self-test")]   // raw docker output in the log
    [InlineData("release.yml", "        run: bash .github/scripts/artifact-checks.sh prepare \"${IMAGE}@${AMD64_IMAGE}\" \"$ARTIFACT\"",
                "        run: |\n          /usr/bin/docker image ls\n          bash .github/scripts/artifact-checks.sh prepare \"${IMAGE}@${AMD64_IMAGE}\" \"$ARTIFACT\"")] // docker through a path
    [InlineData("ci.yml", "        run: bash .github/scripts/artifact-checks.sh self-test",
                "        run: |\n          \"docker\" image ls\n          bash .github/scripts/artifact-checks.sh self-test")]   // docker in quotes
    [InlineData("ci.yml", "        run: bash .github/scripts/artifact-checks.sh self-test",
                "        run: |\n          docker-compose ps\n          bash .github/scripts/artifact-checks.sh self-test")]   // docker-compose
    [InlineData("ci.yml", "      - name: Disclosure self-test\n        run: bash .github/scripts/artifact-checks.sh self-test\n\n      - name: Hardened profile\n        run: bash .github/scripts/artifact-checks.sh hardened \"$ARTIFACT\"\n",
                "      - name: Hardened profile\n        run: bash .github/scripts/artifact-checks.sh hardened \"$ARTIFACT\"\n\n      - name: Disclosure self-test\n        run: bash .github/scripts/artifact-checks.sh self-test\n")] // self-test after the checks
    [InlineData("release.yml", "        run: bash .github/scripts/artifact-checks.sh hardened \"$ARTIFACT\"", "        run: echo skipped")]
    [InlineData("security.yml", "--exit-code 1", "--exit-code 0")]                                                              // secret findings ignored
    [InlineData("security.yml", "-p:CodeAnalysisTreatWarningsAsErrors=true", "-p:CodeAnalysisTreatWarningsAsErrors=false")]
    [InlineData("security.yml", "          if [ \"$FOUND\" -gt 0 ]; then\n", "          if false; then\n")]                 // vulnerable packages pass
    [InlineData("security.yml", "echo \"::error::${FOUND} vulnerable package(s)\"; exit 1\n", "echo \"::warning::${FOUND} vulnerable package(s)\"\n")] // reported, not failed
    [InlineData("security.yml", "          fail-on-severity: low", "          fail-on-severity: critical")]
    public void A_weakened_integration_or_artifact_gate_is_caught(string file, string from, string to)
    {
        string Text(string name) => name == file ? Mutate(Workflow(name), from, to) : Workflow(name);
        Assert.NotEmpty(WorkflowPolicy.IntegrationGateViolations(Text("release.yml"), Text("ci.yml"), Text("security.yml")));
    }

    // ---- the real files ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("chart-ci.yml")]
    [InlineData("release.yml")]
    [InlineData("security.yml")]
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
    [InlineData("security.yml")]
    public void Permissions_are_least_privilege(string file)
        => Assert.Empty(WorkflowPolicy.PermissionViolations(Workflow(file), AllowedWrites[file]));

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("chart-ci.yml")]
    [InlineData("release.yml")]
    [InlineData("security.yml")]
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
    [InlineData("security.yml")]
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
    [InlineData("security.yml")]
    public void No_step_output_is_written_from_an_unchecked_command_substitution(string file)
        => Assert.Empty(WorkflowPolicy.SwallowedOutputFailures(Workflow(file)));

    [Fact]
    public void A_step_output_written_from_a_command_substitution_is_caught()
        => Assert.NotEmpty(WorkflowPolicy.SwallowedOutputFailures(Mutate(Workflow("release.yml"),
            "echo \"amd64_image=${AMD64_IMAGE}\" >> \"$GITHUB_OUTPUT\"",
            "echo \"amd64_image=$(image_digest \"$AMD64_INDEX\" amd64)\" >> \"$GITHUB_OUTPUT\"")));

    [Theory]
    [InlineData("    needs: [prepare, test, security, build, scan, artifact-test]", "    needs: [prepare, security, build, scan, artifact-test]")] // publish-image without tests
    [InlineData("    needs: [prepare, test, security, build, scan, artifact-test]", "    needs: [prepare, test, security, build, artifact-test]")] // publish-image without scan
    [InlineData("    needs: [prepare, test, scan, publish-image]", "    needs: [prepare, test, publish-image]")]
    [InlineData("    needs: [prepare, test, build, scan, publish-image, publish-chart]", "    needs: [prepare, build, scan, publish-image, publish-chart]")]
    [InlineData("PUBLISHED=\"$(gh release list", "PUBLISHED=\"$(git tag -l 'v*'; true) #")] // latest from git tags, not published releases
    [InlineData("    needs: [prepare, test]\n    runs-on", "    needs: [prepare]\n    runs-on")]       // build before tests
    [InlineData("      ref: ${{ needs.prepare.outputs.sha }}\n      # R1-51 (contract 0008): only this", "      ref: ${{ github.sha }}\n      # R1-51 (contract 0008): only this")] // tests on another commit
    [InlineData("      - name: Checkout the tag commit\n        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n        with:\n          ref: ${{ needs.prepare.outputs.sha }}\n\n      - name: Set up QEMU",
                "      - name: Checkout the tag commit\n        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1\n\n      - name: Set up QEMU")] // build checks out the dispatched ref
    [InlineData("          ref: refs/tags/${{ steps.tag.outputs.tag }}", "          ref: ${{ github.ref }}")]
    [InlineData("if [ \"$GITHUB_REF\" != \"refs/tags/${TAG}\" ]; then", "if false; then")] // dispatch from any ref
    [InlineData("CSPROJ_VER=", "IGNORED_VER=")] // tag no longer compared with the csproj version
    [InlineData("      packages: write   # move X.Y and latest in GHCR\n    steps:",
                "      packages: write   # move X.Y and latest in GHCR\n    env:\n      PUSH_LATEST: ${{ needs.prepare.outputs.push_latest }}\n    steps:")] // a frozen decision at job level
    [InlineData("--limit 1000 --exclude-drafts --exclude-pre-releases --json tagName", "--limit 1000 --exclude-pre-releases --json tagName")] // a draft holds latest back
    [InlineData("    needs: [prepare, test, build, scan, publish-image, publish-chart, github-release]\n    runs-on: ubuntu-latest\n    permissions:\n      contents: read    # list",
                "    needs: [prepare, test, build, scan, publish-image, publish-chart]\n    runs-on: ubuntu-latest\n    permissions:\n      contents: read    # list")] // tags move before the GitHub release exists
    [InlineData("          GH_TOKEN: ${{ github.token }}\n        run: |\n          set -euo pipefail\n          # The moving tags",
                "          GH_TOKEN: ${{ github.token }}\n          PUSH_LATEST: ${{ needs.prepare.outputs.push_latest }}\n        run: |\n          set -euo pipefail\n          # The moving tags")] // a decision frozen in prepare, under any name
    [InlineData("          PUBLISHED=\"$(gh release list --repo \"$GITHUB_REPOSITORY\" --limit 1000 --exclude-drafts --exclude-pre-releases --json tagName --jq '.[].tagName')\"\n          ALL=",
                "          PUBLISHED=\"$LIST\"\n          ALL=")] // the list taken from elsewhere
    public void A_broken_release_gate_is_caught(string from, string to)
        => Assert.NotEmpty(WorkflowPolicy.ReleaseGateViolations(Mutate(Workflow("release.yml"), from, to), Workflow("ci.yml")));

    [Theory]
    [InlineData("  workflow_call:", "  workflow_dispatch:")]
    [InlineData("dotnet test src/Whiskers.Tests/Whiskers.Tests.csproj", "echo skipped")]
    [InlineData("          ref: ${{ inputs.ref }}", "          ref: main")]
    public void A_ci_workflow_that_cannot_gate_the_release_is_caught(string from, string to)
        => Assert.NotEmpty(WorkflowPolicy.ReleaseGateViolations(Workflow("release.yml"), Mutate(Workflow("ci.yml"), from, to)));

    // ---- R1-51: the release gate's wiring (contract 0008) ---------------------------------------------------------

    [Fact]
    public void The_release_gate_is_wired_as_contract_0008_specifies()
        => Assert.Empty(WorkflowPolicy.ReleaseGateWiringViolations(Workflow("release.yml"), Workflow("ci.yml"), Workflow("security.yml")));

    [Theory]
    // The mode: only release.yml's test job makes a release run, and only through the input.
    [InlineData("release.yml", "      release-gate: release\n", "")]
    [InlineData("release.yml", "      release-gate: release\n", "      release-gate: dry-run\n")]
    [InlineData("release.yml", "    uses: ./.github/workflows/security.yml\n    with:\n", "    uses: ./.github/workflows/security.yml\n    with:\n      release-gate: release\n")]
    [InlineData("ci.yml", "      release-gate:\n        description:", "      gate-mode:\n        description:")]
    [InlineData("ci.yml", "          WHISKERS_RELEASE_GATE: ${{ inputs.release-gate }}", "          WHISKERS_RELEASE_GATE: release")]
    [InlineData("ci.yml", "        env:\n          WHISKERS_RELEASE_GATE: ${{ inputs.release-gate }}\n", "")]
    [InlineData("ci.yml", "permissions: {}\n", "permissions: {}\n\nenv:\n  WHISKERS_RELEASE_GATE: ${{ inputs.release-gate }}\n")]
    [InlineData("ci.yml", "          set -euo pipefail\n          if [ \"$RELEASE_GATE\" = \"release\" ]; then",
                "          set -euo pipefail\n          export WHISKERS_RELEASE_GATE=release\n          if [ \"$RELEASE_GATE\" = \"release\" ]; then")]
    // The base comparison: every pull request and every release run, the right base, fail closed.
    [InlineData("ci.yml", "        if: github.event_name == 'pull_request' || inputs.release-gate == 'release'\n", "        if: github.event_name == 'pull_request'\n")]
    [InlineData("ci.yml", "        if: github.event_name == 'pull_request' || inputs.release-gate == 'release'\n", "        if: inputs.release-gate == 'release'\n")]
    [InlineData("ci.yml", "      - name: Release gate (compare with the base)\n", "      - name: Release gate (compare with the base)\n        continue-on-error: true\n")]
    [InlineData("ci.yml", "            export WHISKERS_GATE_BASE=previous-tag\n", "            export WHISKERS_GATE_BASE=\"$BASE_SHA\"\n")]
    [InlineData("ci.yml", "            git fetch --no-tags --depth=1 origin \"$BASE_SHA\"\n", "            git fetch --depth=1 origin \"$BASE_SHA\" '+refs/tags/v*:refs/tags/v*'\n")]
    [InlineData("ci.yml", "          BASE_SHA: ${{ github.event.pull_request.base.sha }}", "          BASE_SHA: ${{ github.event.pull_request.head.sha }}")]
    [InlineData("ci.yml", "          bash .github/scripts/check-test-results.sh TestResults/gate-base.trx 1 --no-skips\n", "")]
    [InlineData("ci.yml", "TestResults/gate-base.trx 1 --no-skips", "TestResults/gate-base.trx 1")]
    [InlineData("ci.yml", "          export WHISKERS_GATE_BASE_REQUIRED=true\n", "")]                                                  // a lost base passes
    [InlineData("ci.yml", "git fetch --no-tags --unshallow origin", "git fetch --no-tags --depth=1 origin")]                      // no history: ancestry unknowable
    [InlineData("ci.yml", " --exclude-drafts --exclude-pre-releases", "")]                                                          // a draft as the base
    [InlineData("ci.yml", "            export WHISKERS_GATE_RELEASES=\"$RUNNER_TEMP/releases.txt\"\n", "")]                          // any pushed tag is the base
    [InlineData("ci.yml", "          RELEASE_GATE: ${{ inputs.release-gate }}\n          RELEASES_TOKEN: ${{ github.token }}\n", "          RELEASE_GATE: ${{ inputs.release-gate }}\n")]
    [InlineData("ci.yml", "          RELEASES_TOKEN: ${{ github.token }}\n", "          RELEASES_TOKEN: ${{ github.token }}\n          GH_TOKEN: ${{ github.token }}\n")] // the tests see the token
    [InlineData("ci.yml", "          unset RELEASES_TOKEN\n", "")]                                                                     // the tests see the token
    [InlineData("ci.yml", "          unset RELEASES_TOKEN\n", "          unset RELEASES_TOKEN\n          echo \"${{ github.token }}\" >/dev/null\n")]          // the token inline in the script
    [InlineData("ci.yml", "          unset RELEASES_TOKEN\n", "          unset RELEASES_TOKEN\n          echo \"${{ secrets.GITHUB_TOKEN }}\" >/dev/null\n")]  // a secret inline in the script
    [InlineData("ci.yml", "          RELEASES_TOKEN: ${{ github.token }}\n", "          RELEASES_TOKEN: ${{ github.token }}\n          OTHER: ${{ github.token }}\n")] // the token under another name
    [InlineData("ci.yml", "          RELEASES_TOKEN: ${{ github.token }}\n", "          RELEASES_TOKEN: ${{ github.token }}\n          OTHER: ${{ secrets.GITHUB_TOKEN }}\n")] // a secret under another name
    // The N-1 checks list the published releases with gh: both need GH_TOKEN (review round 3, M-1).
    [InlineData("ci.yml", "      - name: N-1 upgrade and recovery\n        env:\n          GH_TOKEN: ${{ github.token }}\n", "      - name: N-1 upgrade and recovery\n        env:\n          RELEASES_TOKEN: ${{ github.token }}\n")]
    [InlineData("release.yml", "          VER: ${{ needs.prepare.outputs.ver }}\n          GH_TOKEN: ${{ github.token }}\n        run: |\n          set -euo pipefail\n          PREVIOUS=", "          VER: ${{ needs.prepare.outputs.ver }}\n        run: |\n          set -euo pipefail\n          PREVIOUS=")]
    [InlineData("ci.yml", "            git fetch --no-tags --depth=1 origin \"$BASE_SHA\"\n", "            git fetch --no-tags --depth=1 origin \"$BASE_SHA\"\n            export GH_TOKEN=\"$RELEASES_TOKEN\"\n")] // and in a pull request
    // The image-runtime run: from its TRX file, with its manifest.
    [InlineData("ci.yml", " --no-skips --require .github/required-tests/image-runtime.txt", " --no-skips")]
    [InlineData("ci.yml", "TestResults/image-runtime.trx 7 --no-skips", "TestResults/image-runtime.trx 7")]
    [InlineData("ci.yml", "            --logger \"trx;LogFileName=image-runtime.trx\" --results-directory TestResults\n", "            --results-directory TestResults\n")]
    // security.yml in the release, on the tag commit, before anything is published.
    [InlineData("release.yml", "    needs: [prepare, test, security, build, scan, artifact-test]", "    needs: [prepare, test, build, scan, artifact-test]")]
    [InlineData("release.yml", "  security:\n    needs: prepare\n", "  security:\n    needs: prepare\n    if: github.event_name == 'push'\n")]
    [InlineData("release.yml", "    uses: ./.github/workflows/security.yml\n    with:\n      ref: ${{ needs.prepare.outputs.sha }}", "    uses: ./.github/workflows/security.yml\n    with:\n      ref: main")]
    [InlineData("security.yml", "  workflow_call:\n", "  workflow_run:\n")]
    [InlineData("security.yml", "        with:\n          ref: ${{ inputs.ref }}\n          fetch-depth: 0", "        with:\n          fetch-depth: 0")]
    // The tag commit is on main.
    [InlineData("release.yml", "          if ! git merge-base --is-ancestor \"$SHA\" refs/remotes/origin/main; then\n", "          if false; then\n")]
    [InlineData("release.yml", "          ref: refs/tags/${{ steps.tag.outputs.tag }}\n          fetch-depth: 0", "          ref: refs/tags/${{ steps.tag.outputs.tag }}")]
    public void A_broken_release_gate_wiring_is_caught(string file, string from, string to)
    {
        string Text(string name) => name == file ? Mutate(Workflow(name), from, to) : Workflow(name);
        var violations = WorkflowPolicy.ReleaseGateWiringViolations(Text("release.yml"), Text("ci.yml"), Text("security.yml"))
            .Concat(WorkflowPolicy.IntegrationGateViolations(Text("release.yml"), Text("ci.yml"), Text("security.yml")));
        Assert.NotEmpty(violations);
    }

    [Theory]
    [InlineData("          image-ref: ${{ env.IMAGE }}@${{ needs.build.outputs.arm64_image }}", "          image-ref: ${{ env.IMAGE }}@${{ needs.build.outputs.amd64_image }}")] // arm64 unscanned
    [InlineData("          image-ref: ${{ env.IMAGE }}@${{ needs.build.outputs.amd64_image }}", "          image-ref: ${{ env.IMAGE }}:scan")] // by tag, not by digest
    [InlineData("          severity: CRITICAL\n          vuln-type: os,library\n          trivyignores: .trivyignore\n\n      - name: Trivy scan arm64",
                "          severity: CRITICAL\n          vuln-type: os,library\n          ignore-unfixed: true\n          trivyignores: .trivyignore\n\n      - name: Trivy scan arm64")]
    [InlineData("          exit-code: \"1\"\n          severity: CRITICAL\n          vuln-type: os,library\n          trivyignores: .trivyignore\n\n  # GATE (R1-47)",
                "          exit-code: \"0\"\n          severity: CRITICAL\n          vuln-type: os,library\n          trivyignores: .trivyignore\n\n  # GATE (R1-47)")] // a scan that never fails
    [InlineData("          trivyignores: .trivyignore\n\n  # GATE (R1-47)", "\n  # GATE (R1-47)")] // exceptions file not used
    [InlineData("          platforms: linux/arm64", "          platforms: linux/amd64,linux/arm64")]
    [InlineData("          platforms: linux/arm64", "          platforms: linux/amd64")]
    [InlineData("platforms: linux/amd64\n          outputs: type=image,name=${{ env.IMAGE }},push-by-digest=true,name-canonical=true,push=true",
                "platforms: linux/amd64\n          push: true\n          tags: ${{ env.IMAGE }}:latest")] // tagged before the scan
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
        var end = text.IndexOf("  # GATE (R1-47): the staged amd64 image", StringComparison.Ordinal);
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

    /// <summary>A job that writes somewhere the release ships to. Uploading code-scanning results (security-events) is a
    /// check, not a publication: the release's own security job (R1-51) holds it for CodeQL.</summary>
    private static bool IsPublisher(YamlMappingNode job)
        => Child(job, "permissions") is { } p && Writes(p).Any(scope => scope != "security-events");

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
        // R1-47: both parts of the suite — the in-process tests and the Docker-backed integration tests.
        foreach (var part in new[] { "--filter \"Category!=RequiresDocker\"", "--filter \"Category=RequiresDocker\"" })
            if (!ciJobs.SelectMany(j => Steps(j.Job)).Any(s => Scalar(Child(s, "run")) is { } run
                    && run.Contains("dotnet test", StringComparison.Ordinal) && run.Contains(part, StringComparison.Ordinal)))
                violations.Add($"ci.yml does not run 'dotnet test {part}'");
        foreach (var (id, job) in ciJobs)
            foreach (var checkout in Steps(job).Where(s => Scalar(Child(s, "uses"))?.StartsWith("actions/checkout@") == true))
                if (Scalar(Child(Child(checkout, "with"), "ref"))?.Contains("inputs.ref") != true)
                    violations.Add($"ci.yml job '{id}' ignores the ref the release passes in");

        // Who builds, scans and publishes.
        var builders = JobsUsing(jobs, "docker/build-push-action@");
        var scanners = JobsUsing(jobs, "aquasecurity/trivy-action@");
        if (builders.Count == 0) violations.Add("no job builds the image");
        if (scanners.Count == 0) violations.Add("no job scans the image");
        var publishers = jobs.Where(j => IsPublisher(j.Value) && !builders.Contains(j.Key))
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
        // The moving tags are decided from the published releases right where they move, in the last job (review rounds
        // 3 and 4): a decision taken earlier is replayed by "Re-run failed jobs" and could move them back past a later
        // release, and a move before the GitHub release exists is not repeated when only that release had to be finished.
        var moveSteps = jobs.SelectMany(j => Steps(j.Value).Select(s => (Job: j.Key, Step: s)))
            .Where(x => Scalar(Child(x.Step, "run"))?.Contains("imagetools create -t \"$t\"") == true).ToList();
        if (moveSteps.Count != 1)
            violations.Add($"expected exactly one step that moves X.Y and latest, found {moveSteps.Count}");
        else
        {
            var (moveJob, moveStep) = moveSteps[0];
            var moveRun = Scalar(Child(moveStep, "run"))!;
            if (!moveRun.Contains("gh release list") || !moveRun.Contains("--exclude-drafts") || !moveRun.Contains("--exclude-pre-releases")
                || moveRun.Contains("git tag -l"))
                violations.Add("`latest` and X.Y are not decided from the published releases in the step that moves them");
            var release = JobsUsing(jobs, "softprops/action-gh-release@").FirstOrDefault() ?? "github-release";
            if (!Needs(jobs[moveJob]).Contains(release))
                violations.Add($"X.Y and latest move in '{moveJob}', which does not wait for the GitHub release ('{release}')");
            // Only the version, its line and the published digest may come from earlier jobs — not a decision. The whole
            // job is searched (job env, every step's env, with and run), so a decision handed in another way is caught too.
            var inputs = Regex.Matches(JobText(jobs[moveJob]), @"needs\.([\w-]+)\.outputs\.([\w-]+)")
                .Select(m => $"{m.Groups[1].Value}.{m.Groups[2].Value}").Distinct();
            foreach (var input in inputs.Except(["prepare.ver", "prepare.minor", "publish-image.digest"]))
                violations.Add($"the move job takes '{input}' from an earlier job, so a re-run replays it");
        }

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

    // ---- R1-47: time limits, the mandatory suites and the artifact gate ----

    /// <summary>Every job that runs steps has a <c>timeout-minutes</c> of at most three hours (the default is six).</summary>
    public static IReadOnlyList<string> TimeLimitViolations(string yaml)
    {
        var violations = new List<string>();
        foreach (var (id, job) in Jobs(Parse(yaml)))
        {
            if (Child(job, "uses") is not null) continue;   // a called workflow limits its own jobs
            var limit = Scalar(Child(job, "timeout-minutes"));
            if (!int.TryParse(limit, out var minutes) || minutes is < 1 or > 180)
                violations.Add($"job '{id}' has no time limit of 1-180 minutes (timeout-minutes: '{limit}')");
        }
        return violations;
    }

    public static IReadOnlyList<string> IntegrationGateViolations(string releaseYaml, string ciYaml, string securityYaml)
    {
        var violations = new List<string>();
        var ci = Jobs(Parse(ciYaml)).ToDictionary(j => j.Id, j => j.Job);
        var release = Jobs(Parse(releaseYaml)).ToDictionary(j => j.Id, j => j.Job);
        var security = Jobs(Parse(securityYaml)).ToDictionary(j => j.Id, j => j.Job);

        foreach (var (file, jobs) in new[] { ("ci.yml", ci), ("release.yml", release), ("security.yml", security) })
        foreach (var (id, job) in jobs)
        foreach (var step in Steps(job))
            if (Scalar(Child(step, "continue-on-error")) is { } c && !c.Equals("false", StringComparison.OrdinalIgnoreCase))
                violations.Add($"{file} job '{id}' lets a step fail without failing the job (continue-on-error)");

        // The suite: in-process tests with a floor, Docker-backed tests with a floor and no skips (TA-50, F-13), each with
        // the manifest of its required tests — a floor alone lets one mandatory test vanish behind the others.
        var ciRuns = ci.Values.SelectMany(Steps).Select(s => Scalar(Child(s, "run"))).OfType<string>().ToList();
        if (!ciRuns.Any(r => r.Contains("dotnet test", StringComparison.Ordinal)
                             && r.Contains("--filter \"Category!=RequiresDocker\"", StringComparison.Ordinal)
                             && Regex.IsMatch(r, @"check-test-results\.sh \S+ [1-9]\d* --require \.github/required-tests/in-process\.txt\n")))
            violations.Add("ci.yml does not check the in-process test results against a floor and their required tests");
        if (!ciRuns.Any(r => r.Contains("dotnet test", StringComparison.Ordinal)
                             && r.Contains("--filter \"Category=RequiresDocker\"", StringComparison.Ordinal)
                             && r.Contains("docker info", StringComparison.Ordinal)
                             && Regex.IsMatch(r, @"check-test-results\.sh \S+ [1-9]\d* --no-skips --require \.github/required-tests/docker\.txt\n")))
            violations.Add("ci.yml does not run the Docker-backed suites as mandatory (Docker probe, floor, no skips, required tests)");

        // The artifact on pull requests and pushes: built, Trivy-gated, hardened and N-1 upgrade checks on that image.
        var ciArtifact = ci.Where(j => JobRuns(j.Value).Contains("artifact-checks.sh hardened", StringComparison.Ordinal)).ToList();
        if (ciArtifact.Count != 1) violations.Add($"ci.yml: expected one job that checks the built image, found {ciArtifact.Count}");
        foreach (var (id, job) in ciArtifact)
        {
            violations.AddRange(ArtifactJobViolations("ci.yml", id, job));
            var build = Steps(job).FirstOrDefault(s => Scalar(Child(s, "uses"))?.StartsWith("docker/build-push-action@") == true);
            if (build is null || Scalar(Child(Child(build, "with"), "load")) != "true"
                || Scalar(Child(Child(build, "with"), "tags")) != "${{ env.ARTIFACT }}")
                violations.Add($"ci.yml job '{id}' does not check the image it built ($ARTIFACT)");
            var scan = Steps(job).FirstOrDefault(s => Scalar(Child(s, "uses"))?.StartsWith("aquasecurity/trivy-action@") == true);
            var scanWith = Child(scan, "with");
            if (scan is null || Child(scan, "if") is not null || Scalar(Child(scanWith, "exit-code")) != "1"
                || Scalar(Child(scanWith, "image-ref")) != "${{ env.ARTIFACT }}"
                || !(Scalar(Child(scanWith, "severity")) ?? "").Contains("CRITICAL", StringComparison.Ordinal))
                violations.Add($"ci.yml job '{id}' does not gate the built image on Trivy (CRITICAL)");
        }

        // The release: the staged digest itself, before anything is published.
        var releaseArtifact = release.Where(j => JobRuns(j.Value).Contains("artifact-checks.sh hardened", StringComparison.Ordinal)).ToList();
        if (releaseArtifact.Count != 1) violations.Add($"release.yml: expected one job that checks the staged image, found {releaseArtifact.Count}");
        foreach (var (id, job) in releaseArtifact)
        {
            violations.AddRange(ArtifactJobViolations("release.yml", id, job));
            var pull = Steps(job).FirstOrDefault(s => Scalar(Child(s, "run"))?.Contains(
                "artifact-checks.sh prepare \"${IMAGE}@${AMD64_IMAGE}\" \"$ARTIFACT\"", StringComparison.Ordinal) == true);
            if (pull is null || Scalar(Child(Child(pull, "env"), "AMD64_IMAGE"))?.Contains("needs.build.outputs.amd64_image") != true)
                violations.Add($"release.yml job '{id}' does not check the staged amd64 digest that is published");
            if (JobRuns(job).Contains("docker build", StringComparison.Ordinal) || JobsUsing(new() { [id] = job }, "docker/build-push-action@").Count > 0)
                violations.Add($"release.yml job '{id}' builds an image instead of checking the staged one");
            var builders = JobsUsing(release, "docker/build-push-action@");
            foreach (var publisher in release.Where(j => IsPublisher(j.Value) && !builders.Contains(j.Key)))
                if (!Ancestors(release, publisher.Key).Contains(id))
                    violations.Add($"release.yml publish job '{publisher.Key}' does not wait for the artifact checks ('{id}')");
        }

        // Dependencies, secrets, static analysis, CodeQL (TA-47, F-09).
        var securityRuns = string.Join("\n", security.Values.SelectMany(Steps).Select(s => Scalar(Child(s, "run"))).OfType<string>());
        if (!securityRuns.Contains("--vulnerable --include-transitive", StringComparison.Ordinal)
            || !Regex.IsMatch(securityRuns, @"if \[ ""\$FOUND"" -gt 0 \]; then\n(?:(?!\s*fi\n)[^\n]*\n)*?[^\n]*;\s*exit 1\n\s*fi\n"))
            violations.Add("security.yml does not fail on a known-vulnerable package");
        var review = security.Values.SelectMany(Steps).FirstOrDefault(s => Scalar(Child(s, "uses"))?.StartsWith("actions/dependency-review-action@") == true);
        if (review is null || Scalar(Child(Child(review, "with"), "fail-on-severity")) != "low")
            violations.Add("security.yml does not refuse a pull request that adds any vulnerable dependency");
        if (!securityRuns.Contains("GITLEAKS_IMAGE", StringComparison.Ordinal) || !securityRuns.Contains("--exit-code 1", StringComparison.Ordinal)
            || !securityRuns.Contains("run_gitleaks git .", StringComparison.Ordinal) || !securityRuns.Contains("run_gitleaks dir .", StringComparison.Ordinal))
            violations.Add("security.yml does not fail on a secret in the history or the tree");
        if (!securityRuns.Contains("-p:AnalysisModeSecurity=All", StringComparison.Ordinal)
            || !securityRuns.Contains("-p:CodeAnalysisTreatWarningsAsErrors=true", StringComparison.Ordinal))
            violations.Add("security.yml does not gate on the security analyzers");
        if (JobsUsing(security, "github/codeql-action/analyze@").Count != 1)
            violations.Add("security.yml does not run CodeQL");
        return violations;
    }

    private static IEnumerable<string> ArtifactJobViolations(string file, string id, YamlMappingNode job)
    {
        var runs = JobRuns(job);
        // Docker runs only inside artifact-checks.sh, which keeps its output private: a registry, daemon or credential-helper
        // error from a docker command in the step itself would reach the Actions log raw.
        // Also through a path or in quotes ("/usr/bin/docker", "'docker'"), and docker-compose.
        if (Regex.IsMatch(runs, @"(?m)(^|[\s;&|(`$/""'])docker(-compose)?[""']?\s+[a-z]"))
            yield return $"{file} job '{id}' runs docker outside artifact-checks.sh; its output would reach the log raw";
        // The log filter and the data tool's failure messages are proven not to disclose planted values.
        if (!Regex.IsMatch(runs, @"(?m)^\s*bash \.github/scripts/artifact-checks\.sh self-test\s*$")) yield return $"{file} job '{id}' does not run the disclosure self-test";
        // ... before any other step of the checks script runs, so a broken output rule is known before the image is touched.
        var checkSteps = Steps(job).Select(s => Scalar(Child(s, "run")) ?? "").Where(r => r.Contains("artifact-checks.sh ", StringComparison.Ordinal)).ToList();
        if (checkSteps.Count > 0 && !Regex.IsMatch(checkSteps[0], @"artifact-checks\.sh self-test\s*$"))
            yield return $"{file} job '{id}' runs the checks script before the disclosure self-test";
        if (!Regex.IsMatch(runs, @"artifact-checks\.sh hardened ""\$ARTIFACT""")) yield return $"{file} job '{id}' does not check the hardened profile of $ARTIFACT";
        if (!Regex.IsMatch(runs, @"artifact-checks\.sh upgrade ""\$ARTIFACT"" ""\$PREVIOUS""")) yield return $"{file} job '{id}' does not upgrade N-1 data with $ARTIFACT";
        // N-1 strictly below the release under test: the same release without pending migrations is rightly no upgrade.
        if (!Regex.IsMatch(runs, @"artifact-checks\.sh previous-release ""\$VER""")) yield return $"{file} job '{id}' does not resolve N-1 as a published release below the version under test";
        foreach (var step in Steps(job).Where(s => Child(s, "if") is not null))
            yield return $"{file} job '{id}' has a conditional step '{Scalar(Child(step, "name"))}'; every artifact check runs";
    }

    private static string JobRuns(YamlMappingNode job)
        => string.Join("\n", Steps(job).Select(s => Scalar(Child(s, "run"))).OfType<string>());

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

    // ---- R1-51: the release gate's wiring (contract 0008) ----

    private const string SecurityWorkflow = "./.github/workflows/security.yml";
    private const string ModeVariable = "WHISKERS_RELEASE_GATE";

    /// <summary>
    /// Contract 0008: only release.yml makes a run a release run (<c>release-gate: release</c>), ci.yml turns that input
    /// into the mode variable for the in-process step and nowhere else, the records are compared with a base on a pull
    /// request and in a release run, the image-runtime run is checked from its TRX file with its manifest, the release
    /// runs security.yml on the tag commit before it publishes, and the tag commit must be on main.
    /// </summary>
    public static IReadOnlyList<string> ReleaseGateWiringViolations(string releaseYaml, string ciYaml, string securityYaml)
    {
        var violations = new List<string>();
        var releaseRoot = Parse(releaseYaml);
        var ciRoot = Parse(ciYaml);
        var securityRoot = Parse(securityYaml);
        var release = Jobs(releaseRoot).ToDictionary(j => j.Id, j => j.Job);
        var ci = Jobs(ciRoot).ToDictionary(j => j.Id, j => j.Job);

        // The mode input: set by the release's test job alone, and to exactly 'release'.
        var callers = release.Where(j => Scalar(Child(j.Value, "uses")) == CiWorkflow).ToList();
        if (callers.Count != 1 || Scalar(Child(Child(callers[0].Value, "with"), "release-gate")) != "release")
            violations.Add("release.yml: the job that calls ci.yml does not pass 'release-gate: release'");
        foreach (var (id, job) in release.Where(j => Scalar(Child(j.Value, "uses")) != CiWorkflow))
            if (Child(Child(job, "with"), "release-gate") is not null)
                violations.Add($"release.yml job '{id}' passes 'release-gate' but does not call ci.yml");
        if (Child(Child(Child(Child(ciRoot, "on"), "workflow_call"), "inputs"), "release-gate") is not YamlMappingNode input
            || Scalar(Child(input, "default")) is not (null or ""))
            violations.Add("ci.yml has no 'release-gate' workflow_call input with an empty default");

        // ci.yml: the variable comes only from that input, only in the in-process step.
        if (Child(Child(ciRoot, "env"), ModeVariable) is not null)
            violations.Add($"ci.yml sets {ModeVariable} for the whole workflow");
        foreach (var (id, job) in ci)
            if (Child(Child(job, "env"), ModeVariable) is not null)
                violations.Add($"ci.yml job '{id}' sets {ModeVariable} for the whole job");
        foreach (var (id, job) in release)
            if (Child(Child(job, "env"), ModeVariable) is not null || Steps(job).Any(s => Child(Child(s, "env"), ModeVariable) is not null))
                violations.Add($"release.yml job '{id}' sets {ModeVariable}: the mode reaches the gate only through ci.yml's input");
        if (Child(Child(releaseRoot, "env"), ModeVariable) is not null)
            violations.Add($"release.yml sets {ModeVariable} for the whole workflow");
        var modeSteps = ci.SelectMany(j => Steps(j.Value).Select(s => (Job: j.Key, Step: s)))
            .Where(x => Child(Child(x.Step, "env"), ModeVariable) is not null).ToList();
        var inProcess = modeSteps.Where(x => Scalar(Child(x.Step, "run"))?.Contains("--filter \"Category!=RequiresDocker\"", StringComparison.Ordinal) == true).ToList();
        if (inProcess.Count != 1 || Scalar(Child(Child(inProcess[0].Step, "env"), ModeVariable)) != "${{ inputs.release-gate }}")
            violations.Add($"ci.yml: the in-process step does not take {ModeVariable} from the 'release-gate' input");
        foreach (var (job, step) in modeSteps.Except(inProcess))
            violations.Add($"ci.yml job '{job}' step '{Scalar(Child(step, "name")) ?? "?"}' sets {ModeVariable}: only the in-process step may");
        var allRuns = string.Join("\n", ci.Values.Concat(release.Values).SelectMany(Steps).Select(s => Scalar(Child(s, "run"))).OfType<string>());
        if (Regex.IsMatch(allRuns, $@"\b{ModeVariable}="))
            violations.Add($"a run script sets {ModeVariable} itself");

        // The base comparison (D16, R18): on a pull request against its base commit, in a release run against the
        // previous release tag; the tags are fetched only in the release run; checked from its TRX file, no skip.
        var baseSteps = ci.SelectMany(j => Steps(j.Value).Select(s => (Job: j.Key, Step: s)))
            .Where(x => Scalar(Child(x.Step, "run"))?.Contains("ReleaseGateBaseTests", StringComparison.Ordinal) == true).ToList();
        if (baseSteps.Count != 1) violations.Add($"ci.yml: expected one step that runs ReleaseGateBaseTests, found {baseSteps.Count}");
        foreach (var (job, step) in baseSteps)
        {
            var condition = Scalar(Child(step, "if")) ?? "";
            if (!condition.Contains("github.event_name == 'pull_request'", StringComparison.Ordinal)
                || !condition.Contains("inputs.release-gate == 'release'", StringComparison.Ordinal)
                || condition.Contains("&&", StringComparison.Ordinal))
                violations.Add($"ci.yml job '{job}': the base comparison does not run on every pull request and every release run");
            var env = Child(step, "env");
            if (Scalar(Child(env, "BASE_SHA")) != "${{ github.event.pull_request.base.sha }}"
                || Scalar(Child(env, "RELEASE_GATE")) != "${{ inputs.release-gate }}")
                violations.Add($"ci.yml job '{job}': the base comparison does not take the pull request's base and the mode from the event");
            var run = Scalar(Child(step, "run")) ?? "";
            var branches = Regex.Match(run, @"if \[ ""\$RELEASE_GATE"" = ""release"" \]; then\n(?<tag>(?:(?!\s*else\n)[^\n]*\n)*?)\s*else\n(?<pr>(?:(?!\s*fi\n)[^\n]*\n)*?)\s*fi\n");
            if (!branches.Success
                || !branches.Groups["tag"].Value.Contains("refs/tags/v*", StringComparison.Ordinal)
                || !branches.Groups["tag"].Value.Contains("WHISKERS_GATE_BASE=previous-tag", StringComparison.Ordinal)
                || !branches.Groups["pr"].Value.Contains("origin \"$BASE_SHA\"", StringComparison.Ordinal)
                || !branches.Groups["pr"].Value.Contains("WHISKERS_GATE_BASE=\"$BASE_SHA\"", StringComparison.Ordinal)
                || branches.Groups["pr"].Value.Contains("refs/tags", StringComparison.Ordinal))
                violations.Add($"ci.yml job '{job}': the base comparison does not fetch the base SHA on a pull request and the tags only in a release run");
            // Review M3: in a release run the base is a published release in the release commit's history, so the step
            // fetches the whole history and lists the published releases (no drafts, no pre-releases) for the test.
            var tagBranch = branches.Success ? branches.Groups["tag"].Value : "";
            if (!tagBranch.Contains("--unshallow", StringComparison.Ordinal)
                || !Regex.IsMatch(tagBranch, @"GH_TOKEN=""\$RELEASES_TOKEN"" gh release list --repo ""\$GITHUB_REPOSITORY"" --limit \d+ --exclude-drafts --exclude-pre-releases --json tagName --jq '\.\[\]\.tagName' > ""\$RUNNER_TEMP/releases\.txt""\n")
                || !tagBranch.Contains("export WHISKERS_GATE_RELEASES=\"$RUNNER_TEMP/releases.txt\"", StringComparison.Ordinal)
                || Scalar(Child(env, "RELEASES_TOKEN")) != "${{ github.token }}")
                violations.Add($"ci.yml job '{job}': the release run's base comparison does not fetch the history and the published releases");
            // Review rounds 2-4: the token reaches gh only and is not in the tests' environment. Only RELEASES_TOKEN may carry
            // a token (no other env value, no run text names github.token or a secret).
            static bool Credential(string? text) => text is not null
                && (text.Contains("github.token", StringComparison.Ordinal) || text.Contains("secrets.", StringComparison.Ordinal));
            var otherCredential = (env as YamlMappingNode)?.Children
                .Any(kv => Scalar(kv.Key) != "RELEASES_TOKEN" && Credential(Scalar(kv.Value))) == true;
            if (Child(env, "GH_TOKEN") is not null || Child(env, "GITHUB_TOKEN") is not null || otherCredential || Credential(run)
                || Regex.Matches(run, @"\$RELEASES_TOKEN").Count != 1 || !Regex.IsMatch(run, @"\nfi\nunset RELEASES_TOKEN\n"))
                violations.Add($"ci.yml job '{job}': the base comparison exposes the token beyond 'gh release list' (unset it before the tests)");
            // Review m3: the step marks itself, so a base lost on the way fails instead of passing without a comparison.
            if (!Regex.IsMatch(run, @"\nexport WHISKERS_GATE_BASE_REQUIRED=true\ndotnet test "))
                violations.Add($"ci.yml job '{job}': the base comparison does not require a base (WHISKERS_GATE_BASE_REQUIRED=true)");
            if (!Regex.IsMatch(run, @"check-test-results\.sh \S+ [1-9]\d* --no-skips\n"))
                violations.Add($"ci.yml job '{job}': the base comparison is not checked from its TRX file without skips");
            if (!ci.TryGetValue(job, out var j) || !Steps(j).Any(s => Scalar(Child(s, "run"))?.Contains("--filter \"Category!=RequiresDocker\"", StringComparison.Ordinal) == true))
                violations.Add($"ci.yml job '{job}': the base comparison does not run in the job that builds and tests");
        }

        // Review round 3 M-1: the N-1 check lists the published releases with gh, which needs GH_TOKEN in its step; a
        // rename for the base step once took it away, and the artifact job then failed on every run.
        foreach (var (file, jobs) in new[] { ("ci.yml", ci), ("release.yml", release) })
        {
            var n1 = jobs.SelectMany(j => Steps(j.Value).Select(s => (Job: j.Key, Step: s)))
                .Where(x => Scalar(Child(x.Step, "run"))?.Contains("artifact-checks.sh previous-release", StringComparison.Ordinal) == true).ToList();
            if (n1.Count == 0) violations.Add($"{file}: no step runs 'artifact-checks.sh previous-release'");
            foreach (var (job, step) in n1)
                if (Scalar(Child(Child(step, "env"), "GH_TOKEN")) != "${{ github.token }}")
                    violations.Add($"{file} job '{job}' step '{Scalar(Child(step, "name")) ?? "?"}' runs 'artifact-checks.sh previous-release' without GH_TOKEN: ${{{{ github.token }}}}");
        }

        // The image-runtime run (D14): from its TRX file, a floor, no skip, its manifest.
        var imageRuns = ci.Values.SelectMany(Steps).Select(s => Scalar(Child(s, "run"))).OfType<string>()
            .Where(r => r.Contains("--filter \"Category=ImageRuntime\"", StringComparison.Ordinal)).ToList();
        if (imageRuns.Count != 1 || !imageRuns[0].Contains("--logger \"trx;LogFileName=image-runtime.trx\"", StringComparison.Ordinal)
            || !Regex.IsMatch(imageRuns[0], @"check-test-results\.sh TestResults/image-runtime\.trx [1-9]\d* --no-skips --require \.github/required-tests/image-runtime\.txt\n"))
            violations.Add("ci.yml does not check the image-runtime tests from their TRX file against a floor, without skips and with image-runtime.txt");

        // security.yml in the release (D8): called on the tag commit, unconditionally, and every publication waits for it.
        if (Child(Child(securityRoot, "on"), "workflow_call") is null) violations.Add("security.yml cannot be called by the release (no workflow_call)");
        foreach (var (id, job) in Jobs(securityRoot))
            foreach (var checkout in Steps(job).Where(s => Scalar(Child(s, "uses"))?.StartsWith("actions/checkout@") == true))
                if (Scalar(Child(Child(checkout, "with"), "ref")) != "${{ inputs.ref }}")
                    violations.Add($"security.yml job '{id}' ignores the ref the release passes in");
        var securityCallers = release.Where(j => Scalar(Child(j.Value, "uses")) == SecurityWorkflow).ToList();
        if (securityCallers.Count != 1) violations.Add($"release.yml: expected one job that calls security.yml, found {securityCallers.Count}");
        foreach (var (id, job) in securityCallers)
        {
            if (Scalar(Child(Child(job, "with"), "ref"))?.Contains(PrepareSha) != true)
                violations.Add($"release.yml job '{id}' does not check the tag commit ({PrepareSha})");
            if (Child(job, "if") is not null) violations.Add($"release.yml job '{id}' runs only under a condition");
            var builders = JobsUsing(release, "docker/build-push-action@");
            foreach (var publisher in release.Where(p => IsPublisher(p.Value) && !builders.Contains(p.Key)).Select(p => p.Key))
                if (!Ancestors(release, publisher).Contains(id))
                    violations.Add($"release.yml publish job '{publisher}' does not wait for the security checks ('{id}')");
        }

        // The tag commit is on main (D16): otherwise the pull request's base comparison never saw it.
        if (release.TryGetValue("prepare", out var prepare))
        {
            var prepareRuns = string.Join("\n", Steps(prepare).Select(s => Scalar(Child(s, "run"))).OfType<string>());
            if (!Regex.IsMatch(prepareRuns, @"if ! git merge-base --is-ancestor ""\$SHA"" refs/remotes/origin/main; then\n[^\n]*exit 1\n"))
                violations.Add("the prepare job does not refuse a tag commit that is not on origin/main");
            var tagCheckout = Steps(prepare).FirstOrDefault(s => Scalar(Child(s, "uses"))?.StartsWith("actions/checkout@") == true);
            if (Scalar(Child(Child(tagCheckout, "with"), "fetch-depth")) != "0")
                violations.Add("the prepare job's checkout does not fetch origin/main (fetch-depth: 0)");
        }
        else violations.Add("the release has no 'prepare' job");
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

    /// <summary>Every scalar in a job, keys and values, joined: what a text search over the job should see.</summary>
    private static string JobText(YamlNode node) => node switch
    {
        YamlScalarNode s => s.Value ?? "",
        YamlMappingNode m => string.Join("\n", m.Children.SelectMany(c => new[] { JobText(c.Key), JobText(c.Value) })),
        YamlSequenceNode q => string.Join("\n", q.Children.Select(JobText)),
        _ => "",
    };

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
