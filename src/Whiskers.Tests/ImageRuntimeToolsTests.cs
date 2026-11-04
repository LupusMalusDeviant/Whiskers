using System.Diagnostics;
using System.Text.RegularExpressions;
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
/// mounted socket, and the host shell through <c>nsenter</c> in the privileged full profile. Without
/// <c>WHISKERS_IMAGE</c> they are skipped; CI's <c>image-runtime</c> job builds the image and sets
/// <c>WHISKERS_IMAGE_TESTS_REQUIRED=true</c>, which turns a missing image or daemon into a failure.</para>
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

    private static string ImageUnderTest()
    {
        var required = string.Equals(Environment.GetEnvironmentVariable("WHISKERS_IMAGE_TESTS_REQUIRED"), "true", StringComparison.OrdinalIgnoreCase);
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
