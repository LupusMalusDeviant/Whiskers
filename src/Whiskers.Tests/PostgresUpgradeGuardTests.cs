using System.Diagnostics;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Whiskers.Tests;

/// <summary>
/// The overlay's db-upgrade-guard (deploy/docker-compose.postgres.yml), RUN: the script is taken from the compose file
/// itself (<see cref="PostgresOverlayTests.GuardScript"/>, not a copy) and executed with the image's /bin/sh against
/// prepared /old (the PostgreSQL 17 volume) and /new (the PostgreSQL 18 volume) contents. The guard passes only for an
/// empty 17 volume or a 17 cluster whose pg_control fingerprint equals the marker a confirmed restore wrote.
/// </summary>
[Trait("Category", DockerRequirement.Category)]
public sealed class PostgresUpgradeGuardTests : IAsyncLifetime
{
    private readonly IContainer _box = new ContainerBuilder("postgres:18-alpine")
        .WithEntrypoint("tail", "-f", "/dev/null")
        .Build();
    private Exception? _startFailure;

    public async Task InitializeAsync() => _startFailure = await DockerRequirement.TryStartAsync(_box);

    public async Task DisposeAsync()
    {
        if (_startFailure is null) await _box.DisposeAsync();
    }

    // A PostgreSQL 17 data directory as far as the guard looks at it: PG_VERSION, global/pg_control, and the per-database
    // PG_VERSION files below base/ that the depth limit must not mistake for clusters.
    private static string Cluster(string dir) =>
        $"mkdir -p {dir}/global {dir}/base/1 && echo 17 > {dir}/PG_VERSION && echo 17 > {dir}/base/1/PG_VERSION"
        + $" && head -c 8192 /dev/urandom > {dir}/global/pg_control";

    private static string Marker(string dir) =>
        $"sha256sum {dir}/global/pg_control | cut -d' ' -f1 > /new/.whiskers-restored-from-17";

    public static TheoryData<string, string, int> Cases => new()
    {
        { "empty 17 volume, empty 18 volume (fresh install)", "true", 0 },
        { "17 cluster at the root, no marker (blind pull)", Cluster("/old"), 1 },
        { "17 cluster at the root, matching marker (confirmed restore)", $"{Cluster("/old")} && {Marker("/old")}", 0 },
        { "17 cluster at the root, different marker (rollback)",
            $"{Cluster("/old")} && echo 0000000000000000000000000000000000000000000000000000000000000000 > /new/.whiskers-restored-from-17", 1 },
        { "17 cluster in a pgdata/ subdirectory, no marker (custom PGDATA)", Cluster("/old/pgdata"), 1 },
        { "17 cluster in a pgdata/ subdirectory, matching marker", $"{Cluster("/old/pgdata")} && {Marker("/old/pgdata")}", 0 },
        { "17 volume not empty but no PG_VERSION (unrecognised content)", "echo hello > /old/notes.txt && mkdir -p /old/x/y", 1 },
        { "two clusters in the 17 volume", $"{Cluster("/old/a")} && {Cluster("/old/b")}", 1 },
        { "18 cluster but no 17 data (17 volume removed)", "mkdir -p /new/18/docker && echo 18 > /new/18/docker/PG_VERSION", 0 },
    };

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public async Task The_guard_decides_on_the_volumes_contents(string scenario, string prepare, int expected)
    {
        DockerRequirement.SkipUnlessStarted(_startFailure, "The guard test container");
        var setup = await _box.ExecAsync(["sh", "-c", $"rm -rf /old /new && mkdir /old /new && {prepare}"]);
        Assert.True(setup.ExitCode == 0, $"{scenario}: setup failed: {setup.Stderr}");

        var run = await _box.ExecAsync(["sh", "-c", PostgresOverlayTests.GuardScript()]);

        Assert.True(expected == run.ExitCode, $"{scenario}: exit {run.ExitCode}, expected {expected}\n{run.Stdout}{run.Stderr}");
        if (expected == 1)
        {
            Assert.Contains("whiskers:", run.Stderr);
            Assert.Contains("docker volume ls", run.Stderr);
        }
        // The normal run never writes: a refused or passed start leaves /new as it was.
        var tmp = await _box.ExecAsync(["sh", "-c", "test ! -e /new/.whiskers-restored-from-17.tmp"]);
        Assert.Equal(0, tmp.ExitCode);
    }

    [SkippableFact]
    public async Task The_rollback_refusal_names_the_way_out()
    {
        DockerRequirement.SkipUnlessStarted(_startFailure, "The guard test container");
        await _box.ExecAsync(["sh", "-c", $"rm -rf /old /new && mkdir /old /new && {Cluster("/old")} && echo other > /new/.whiskers-restored-from-17"]);
        var run = await _box.ExecAsync(["sh", "-c", PostgresOverlayTests.GuardScript()]);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("the PostgreSQL 17 data changed after the upgrade (a rollback?)", run.Stderr);
        Assert.Contains("remove the 18 volume", run.Stderr);
    }

    [SkippableFact]
    public async Task The_blind_pull_refusal_points_to_the_guide()
    {
        DockerRequirement.SkipUnlessStarted(_startFailure, "The guard test container");
        await _box.ExecAsync(["sh", "-c", $"rm -rf /old /new && mkdir /old /new && {Cluster("/old")}"]);
        var run = await _box.ExecAsync(["sh", "-c", PostgresOverlayTests.GuardScript()]);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("PostgreSQL 17 data found in the volume whiskers-db-data", run.Stderr);
        Assert.Contains("docs/operations/postgres-18-upgrade.md", run.Stderr);
    }
}

/// <summary>
/// The overlay as docker compose reads it: <c>docker compose -f docker-compose.yml -f deploy/docker-compose.postgres.yml
/// config</c> must accept it (interpolation, the YAML block of the guard), and the guard command compose hands to the
/// container must be the script the guard tests run. The password comes from the process environment, as from a .env.
/// Skipped without the docker compose CLI locally; CI has it.
/// </summary>
[Trait("Category", DockerRequirement.Category)]
public sealed class PostgresOverlayComposeTests
{
    private static (int Exit, string Out, string Err) Compose(params string[] args)
    {
        var psi = new ProcessStartInfo("docker")
        {
            WorkingDirectory = DeploymentScripts.RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("compose");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["WHISKERS_DB_PASSWORD"] = "compose-config-test";
        psi.Environment["MSYS_NO_PATHCONV"] = "1";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit(60_000);
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    private static bool ComposeAvailable()
    {
        try { return Compose("version").Exit == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static readonly string[] Files = ["-f", "docker-compose.yml", "-f", "deploy/docker-compose.postgres.yml"];

    [SkippableFact]
    public void Compose_accepts_the_overlay_and_hands_the_guard_its_script()
    {
        DockerRequirement.SkipUnlessAvailable(ComposeAvailable(), "the docker compose CLI is not available");

        var quiet = Compose([.. Files, "config", "-q"]);
        Assert.True(quiet.Exit == 0, quiet.Err);

        var json = Compose([.. Files, "config", "--format", "json"]);
        Assert.True(json.Exit == 0, json.Err);
        using var doc = JsonDocument.Parse(json.Out);
        var guard = doc.RootElement.GetProperty("services").GetProperty("db-upgrade-guard");
        var command = guard.GetProperty("command")[0].GetString()!.Replace("\r\n", "\n");
        // compose prints the model re-escaped ($ as $$); either way it is the block the guard tests run.
        Assert.Contains(command, new[] { PostgresOverlayTests.GuardScript(), PostgresOverlayTests.GuardScriptRaw() });
        Assert.Equal(["/bin/sh", "-c"], guard.GetProperty("entrypoint").EnumerateArray().Select(e => e.GetString()));
        var env = guard.GetProperty("environment");
        Assert.Equal("compose-config-test", env.GetProperty("PGPASSWORD").GetString());
        Assert.Equal("whiskers", env.GetProperty("POSTGRES_USER").GetString());
        Assert.Equal("whiskers", env.GetProperty("POSTGRES_DB").GetString());
    }
}
