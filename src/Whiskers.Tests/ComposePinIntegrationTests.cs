using System.Diagnostics;
using System.Text;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0013, D10–D13 on a real daemon: a project with a pulled and a built service goes through the planner and
/// <see cref="ComposeRunner"/> (write, pull, build, inspect, pin, <c>up --pull never --no-build</c> with the pin file,
/// re-check): every container runs exactly the inspected id. With both tags moved between the pin file and <c>up</c>,
/// both still start the inspected images — on the classic and on Docker 29's containerd store, since the pin is the image
/// id (D10 as amended on 2026-11-10; a repo digest pin stopped resolving there). Commands run locally through
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

    /// <summary>Contract 0013, § 4: the Compose version this run must use (CI's minimum-Compose step sets it). Unset, any
    /// Compose runs the test.</summary>
    private static readonly string? RequiredComposeVersion = Environment.GetEnvironmentVariable("WHISKERS_TEST_COMPOSE_VERSION");

    private static bool DockerAvailable()
    {
        bool available;
        try { available = Bash("docker info >/dev/null 2>&1 && docker compose version >/dev/null 2>&1", 60_000).Exit == 0; }
        catch (System.ComponentModel.Win32Exception) { available = false; }
        // A run that asked for a version proves nothing on another one: it fails instead of passing on the wrong Compose.
        if (available && !string.IsNullOrEmpty(RequiredComposeVersion))
        {
            var actual = Bash("docker compose version --short", 60_000).Out.Trim().TrimStart('v');
            Assert.True(actual == RequiredComposeVersion.TrimStart('v'),
                $"WHISKERS_TEST_COMPOSE_VERSION is {RequiredComposeVersion}, but docker compose is {actual}");
        }
        return available;
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
        // D10 as amended: both services by the image id step 4 inspected, whatever the image store.
        Assert.Contains($"\"pulled\":\n    image: \"{run.Inspected["pulled"]}\"", run.Pins);
        Assert.Contains($"\"built\":\n    image: \"{run.Inspected["built"]}\"", run.Pins);
        // Every container runs the image step 4 inspected and carries the reference the project names.
        Assert.Equal($"{run.Inspected["pulled"]}|{PulledImage}", run.Running["pulled"]);
        Assert.StartsWith($"{run.Inspected["built"]}|whiskers-pin-", run.Running["built"]);
    }

    [SkippableFact]
    public async Task Moving_both_tags_after_the_inspection_still_starts_the_inspected_images()
    {
        DockerRequirement.SkipUnlessAvailable(DockerAvailable(), "the Docker daemon or the docker compose CLI is not available");

        var run = await RunAsync("pulled", "built");

        // The tags now point the other way round; the id pins start exactly what step 4 inspected, on the classic and
        // on the containerd image store (contract 0013, D10 as amended on 2026-11-10).
        Assert.True(run.Result.Success, $"{run.Result.Refusal}\n{run.Result.Error}\n{string.Join('\n', run.Result.Violations)}\n{run.Result.Output}");
        Assert.Empty(run.Result.Violations);
        Assert.Equal($"{run.Inspected["pulled"]}|{PulledImage}", run.Running["pulled"]);
        Assert.StartsWith($"{run.Inspected["built"]}|whiskers-pin-", run.Running["built"]);
    }
}
