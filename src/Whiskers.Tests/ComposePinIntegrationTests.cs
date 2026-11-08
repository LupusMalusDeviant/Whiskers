using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0013, D10–D13 on a real daemon: a project with a pulled and a built service goes through the planner and
/// <see cref="ComposeRunner"/> (write, pull, build, inspect, pin, <c>up --pull never --no-build</c> with the pin file,
/// re-check): every container runs exactly the inspected id. With both tags moved between the pin file and <c>up</c>,
/// nothing ever runs another image — on the classic store both start as inspected, on the containerd store <c>up</c>
/// fails closed because a moved tag takes the repo digest reference along. Commands run locally through
/// <c>bash -c</c>, as the host executor runs them on a server (or, on a Windows machine, inside the container named by
/// <c>WHISKERS_TEST_SHELL_CONTAINER</c>).
/// </summary>
[Trait("Category", DockerRequirement.Category)]
public sealed class ComposePinIntegrationTests
{
    private const string PulledImage = "busybox:1.36";

    /// <summary>The <c>bash</c> on PATH. On Windows the process API looks in System32 first, where <c>bash.exe</c> is
    /// WSL's — a developer machine runs the test through Git's bash on PATH instead.</summary>
    private static readonly string BashPath = OperatingSystem.IsWindows()
        ? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
              .Select(d => Path.Combine(d, "bash.exe"))
              .FirstOrDefault(p => File.Exists(p)
                                   && !p.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase)
                                   && !p.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) ?? "bash"
        : "bash";

    /// <summary>A running Linux container with the Docker CLI and the daemon's socket (e.g. <c>docker:29-cli</c>) to run
    /// the commands in instead — for a Windows developer machine, whose own shell cannot give a directory mode 700.
    /// Unset on CI.</summary>
    private static readonly string? ShellContainer = Environment.GetEnvironmentVariable("WHISKERS_TEST_SHELL_CONTAINER");

    private static (int Exit, string Out, string Err) Bash(string command, int timeoutMs = 300_000)
    {
        var psi = new ProcessStartInfo(string.IsNullOrEmpty(ShellContainer) ? BashPath : "docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (!string.IsNullOrEmpty(ShellContainer))
            foreach (var a in new[] { "exec", "-i", ShellContainer, "sh" }) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, stdout.Result, "timed out");
        }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    private static bool DockerAvailable()
    {
        try { return Bash("docker info >/dev/null 2>&1 && docker compose version >/dev/null 2>&1", 60_000).Exit == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>The host executor: <c>bash -c</c> on this machine.</summary>
    private sealed class LocalHost : IHostCommandExecutor
    {
        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            var (exit, output, error) = Bash(command, (int)(timeout ?? TimeSpan.FromMinutes(5)).TotalMilliseconds);
            return Task.FromResult(new CommandResult { ExitCode = exit, Output = output, Error = error });
        }
    }

    private static string Id(string image) => Bash($"docker image inspect --format '{{{{.Id}}}}' {image}").Out.Trim();

    /// <summary>The containerd image store (Docker 29's default for a fresh install) resolves <c>repo@sha256:…</c> only
    /// through an image record of that repository that still points to the digest — a moved tag takes it away. The
    /// classic store keeps the digest reference.</summary>
    private static bool ContainerdStore() =>
        Bash("docker info --format '{{.DriverStatus}}'").Out.Contains("io.containerd.snapshotter", StringComparison.Ordinal);

    private sealed record Scenario(ComposeRunResult Result, IReadOnlyDictionary<string, string> Inspected, string Pins,
        IReadOnlyDictionary<string, string?> Running);

    /// <summary>
    /// Plans and runs a project with a pulled service (<see cref="PulledImage"/>) and a built one through the planner and
    /// the runner. Right after the pin file is written, it records the inspected ids and moves the tags of
    /// <paramref name="move"/> to the other service's image. Returns the run, the inspected ids, the pin file and, per
    /// service, the image id and pin label of its container (null without one). Cleans up everything it made.
    /// </summary>
    private static async Task<Scenario> RunAsync(params string[] move)
    {
        var project = $"whiskers-pin-{Guid.NewGuid():N}"[..25];
        var dir = $"/tmp/{project}";
        var built = $"{project}-built";
        var tags = new Dictionary<string, string> { ["pulled"] = PulledImage, ["built"] = built };
        var yaml = $"name: {project}\nservices:\n" +
                   $"  pulled:\n    image: {PulledImage}\n    command: [\"sleep\", \"600\"]\n" +
                   "  built:\n    build: .\n    command: [\"sleep\", \"600\"]\n";
        var host = new LocalHost();
        var docker = new FakeDocker();
        string? pulledBefore = null;
        var inspected = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            Assert.Equal(0, Bash($"mkdir -p {dir} && printf 'FROM {PulledImage}\\nLABEL whiskers.test=pin\\n' > {dir}/Dockerfile").Exit);
            Assert.Equal(0, Bash($"docker pull -q {PulledImage}").Exit);
            pulledBefore = Id(PulledImage);

            var plan = await new ComposePlanner(docker, host).PlanAsync(ComposeDeployment.FromContent("local", dir, yaml));
            var result = await new ComposeRunner(host, docker).RunAsync(plan, async (command, timeout, ct) =>
            {
                var done = await host.ExecuteAsync("local", command, timeout, ct);
                if (command.Contains(ComposePins.FileName, StringComparison.Ordinal) && command.Contains("base64 -d", StringComparison.Ordinal))
                {
                    // Step 4 passed and the pin file is written: record what was inspected, then move the tags.
                    inspected["pulled"] = Id(PulledImage);
                    inspected["built"] = Id(built);
                    foreach (var service in move)
                        Assert.Equal(0, Bash($"docker tag {inspected[service == "pulled" ? "built" : "pulled"]} {tags[service]}").Exit);
                }
                if (command.Contains(" up -d", StringComparison.Ordinal))
                {
                    // What the inventory lists after up (the re-check reads it): the project's containers.
                    var listed = Bash("docker ps -a --no-trunc --filter label=com.docker.compose.project=" + project +
                                      " --format '{{.ID}}\t{{.Names}}\t{{.Label \"com.docker.compose.service\"}}'").Out;
                    foreach (var line in listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var parts = line.Split('\t');
                        docker.Add(new ContainerInfo
                        {
                            Id = parts[0], Name = parts[1], ServerId = "local", State = "running",
                            Labels = new() { [ComposeModel.ProjectLabel] = project, [ComposeModel.ServiceLabel] = parts[2] },
                        });
                    }
                }
                return done;
            }, new ComposeRunOptions(Pull: true, TimeSpan.FromMinutes(5)));

            var running = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var service in tags.Keys)
            {
                var id = Bash($"docker ps -aq --filter label=com.docker.compose.project={project} " +
                              $"--filter label=com.docker.compose.service={service}").Out.Trim();
                running[service] = id.Length == 0 ? null : Bash(
                    $"docker inspect --format '{{{{.Image}}}}|{{{{index .Config.Labels \"{ComposePins.ReferenceLabel}\"}}}}' {id}").Out.Trim();
            }
            return new Scenario(result, inspected, Bash($"cat {dir}/.whiskers/{ComposePins.FileName}").Out, running);
        }
        finally
        {
            Bash($"docker compose -p {project} down --timeout 1 >/dev/null 2>&1; rm -rf {dir}");
            // The pulled tag back first: removing the built image by id removes every tag it carries.
            if (pulledBefore is not null)
                Bash($"docker tag {pulledBefore} {PulledImage}");
            Bash($"docker image rm {built} >/dev/null 2>&1");
            if (inspected.TryGetValue("built", out var builtId))
                Bash($"docker image rm {builtId} >/dev/null 2>&1");
        }
    }

    [SkippableFact]
    public async Task A_pulled_and_a_built_service_start_exactly_the_inspected_images_with_the_pin_file()
    {
        DockerRequirement.SkipUnlessAvailable(DockerAvailable(), "the Docker daemon or the docker compose CLI is not available");

        var run = await RunAsync();

        Assert.True(run.Result.Success, $"{run.Result.Refusal}\n{run.Result.Error}\n{string.Join('\n', run.Result.Violations)}\n{run.Result.Output}");
        Assert.Empty(run.Result.Violations);
        Assert.NotEqual(run.Inspected["pulled"], run.Inspected["built"]);
        // D10: the pulled service by the repo digest of its own repository. The built one by its id (Q6) on the
        // classic store; the containerd store records a repo digest for a local build too, so rule 1 applies there.
        Assert.Matches(new Regex("\"pulled\":\\n    image: \"docker\\.io/library/busybox@sha256:[0-9a-f]{64}\""), run.Pins);
        Assert.Matches(ContainerdStore()
            ? new Regex("\"built\":\\n    image: \"docker\\.io/library/whiskers-pin-[0-9a-f]{12}-built@sha256:[0-9a-f]{64}\"")
            : new Regex($"\"built\":\\n    image: \"{run.Inspected["built"]}\""), run.Pins);
        // Every container runs the image step 4 inspected and carries the reference the project names.
        Assert.Equal($"{run.Inspected["pulled"]}|{PulledImage}", run.Running["pulled"]);
        Assert.StartsWith($"{run.Inspected["built"]}|whiskers-pin-", run.Running["built"]);
    }

    [SkippableFact]
    public async Task Moving_both_tags_after_the_inspection_never_starts_another_image()
    {
        DockerRequirement.SkipUnlessAvailable(DockerAvailable(), "the Docker daemon or the docker compose CLI is not available");

        var run = await RunAsync("pulled", "built");

        // Whatever runs, runs the image step 4 inspected for its service.
        foreach (var (service, running) in run.Running)
            if (running is not null)
                Assert.StartsWith(run.Inspected[service] + "|", running);
        if (!ContainerdStore())
        {
            // The classic store keeps the repo digest reference: the run starts both, exactly as inspected.
            Assert.True(run.Result.Success, $"{run.Result.Error}\n{string.Join('\n', run.Result.Violations)}\n{run.Result.Output}");
            Assert.NotNull(run.Running["pulled"]);
            Assert.NotNull(run.Running["built"]);
        }
        else
        {
            // Containerd store: a moved tag takes the repo digest reference along (a local build has one there too), so
            // up fails closed instead of starting the image the tag points to now. Only an id pin would start the
            // inspected image here (contract 0013, D10; an amendment is proposed).
            Assert.False(run.Result.Success);
            Assert.Contains("No such image: ", run.Result.Output);
        }
    }
}
