using System.Diagnostics;
using System.Text.RegularExpressions;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;
using Whiskers.Services.Terminal;

namespace Whiskers.Tests;

/// <summary>
/// R1-52 (F-19): the image carries the runtime tools Whiskers starts as processes, and not the Docker engine.
///
/// <para>The source checks pin the list: every place that starts a process is known, and every program it starts is
/// either shipped in the image or named here as provided from outside, with the reason. A new process start or a new
/// program fails until this list (and the Dockerfile) say how the image provides it.</para>
///
/// <para>The image checks (category <c>ImageRuntime</c>) run against a built image named by <c>WHISKERS_IMAGE</c>:
/// every shipped tool is present and starts, nothing of the engine (dockerd, containerd, runc) is, and the local
/// terminal paths work through the image's own binaries — <c>docker exec</c> through the docker CLI against the
/// mounted socket, and the host shell through <c>nsenter</c> in the privileged full profile; a compose deployment
/// through the production deploy script and local host transport runs its workload on the Docker host and removes it;
/// the VPN runtime (netfilter, TUN, tailscaled's router) initializes without credentials; every pinned base digest
/// covers amd64 and arm64; the image boots. Without <c>WHISKERS_IMAGE</c> (or a host without <c>docker compose</c>,
/// such as Docker Desktop's VM) they are skipped; CI's <c>image-runtime</c> job builds the image on x64 and arm64 and
/// sets <c>WHISKERS_IMAGE_TESTS_REQUIRED=true</c>, which turns anything missing into a failure.</para>
/// </summary>
public class ImageRuntimeToolsTests
{
    /// <summary>Programs the image ships, and what Whiskers starts them for.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Shipped = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["docker"] = "local web terminal: `docker exec` into a container (static CLI from the docker-cli stage)",
        ["nsenter"] = "local host shell and host commands in the full profile (util-linux, base image)",
        ["ssh"] = "SSH terminal, SSH host commands, SSH Docker tunnel",
        ["sshpass"] = "SSH host commands with a password",
        ["tailscale"] = "in-app Tailscale provider and the legacy entrypoint bring-up",
        ["tailscaled"] = "in-app Tailscale provider and the legacy entrypoint bring-up",
        ["pg_dump"] = "PostgreSQL self-backup (WHISKERS_PG_BIN_DIR)",
        ["pg_restore"] = "PostgreSQL self-restore (WHISKERS_PG_BIN_DIR)",
        ["psql"] = "PostgreSQL self-restore (WHISKERS_PG_BIN_DIR)",
    };

    /// <summary>Programs Whiskers can start that the image deliberately does not ship.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ProvidedOutside = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["netbird"] = "optional NetBird provider: reports 'not installed' without the binary; a derived image or the host provides it",
        ["claude"] = "optional Claude Code agent runtime: installed by the operator who chooses it",
    };

    /// <summary>Tools the image needs beyond what the app starts: the HEALTHCHECK probe and tailscaled's netfilter.</summary>
    private static readonly string[] SupportTools = ["curl", "iptables"];

    /// <summary>Parts of the Docker engine that must not be in the image (the docker.io package brought them).</summary>
    private static readonly string[] EngineParts = ["dockerd", "containerd", "containerd-shim-runc-v2", "runc", "docker-proxy", "docker-init"];

    /// <summary>Every file that starts a process, and how the programs it starts are named in the source.</summary>
    private static readonly (string Sink, string[] Sources, Regex Program)[] ProcessSinks =
    [
        ("Services/Server/HostCommandExecutor.cs", ["Services/Server/HostCommandExecutor.cs"], new(@"RunProcessAsync\(\s*""([^""]+)""")),
        ("Services/Terminal/TerminalSession.cs", ["Services/Terminal/TerminalSession.cs"], new(@"\bnew\(\s*""([^""]+)""\s*,")),
        ("Services/Docker/SshTunnelManager.cs", ["Services/Docker/SshTunnelManager.cs"], new(@"FileName\s*=\s*""([^""]+)""")),
        ("Services/Agent/ClaudeCodeRuntime.cs", ["Services/Agent/ClaudeCodeRuntime.cs"], new(@"FileName\s*=\s*""([^""]+)""")),
        ("Services/Backup/PostgresDumpTool.cs", ["Services/Backup/PostgresDumpTool.cs"], new(@"\b(?:RunAsync|Start)\(\s*""([^""]+)""")),
        // The VPN runner takes the program as a parameter; the providers name it.
        ("Services/Vpn/VpnProcessRunner.cs", ["Services/Vpn/Providers/TailscaleVpnProvider.cs", "Services/Vpn/Providers/NetbirdVpnProvider.cs"],
            new(@"_runner\.(?:RunAsync|StartDetached)\(\s*""([^""]+)""")),
    ];

    private static readonly Regex ProcessStart = new(@"new\s+ProcessStartInfo\b|Process\.Start\(|new\s+Process\b", RegexOptions.CultureInvariant);

    // ---- source checks ----------------------------------------------------------------------------------------

    [Fact]
    public void Every_file_that_starts_a_process_is_a_known_sink()
    {
        var src = DeploymentScripts.RepoFile("src", "Whiskers");
        var starting = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".razor", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => ProcessStart.IsMatch(File.ReadAllText(p)))
            .Select(p => Path.GetRelativePath(src, p).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(starting);
        Assert.Equal(ProcessSinks.Select(s => s.Sink).OrderBy(s => s, StringComparer.Ordinal), starting.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_program_the_app_starts_is_shipped_or_named_as_provided_outside()
    {
        var programs = ProgramsStarted();
        Assert.All(ProcessSinks, s => Assert.True(programs.ContainsKey(s.Sink) && programs[s.Sink].Count > 0,
            $"no program found for {s.Sink}: the pattern no longer matches the source"));
        var all = programs.Values.SelectMany(p => p).ToHashSet(StringComparer.Ordinal);
        Assert.All(all, p => Assert.True(Shipped.ContainsKey(p) || ProvidedOutside.ContainsKey(p),
            $"Whiskers starts '{p}', which is neither shipped in the image nor named as provided outside"));
        // And nothing on the list is stale: each entry is still started somewhere.
        Assert.All(Shipped.Keys.Concat(ProvidedOutside.Keys), p => Assert.Contains(p, all));
    }

    [Fact]
    public void Dockerfile_ships_the_docker_cli_and_no_engine()
    {
        var dockerfile = DeploymentScripts.ReadRepoFile("Dockerfile");
        Assert.Empty(EngineViolations(dockerfile));
    }

    [Theory]
    [InlineData("    && apt-get install -y --no-install-recommends \\\n        openssh-client \\\n",
                "    && apt-get install -y --no-install-recommends \\\n        docker.io \\\n        openssh-client \\\n")]
    [InlineData("COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker\n", "")]
    [InlineData("        iptables \\\n", "")]
    public void Engine_check_finds_an_engine_or_a_missing_tool(string original, string mutation)
    {
        var real = DeploymentScripts.ReadRepoFile("Dockerfile");
        Assert.Contains(original, real);
        Assert.NotEmpty(EngineViolations(real.Replace(original, mutation)));
    }

    [Fact]
    public void Ci_runs_the_image_tests_on_both_architectures_and_requires_them()
        => Assert.Empty(CiImageLaneViolations(DeploymentScripts.ReadRepoFile(".github", "workflows", "ci.yml")));

    [Theory]
    [InlineData("        runner: [ubuntu-latest, ubuntu-24.04-arm]", "        runner: [ubuntu-latest]")]
    [InlineData("          WHISKERS_IMAGE_TESTS_REQUIRED: \"true\"", "          WHISKERS_IMAGE_TESTS_REQUIRED: \"false\"")]
    [InlineData("--filter \"Category=ImageRuntime\"", "--filter \"Category=Nothing\"")]
    public void Image_lane_check_finds_a_missing_architecture_or_an_optional_run(string original, string mutation)
    {
        var real = DeploymentScripts.ReadRepoFile(".github", "workflows", "ci.yml");
        Assert.Contains(original, real);
        Assert.NotEmpty(CiImageLaneViolations(real.Replace(original, mutation)));
    }

    // ---- image checks -------------------------------------------------------------------------------------------

    [SkippableFact]
    [Trait("Category", "ImageRuntime")]
    public void Image_ships_every_runtime_tool_and_no_engine()
    {
        var image = ImageUnderTest();
        var tools = string.Join(' ', Shipped.Keys.Concat(SupportTools));
        var engine = string.Join(' ', EngineParts);
        var script =
            "export PATH=\"$WHISKERS_PG_BIN_DIR:$PATH\"; " +
            $"for t in {tools}; do command -v \"$t\" >/dev/null 2>&1 || echo \"missing:$t\"; done; " +
            $"for t in {engine}; do command -v \"$t\" >/dev/null 2>&1 && echo \"engine:$t\"; done; " +
            "dpkg-query -W -f='${Status}\\n' docker.io containerd runc 2>/dev/null | grep -q 'install ok installed' && echo 'engine:package'; " +
            // Each shipped tool starts (its libraries are there): a removed dependency fails here, not in production.
            "set -e; docker --version; nsenter --version; ssh -V; sshpass -V >/dev/null; tailscale version; tailscaled --version; " +
            "pg_dump --version; pg_restore --version; psql --version; curl --version >/dev/null; iptables --version; echo tools-ok";
        var (exit, stdout, stderr) = Docker(["run", "--rm", "--entrypoint", "sh", image, "-c", script]);
        var output = stdout + stderr;
        Assert.DoesNotContain("missing:", output);
        Assert.DoesNotContain("engine:", output);
        Assert.True(exit == 0 && stdout.Contains("tools-ok", StringComparison.Ordinal), $"a runtime tool did not start (exit {exit}):\n{output}");
    }

    [SkippableFact]
    [Trait("Category", "ImageRuntime")]
    public void Local_container_terminal_runs_docker_exec_through_the_image()
    {
        var image = ImageUnderTest();
        var target = $"whiskers-r152-target-{Guid.NewGuid():N}";
        try
        {
            var (startExit, _, startErr) = Docker(["run", "-d", "--name", target, "--entrypoint", "sleep", image, "300"]);
            Assert.True(startExit == 0, $"target container did not start: {startErr}");

            // Exactly the launch the terminal uses for a local container (TerminalLaunch.Local), run by the image's CLI.
            var launch = TerminalLaunch.Local("/bin/sh", target);
            var (exit, stdout, stderr) = Docker(
                ["run", "--rm", "-i", "-v", "/var/run/docker.sock:/var/run/docker.sock", "--entrypoint", launch.FileName, image, .. launch.Arguments],
                stdin: "echo whiskers-terminal-$((6*7))\nexit\n");
            Assert.True(stdout.Contains("whiskers-terminal-42", StringComparison.Ordinal),
                $"docker exec through the image did not run the shell (exit {exit}):\n{stdout}\n{stderr}");
        }
        finally
        {
            Docker(["rm", "-f", target]);
        }
    }

    [SkippableFact]
    [Trait("Category", "ImageRuntime")]
    public void Full_profile_host_shell_runs_nsenter_through_the_image()
    {
        var image = ImageUnderTest();
        // The host shell of the full profile (privileged, pid: host — docker-compose.yml): nsenter into PID 1.
        var launch = TerminalLaunch.Local("sh", null);
        var (exit, stdout, stderr) = Docker(
            ["run", "--rm", "-i", "--privileged", "--pid=host", "--entrypoint", launch.FileName, image, .. launch.Arguments],
            stdin: "echo whiskers-host-$((6*7))\nexit\n");
        Assert.True(stdout.Contains("whiskers-host-42", StringComparison.Ordinal),
            $"the host shell through nsenter did not run (exit {exit}):\n{stdout}\n{stderr}");
    }

    /// <summary>A compose deployment the way Whiskers runs one on its own host: the production deploy script
    /// (<see cref="ComposePlan.WriteScript"/> and <see cref="ComposePlan.UpCommand"/>, as <see cref="ComposeRunner"/> runs them for <c>deploy_compose</c>, the UI and webhooks) through the
    /// production local transport (<see cref="HostCommandExecutor.LocalShellArguments"/>: <c>nsenter</c> into PID 1),
    /// started from the built image in the full profile. The workload must run on the host, and the same path
    /// removes it again.</summary>
    [SkippableFact]
    [Trait("Category", "ImageRuntime")]
    public void Compose_deployment_runs_on_the_host_through_the_image()
    {
        var image = ImageUnderTest();
        var project = $"whiskers-r152-deploy-{Guid.NewGuid():N}"[..34];
        var dir = $"/tmp/{project}";
        var file = $"{dir}/compose.yaml";
        // The workload is the image under test itself (already on the host's daemon: no pull, no registry).
        var yaml = $"services:\n  probe:\n    image: {image}\n    pull_policy: never\n    entrypoint: [\"sleep\", \"600\"]\n";
        var plan = new ComposePlan("local", project, dir, [file], [new ComposeFileInput(file, yaml)],
            EnvFileContent: null, HasEnvFile: false, RemoveOrphans: false, Services: [], Impact: []);

        var (probeExit, probeOut, probeErr) = OnHost(image, "docker compose version");
        if (probeExit != 0)
        {
            var reason = $"the host behind this Docker daemon has no `docker compose` in PID 1's namespaces ({(probeOut + probeErr).Trim()})";
            if (Required) throw new InvalidOperationException(reason);
            Skip.If(true, reason);
        }

        try
        {
            // The commands of ComposeRunner's steps 1 and 5 (contract 0006): write the files, then up without pull or build.
            var backup = $"/tmp/{project}-backup";
            var (exit, stdout, stderr) = OnHost(image,
                $"{plan.WriteScript(backup)} && {plan.UpCommand} && {ComposePlan.CleanupScript(backup)}");
            Assert.True(exit == 0, $"the deploy script failed (exit {exit}):\n{stdout}\n{stderr}");
            Assert.Equal("running", WorkloadStates(project).Single());

            var (downExit, downOut, downErr) = OnHost(image, plan.Command("down --remove-orphans --timeout 1") + $" && rm -rf {dir}");
            Assert.True(downExit == 0, $"the cleanup failed (exit {downExit}):\n{downOut}\n{downErr}");
            Assert.Empty(WorkloadStates(project));
            Assert.Equal(0, OnHost(image, $"test ! -e {dir}").Exit);
        }
        finally
        {
            var (_, ids, _) = Docker(["ps", "-aq", "--filter", $"label=com.docker.compose.project={project}"]);
            foreach (var id in ids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Docker(["rm", "-f", id]);
            Docker(["network", "rm", $"{project}_default"]);
            OnHost(image, $"rm -rf {dir}");
        }
    }

    /// <summary>The VPN runtime of the full profile in a throwaway container — no host network, no credentials:
    /// the image's <c>iptables</c> changes netfilter, and <c>tailscaled</c> creates its TUN device, sets up its
    /// router in iptables mode and waits for a login.</summary>
    [SkippableFact]
    [Trait("Category", "ImageRuntime")]
    public void Vpn_runtime_initializes_netfilter_and_the_tun_device()
    {
        var image = ImageUnderTest();
        var script =
            "set -e; " +
            "iptables -N WHISKERS_SMOKE; iptables -A WHISKERS_SMOKE -j RETURN; iptables -S WHISKERS_SMOKE | grep -q -- '-j RETURN'; " +
            "iptables -F WHISKERS_SMOKE; iptables -X WHISKERS_SMOKE; echo netfilter-ok; " +
            "mkdir -p /tmp/ts; " +
            "tailscaled --state=mem: --socket=/tmp/ts/s.sock --statedir=/tmp/ts --no-logs-no-support > /tmp/ts/log 2>&1 & " +
            "st=; for i in $(seq 1 30); do st=$(tailscale --socket=/tmp/ts/s.sock status --json 2>/dev/null | grep -o '\"BackendState\": *\"[A-Za-z]*\"' || true); " +
            "[ -n \"$st\" ] && break; sleep 1; done; echo \"state:$st\"; " +
            "[ -e /sys/class/net/tailscale0 ] && echo tun-ok; " +
            "grep -o 'netfilter running in [a-z]* mode' /tmp/ts/log || true; " +
            "echo '--- tailscaled log'; cat /tmp/ts/log";
        var (exit, stdout, stderr) = Docker(["run", "--rm", "--cap-add", "NET_ADMIN", "--device", "/dev/net/tun", "--entrypoint", "sh", image, "-c", script]);
        var output = stdout + stderr;
        Assert.True(exit == 0, $"the VPN smoke failed (exit {exit}):\n{output}");
        Assert.Contains("netfilter-ok", stdout);
        Assert.Contains("\"BackendState\": \"NeedsLogin\"", stdout);
        Assert.Contains("tun-ok", stdout);
        Assert.Contains("netfilter running in iptables mode", stdout);
    }

    /// <summary>Every pinned base image is a multi-arch index with linux/amd64 and linux/arm64, so both release
    /// architectures build from the pin (a per-architecture digest would give the arm64 build an amd64 image).</summary>
    [SkippableFact]
    [Trait("Category", "ImageRuntime")]
    public void Every_pinned_base_image_covers_amd64_and_arm64()
    {
        RequireDocker();
        var images = BuildPolicy.BaseImages(DeploymentScripts.ReadRepoFile("Dockerfile"));
        Assert.NotEmpty(images);
        var problems = new List<string>();
        foreach (var image in images)
        {
            var (exit, stdout, stderr) = Docker(["buildx", "imagetools", "inspect", "--raw", image]);
            if (exit != 0) { problems.Add($"{image}: {stderr.Trim()}"); continue; }
            using var json = System.Text.Json.JsonDocument.Parse(stdout);
            if (!json.RootElement.TryGetProperty("manifests", out var manifests))
            {
                problems.Add($"{image}: the digest is a single-platform manifest, not a multi-arch index");
                continue;
            }
            var platforms = manifests.EnumerateArray()
                .Where(m => m.TryGetProperty("platform", out _))
                .Select(m => $"{m.GetProperty("platform").GetProperty("os").GetString()}/{m.GetProperty("platform").GetProperty("architecture").GetString()}")
                .ToHashSet(StringComparer.Ordinal);
            foreach (var needed in new[] { "linux/amd64", "linux/arm64" })
                if (!platforms.Contains(needed)) problems.Add($"{image}: no {needed} in the index");
        }
        Assert.Empty(problems);
    }

    [SkippableFact]
    [Trait("Category", "ImageRuntime")]
    public void Image_boots_and_answers_its_health_probe()
    {
        var image = ImageUnderTest();
        var name = $"whiskers-r152-boot-{Guid.NewGuid():N}";
        try
        {
            // VPN_PROVIDER=none: no in-container VPN bring-up in a test container without /dev/net/tun.
            var (startExit, _, startErr) = Docker(["run", "-d", "--name", name, "-e", "VPN_PROVIDER=none", image]);
            Assert.True(startExit == 0, $"the image did not start: {startErr}");
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                // The image's own HEALTHCHECK command.
                var (exit, stdout, _) = Docker(["exec", name, "curl", "-fsS", "http://localhost:8080/healthz"]);
                if (exit == 0 && stdout.Length > 0) return;
                var (_, running, _) = Docker(["inspect", "-f", "{{.State.Running}}", name]);
                if (running.Trim() != "true") break;
                Thread.Sleep(2000);
            }
            var (_, logs, logErr) = Docker(["logs", "--tail", "60", name]);
            Assert.Fail($"/healthz never answered:\n{logs}\n{logErr}");
        }
        finally
        {
            Docker(["rm", "-f", name]);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    internal static IReadOnlyList<string> CiImageLaneViolations(string ciYaml)
    {
        var violations = new List<string>();
        // The job's own text: from its key to the next job key (two-space indent) or the end.
        var start = Regex.Match(ciYaml, @"^  image-runtime:\s*$", RegexOptions.Multiline);
        if (!start.Success) return ["ci.yml has no image-runtime job"];
        var rest = ciYaml[(start.Index + start.Length)..];
        var next = Regex.Match(rest, @"^  [A-Za-z0-9_-]+:\s*$", RegexOptions.Multiline);
        var job = next.Success ? rest[..next.Index] : rest;

        var runners = Regex.Match(job, @"^\s+runner:\s*\[(?<list>[^\]]*)\]", RegexOptions.Multiline).Groups["list"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!runners.Any(r => r is "ubuntu-latest" or "ubuntu-24.04")) violations.Add("the image-runtime matrix has no x64 runner");
        if (!runners.Any(r => r.EndsWith("-arm", StringComparison.Ordinal))) violations.Add("the image-runtime matrix has no arm64 runner");
        if (!job.Contains("runs-on: ${{ matrix.runner }}", StringComparison.Ordinal)) violations.Add("the image-runtime job does not run on its matrix");
        if (!Regex.IsMatch(job, @"WHISKERS_IMAGE_TESTS_REQUIRED:\s*""true""")) violations.Add("the image tests are not required in CI");
        if (!job.Contains("--filter \"Category=ImageRuntime\"", StringComparison.Ordinal)) violations.Add("the image-runtime job does not run the ImageRuntime tests");
        if (!job.Contains("docker build", StringComparison.Ordinal)) violations.Add("the image-runtime job does not build the image");
        return violations;
    }

    internal static IReadOnlyList<string> EngineViolations(string dockerfile)
    {
        var violations = new List<string>();
        var text = dockerfile.Replace("\r\n", "\n");
        var code = string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
        foreach (var package in new[] { "docker.io", "docker-ce", "containerd", "runc", "docker-ce-rootless-extras" })
            if (Regex.IsMatch(code, $@"(^|\s){Regex.Escape(package)}(\s|$|\\)", RegexOptions.Multiline))
                violations.Add($"the Dockerfile installs '{package}' (part of the Docker engine)");
        if (!Regex.IsMatch(code, @"^COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker$", RegexOptions.Multiline))
            violations.Add("the Dockerfile does not copy the docker CLI from the docker-cli stage");
        if (!Regex.IsMatch(code, @"^FROM docker:[0-9.]+-cli@sha256:[0-9a-f]{64} AS docker-cli$", RegexOptions.Multiline))
            violations.Add("the docker-cli stage is not a digest-pinned docker:<version>-cli image");
        foreach (var package in new[] { "openssh-client", "sshpass", "iptables", "curl", "tailscale", "postgresql-client-18" })
            if (!Regex.IsMatch(code, $@"(^|\s){Regex.Escape(package)}(\s|$|\\)", RegexOptions.Multiline))
                violations.Add($"the Dockerfile no longer installs '{package}'");
        return violations;
    }

    private static Dictionary<string, List<string>> ProgramsStarted()
    {
        var src = DeploymentScripts.RepoFile("src", "Whiskers");
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (sink, sources, program) in ProcessSinks)
        {
            var found = new List<string>();
            foreach (var source in sources)
                foreach (Match m in program.Matches(File.ReadAllText(Path.Combine(src, source))))
                    found.Add(m.Groups[1].Value);
            result[sink] = found;
        }
        return result;
    }

    /// <summary><c>WHISKERS_IMAGE_TESTS_REQUIRED=true</c> (CI's image-runtime job): nothing in this category may skip.</summary>
    private static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable("WHISKERS_IMAGE_TESTS_REQUIRED"), "true", StringComparison.OrdinalIgnoreCase);

    private static void RequireDocker()
    {
        var (exit, _, err) = DeploymentScripts.FindOnPath("docker") is null ? (-1, "", "docker not on PATH") : Docker(["version", "--format", "{{.Server.Version}}"]);
        if (exit == 0) return;
        if (Required) throw new InvalidOperationException($"no Docker daemon, but the image runtime tests are required here: {err}");
        Skip.If(true, $"Docker is not available ({err.Trim()})");
    }

    /// <summary>A host command exactly as the full profile runs one: from the image, privileged with the host's PID
    /// namespace, <c>nsenter</c> with <see cref="HostCommandExecutor.LocalShellArguments"/>.</summary>
    private static (int Exit, string Stdout, string Stderr) OnHost(string image, string command) =>
        Docker(["run", "--rm", "--privileged", "--pid=host", "--entrypoint", "nsenter", image, .. HostCommandExecutor.LocalShellArguments(command)]);

    /// <summary>The states of the containers of a compose project on the daemon's host.</summary>
    private static IReadOnlyList<string> WorkloadStates(string project)
    {
        var (_, stdout, _) = Docker(["ps", "-a", "--filter", $"label=com.docker.compose.project={project}", "--format", "{{.State}}"]);
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ImageUnderTest()
    {
        var required = Required;
        var image = Environment.GetEnvironmentVariable("WHISKERS_IMAGE");
        if (string.IsNullOrWhiteSpace(image))
        {
            if (required) throw new InvalidOperationException("WHISKERS_IMAGE is not set, but the image runtime tests are required here.");
            Skip.If(true, "WHISKERS_IMAGE is not set (build the image and name it to run the image runtime tests)");
        }
        var docker = DeploymentScripts.FindOnPath("docker");
        var (exit, _, err) = docker is null ? (-1, "", "docker not on PATH") : Docker(["image", "inspect", "--format", "{{.Id}}", image!]);
        if (exit != 0)
        {
            if (required) throw new InvalidOperationException($"the image {image} is not available: {err}");
            Skip.If(true, $"Docker or the image {image} is not available ({err.Trim()})");
        }
        return image!;
    }

    private static (int Exit, string Stdout, string Stderr) Docker(IReadOnlyList<string> args, string? stdin = null, int timeoutMs = 180_000)
    {
        var psi = new ProcessStartInfo(DeploymentScripts.FindOnPath("docker") ?? "docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (stdin is not null) process.StandardInput.Write(stdin.Replace("\r\n", "\n"));
        process.StandardInput.Close();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"docker {string.Join(' ', args)} did not finish within {timeoutMs} ms");
        }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
