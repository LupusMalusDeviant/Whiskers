using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using Microsoft.Extensions.Caching.Memory;
using Whiskers.Services.Docker;
using Whiskers.Services.Docker.Operations;

namespace Whiskers.Tests;

/// <summary>
/// Fable review H5: the helpers Whiskers starts on managed hosts run with the host's root, so they run a pinned image
/// only — never a tag that can move or be replaced on the host.
/// </summary>
public class HelperImagePinningTests
{
    [Fact]
    public void The_helper_image_is_pinned_by_digest()
    {
        Assert.Matches("^alpine:[0-9.]+@sha256:[0-9a-f]{64}$", HelperImages.Alpine);
        Assert.EndsWith("@" + HelperImages.AlpineDigest, HelperImages.Alpine);
    }

    [Fact]
    public async Task The_privileged_host_shell_helper_is_pulled_and_created_by_digest()
    {
        var docker = new RecordingShellDocker();
        var shell = new HostShellOperations(_ => Task.FromResult<IHostShellDocker>(docker), new MemoryCache(new MemoryCacheOptions()));

        await shell.RunHostShellAsync("uptime", "srv", TimeSpan.FromSeconds(10));

        Assert.Equal([HelperImages.Alpine], docker.Checked);
        Assert.Equal([(HelperImages.AlpineRepository, HelperImages.AlpineDigest)], docker.Pulled);
        Assert.Equal(HelperImages.Alpine, Assert.Single(docker.Created).Image);
        Assert.True(Assert.Single(docker.Created).HostConfig.Privileged);
    }

    [Fact]
    public void No_source_starts_an_unpinned_alpine()
    {
        // A string literal that is an alpine reference without a digest ("alpine", "alpine:3.22", "alpine:latest"), the
        // tag passed apart from the name ("alpine", "latest"), or a `docker run … alpine[:tag] …` line — anywhere in the
        // app except HelperImages, the one place the pinned reference is spelled out.
        var unpinned = new Regex(
            @"""alpine(:[\w.\-]+)?""|""alpine"",\s*""[\w.\-]+""|docker run[^\n]*\salpine(:[\w.\-]+)?([\s""]|$)");
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Whiskers"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "HelperImages.cs")
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => unpinned.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private sealed class RecordingShellDocker : IHostShellDocker
    {
        public List<string> Checked { get; } = [];
        public List<(string, string)> Pulled { get; } = [];
        public List<CreateContainerParameters> Created { get; } = [];

        public Task<bool> HasImageAsync(string image, CancellationToken ct) { Checked.Add(image); return Task.FromResult(false); }
        public Task PullImageAsync(string repository, string tag, CancellationToken ct) { Pulled.Add((repository, tag)); return Task.CompletedTask; }
        public Task<string> CreateAsync(CreateContainerParameters parameters, CancellationToken ct)
        {
            Created.Add(parameters);
            return Task.FromResult("helper1");
        }
        public Task StartAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<long> WaitAsync(string id, CancellationToken ct) => Task.FromResult(0L);
        public Task<(string Output, string Error)> ReadLogsAsync(string id, CancellationToken ct) => Task.FromResult(("", ""));
        public Task KillAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<HostShellHelper>> ListHelpersAsync(string label, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HostShellHelper>>([]);
    }
}
