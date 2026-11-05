using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Whiskers.Tests;

/// <summary>Runs the overlay's db-upgrade-guard script (taken from the compose file, <see cref="PostgresOverlayTests.GuardScript"/>)
/// inside a container the way compose does (<c>/bin/sh -c</c>), with extra environment and optionally a file as standard
/// input, like <c>docker compose run -T … &lt; whiskers-pg17.sql</c>.</summary>
internal static class GuardRunner
{
    public const string Marker = "/new/.whiskers-restored-from-17";
    public const string Tag = "-- whiskers-pg17-control: ";

    public static async Task<(long? Exit, string Out, string Err)> RunAsync(IContainer box, string? stdinFile = null, params string[] env)
    {
        await box.CopyAsync(new UTF8Encoding(false).GetBytes(PostgresOverlayTests.GuardScript()), "/guard.sh");
        var assignments = string.Join(" ", env);
        var input = stdinFile is null ? "< /dev/null" : $"< {stdinFile}";
        var result = await box.ExecAsync(["sh", "-c", $"exec env {assignments} sh -c \"$(cat /guard.sh)\" {input}"]);
        return (result.ExitCode, result.Stdout, result.Stderr);
    }

    public static async Task WriteAsync(IContainer box, string path, string content) =>
        await box.CopyAsync(new UTF8Encoding(false).GetBytes(content), path);

    public static async Task<string?> MarkerAsync(IContainer box)
    {
        var read = await box.ExecAsync(["sh", "-c", $"cat {Marker}"]);
        return read.ExitCode == 0 ? read.Stdout.Trim() : null;
    }

    public static async Task<string> Sha256Async(IContainer box, string path)
    {
        var sum = await box.ExecAsync(["sh", "-c", $"sha256sum {path} | cut -d' ' -f1"]);
        Assert.True(sum.ExitCode == 0, sum.Stderr);
        return sum.Stdout.Trim();
    }

    /// <summary>A PostgreSQL 17 data directory as far as the guard looks at it: PG_VERSION, global/pg_control, and the
    /// per-database PG_VERSION files below base/ that the depth limit must not mistake for clusters.</summary>
    public static string Cluster(string dir) =>
        $"mkdir -p {dir}/global {dir}/base/1 && echo 17 > {dir}/PG_VERSION && echo 17 > {dir}/base/1/PG_VERSION"
        + $" && head -c 8192 /dev/urandom > {dir}/global/pg_control";

    /// <summary>Empty /old and /new, then <paramref name="prepare"/>.</summary>
    public static async Task ResetAsync(IContainer box, string prepare = "true")
    {
        var setup = await box.ExecAsync(["sh", "-c", $"rm -rf /old /new /tmp/in.sql && mkdir /old /new && {prepare}"]);
        Assert.True(setup.ExitCode == 0, $"setup failed: {setup.Stderr}");
    }
}

/// <summary>
/// The overlay's db-upgrade-guard (deploy/docker-compose.postgres.yml), RUN: the script is taken from the compose file
/// itself (not a copy) and executed with the image's /bin/sh against prepared /old (the PostgreSQL 17 volume) and /new
/// (the PostgreSQL 18 volume) contents. The normal run passes only for an empty 17 volume or a 17 cluster whose
/// pg_control fingerprint equals the marker the restore mode wrote; the fingerprint mode prints that fingerprint as one
/// SQL comment line; the restore mode refuses a dump that is incomplete or not bound to the current 17 data before it
/// ever contacts a server. The server-side checks of the restore mode run in <see cref="PostgresUpgradeTests"/>.
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

    private void SkipUnlessUp() => DockerRequirement.SkipUnlessStarted(_startFailure, "The guard test container");

    private static string Cluster(string dir) => GuardRunner.Cluster(dir);

    private static string MarkerOf(string dir) =>
        $"sha256sum {dir}/global/pg_control | cut -d' ' -f1 > {GuardRunner.Marker}";

    public static TheoryData<string, string, int> Cases => new()
    {
        { "empty 17 volume, empty 18 volume (fresh install)", "true", 0 },
        { "17 cluster at the root, no marker (blind pull)", Cluster("/old"), 1 },
        { "17 cluster at the root, matching marker (restored)", $"{Cluster("/old")} && {MarkerOf("/old")}", 0 },
        { "17 cluster at the root, different marker (rollback)",
            $"{Cluster("/old")} && echo 0000000000000000000000000000000000000000000000000000000000000000 > {GuardRunner.Marker}", 1 },
        { "17 cluster in a pgdata/ subdirectory, no marker (custom PGDATA)", Cluster("/old/pgdata"), 1 },
        { "17 cluster in a pgdata/ subdirectory, matching marker", $"{Cluster("/old/pgdata")} && {MarkerOf("/old/pgdata")}", 0 },
        { "17 volume not empty but no PG_VERSION (unrecognised content)", "echo hello > /old/notes.txt && mkdir -p /old/x/y", 1 },
        { "17 volume holding only lost+found", "mkdir /old/lost+found", 1 },
        { "two clusters in the 17 volume", $"{Cluster("/old/a")} && {Cluster("/old/b")}", 1 },
        { "18 cluster but no 17 data (17 volume removed)", "mkdir -p /new/18/docker && echo 18 > /new/18/docker/PG_VERSION", 0 },
    };

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public async Task The_normal_run_decides_on_the_volumes_contents(string scenario, string prepare, int expected)
    {
        SkipUnlessUp();
        await GuardRunner.ResetAsync(_box, prepare);
        var markerBefore = await GuardRunner.MarkerAsync(_box);

        var run = await GuardRunner.RunAsync(_box);

        Assert.True(expected == run.Exit, $"{scenario}: exit {run.Exit}, expected {expected}\n{run.Out}{run.Err}");
        if (expected == 1)
        {
            Assert.Contains("whiskers:", run.Err);
            Assert.Contains("docker volume ls", run.Err);
        }
        // The normal run never writes.
        Assert.Equal(markerBefore, await GuardRunner.MarkerAsync(_box));
        Assert.Equal(0, (await _box.ExecAsync(["sh", "-c", $"test ! -e {GuardRunner.Marker}.tmp"])).ExitCode);
    }

    [SkippableFact]
    public async Task The_rollback_refusal_names_the_way_out()
    {
        SkipUnlessUp();
        await GuardRunner.ResetAsync(_box, $"{Cluster("/old")} && echo other > {GuardRunner.Marker}");
        var run = await GuardRunner.RunAsync(_box);
        Assert.Equal(1, run.Exit);
        Assert.Contains("the PostgreSQL 17 data changed after the upgrade (a rollback?)", run.Err);
        Assert.Contains("remove the 18 volume", run.Err);
    }

    [SkippableFact]
    public async Task The_blind_pull_refusal_points_to_the_guide()
    {
        SkipUnlessUp();
        await GuardRunner.ResetAsync(_box, Cluster("/old"));
        var run = await GuardRunner.RunAsync(_box);
        Assert.Equal(1, run.Exit);
        Assert.Contains("PostgreSQL 17 data found in the volume whiskers-db-data", run.Err);
        Assert.Contains("docs/operations/postgres-18-upgrade.md (dump, restore, start)", run.Err);
    }

    [SkippableFact]
    public async Task The_fingerprint_mode_prints_one_comment_line_with_the_17_clusters_fingerprint()
    {
        SkipUnlessUp();
        foreach (var dir in new[] { "/old", "/old/pgdata" })
        {
            await GuardRunner.ResetAsync(_box, Cluster(dir));
            var expected = await GuardRunner.Sha256Async(_box, $"{dir}/global/pg_control");

            var run = await GuardRunner.RunAsync(_box, null, "WHISKERS_FINGERPRINT=1");

            Assert.True(run.Exit == 0, run.Err);
            Assert.Equal($"{GuardRunner.Tag}{expected}\n", run.Out);   // exactly one line on stdout, appended to the dump
            Assert.Equal("", run.Err);
            Assert.Null(await GuardRunner.MarkerAsync(_box));
        }
    }

    [SkippableFact]
    public async Task The_fingerprint_mode_refuses_while_postgresql_17_runs()
    {
        SkipUnlessUp();
        foreach (var dir in new[] { "/old", "/old/pgdata" })
        {
            await GuardRunner.ResetAsync(_box, $"{Cluster(dir)} && echo 4711 > {dir}/postmaster.pid");

            var run = await GuardRunner.RunAsync(_box, null, "WHISKERS_FINGERPRINT=1");

            Assert.True(run.Exit == 1, $"{dir}: exit {run.Exit}\n{run.Out}{run.Err}");
            Assert.Equal("", run.Out);   // nothing appended to the dump
            Assert.Contains("Stop PostgreSQL 17 first: c17 stop -t 120 db.", run.Err);
            Assert.Contains("not shut down cleanly: run step 1", run.Err);   // a stale pid file after a kill
        }
    }

    [SkippableFact]
    public async Task The_fingerprint_and_restore_modes_refuse_without_17_data_or_with_unrecognised_content()
    {
        SkipUnlessUp();
        foreach (var (prepare, message) in new[]
                 {
                     ("true", "no PostgreSQL 17 data in whiskers-db-data: there is nothing to fingerprint or restore."),
                     ("echo hello > /old/notes.txt", "unrecognised content in whiskers-db-data"),
                 })
        foreach (var mode in new[] { "WHISKERS_FINGERPRINT=1", "WHISKERS_RESTORE=1" })
        {
            await GuardRunner.ResetAsync(_box, prepare);
            var run = await GuardRunner.RunAsync(_box, null, mode);
            Assert.True(run.Exit == 1, $"{mode} / {prepare}: exit {run.Exit}\n{run.Err}");
            Assert.Equal("", run.Out);
            Assert.Contains(message, run.Err);
            Assert.Null(await GuardRunner.MarkerAsync(_box));
        }
    }

    // A dump's text as far as the restore mode looks at it before contacting the server.
    private const string Footer = "--\n-- PostgreSQL database dump complete\n--\n";

    public static TheoryData<string, string, string> BadDumps => new()
    {
        { "no footer (empty or cut-off dump)", "SELECT 1;\n{tag}{fp}\n", "the dump is incomplete" },
        { "no fingerprint line", $"SELECT 1;\n{Footer}", "the dump needs exactly one line" },
        { "two fingerprint lines", $"SELECT 1;\n{Footer}{{tag}}{{fp}}\n{{tag}}{{fp}}\n", "the dump needs exactly one line" },
        { "fingerprint of other 17 data (stale dump)",
            $"SELECT 1;\n{Footer}{{tag}}0000000000000000000000000000000000000000000000000000000000000000\n",
            "the dump is not of the current PostgreSQL 17 data" },
    };

    [SkippableTheory]
    [MemberData(nameof(BadDumps))]
    public async Task The_restore_mode_refuses_a_dump_that_is_incomplete_or_not_of_the_current_17_data(
        string scenario, string dump, string message)
    {
        SkipUnlessUp();
        await GuardRunner.ResetAsync(_box, Cluster("/old"));
        var fp = await GuardRunner.Sha256Async(_box, "/old/global/pg_control");
        await GuardRunner.WriteAsync(_box, "/tmp/in.sql", dump.Replace("{tag}", GuardRunner.Tag).Replace("{fp}", fp));

        // No server is reachable here (no host "db"): each refusal must come before the restore contacts one.
        var run = await GuardRunner.RunAsync(_box, "/tmp/in.sql", "WHISKERS_RESTORE=1");

        Assert.True(run.Exit == 1, $"{scenario}: exit {run.Exit}\n{run.Err}");
        Assert.Contains(message, run.Err);
        Assert.Contains("nothing restored", run.Err);
        Assert.Null(await GuardRunner.MarkerAsync(_box));
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
        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Assert.Fail($"docker compose {string.Join(' ', args)} did not finish within 60 s");
        }
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
