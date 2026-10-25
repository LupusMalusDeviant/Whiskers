using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.AutoPatch;
using Whiskers.Services.Cve;
using Whiskers.Services.ImageUpdate;

namespace Whiskers.Tests;

/// <summary>
/// Security auto-patching (2026-10-25). The rules that decide what an unattended night may touch, the
/// reading of the host script's verdict, and the blind spot in the update checker that made 305 fixable
/// CVEs invisible.
/// </summary>
public class AutoPatchTests
{
    // ---------------------------------------------------------------- update checker: what runs vs. the tag

    [Theory]
    [InlineData("e9e1a1e18e8e", true)]
    [InlineData("sha256:e9e1a1e18e8e127c1b7602063cc6a13db0a12b62dfd770d83936c089751d498d", true)]
    [InlineData("caddy:2-alpine", false)]
    [InlineData("ghcr.io/music-assistant/server:latest", false)]
    [InlineData("deadbeef", false)]           // too short for an ID — could be a local image name
    public void A_bare_image_id_is_recognised(string image, bool expected)
        => Assert.Equal(expected, ImageUpdateChecker.LooksLikeImageId(image));

    [Fact]
    public void A_stale_container_is_checked_by_its_configured_name_and_its_running_image()
    {
        // The case that hid kira-music for a week: the tag had moved on, so the listing showed the old ID.
        var (image, localRef) = ImageUpdateChecker.ResolveCheckRefs(
            "e9e1a1e18e8e", "ghcr.io/music-assistant/server:latest", "sha256:e9e1a1e18e8e00");

        Assert.Equal("ghcr.io/music-assistant/server:latest", image);
        Assert.Equal("sha256:e9e1a1e18e8e00", localRef);
    }

    [Fact]
    public void The_local_digest_comes_from_the_running_image_not_the_tag()
    {
        // Even with the name still listed, the tag may point at a newer pull than the container runs.
        var (image, localRef) = ImageUpdateChecker.ResolveCheckRefs("postgres:16-alpine", null, "sha256:old");

        Assert.Equal("postgres:16-alpine", image);
        Assert.Equal("sha256:old", localRef);
    }

    [Fact]
    public void Without_a_running_image_id_the_tag_is_the_fallback()
    {
        var (image, localRef) = ImageUpdateChecker.ResolveCheckRefs("nginx:alpine", null, "");
        Assert.Equal(("nginx:alpine", "nginx:alpine"), (image, localRef));
    }

    // ---------------------------------------------------------------- planner

    private static ContainerInfo Compose(string name, string image = "caddy:2-alpine", string server = "local",
        string state = "running", Dictionary<string, string>? extra = null)
    {
        var labels = new Dictionary<string, string>
        {
            ["com.docker.compose.project"] = "p",
            ["com.docker.compose.service"] = name,
            ["com.docker.compose.project.working_dir"] = "/opt/p",
        };
        foreach (var kv in extra ?? new()) labels[kv.Key] = kv.Value;
        return new ContainerInfo { Id = name + "-id", Name = name, Image = image, ServerId = server, State = state, Labels = labels };
    }

    private static CveScanResult Scan(string name, string server = "local", params (CveSeverity Sev, string? Fix)[] findings)
        => new()
        {
            ServerId = server,
            Source = CveSource.Container,
            ContainerId = name + "-before-recreate",   // deliberately NOT the live id
            ContainerName = name,
            Findings = findings.Select((f, i) => new CveFinding
            {
                CveId = $"CVE-2026-{i:0000}", Severity = f.Sev, FixedVersion = f.Fix, Package = "p",
            }).ToList(),
        };

    [Fact]
    public void Only_fixable_findings_at_or_above_the_threshold_make_a_candidate()
    {
        var plan = AutoPatchPlanner.Plan(
            [Compose("fixable"), Compose("unfixable"), Compose("low-only")],
            [
                Scan("fixable", "local", (CveSeverity.High, "1.2.3")),
                Scan("unfixable", "local", (CveSeverity.Critical, null), (CveSeverity.High, "")),
                Scan("low-only", "local", (CveSeverity.Medium, "1.0")),
            ],
            new AutoPatchSettings());

        Assert.Equal(["fixable"], plan.Candidates.Select(c => c.Container.Name));
        Assert.Empty(plan.Skipped);   // nothing to fix is not a skip — it is not worth a line
    }

    [Theory]
    [InlineData("serverwatch")]
    [InlineData("ghostunnel")]
    [InlineData("socket-proxy")]
    [InlineData("nginx-proxy-manager")]
    public void Containers_carrying_whiskers_own_connection_are_never_touched(string name)
    {
        var plan = AutoPatchPlanner.Plan([Compose(name)], [Scan(name, "local", (CveSeverity.Critical, "1"))],
            new AutoPatchSettings());

        Assert.Empty(plan.Candidates);
        Assert.Contains("own connection", Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Exclusions_opt_outs_non_compose_stopped_and_pinned_containers_are_skipped_with_a_reason()
    {
        var standalone = new ContainerInfo { Id = "s", Name = "standalone", Image = "x:1", ServerId = "local", State = "running" };
        var plan = AutoPatchPlanner.Plan(
            [
                Compose("excluded"),
                Compose("opted-out", extra: new() { [AutoPatchPlanner.OptOutLabel] = "false" }),
                standalone,
                Compose("stopped", state: "exited"),
                Compose("pinned", image: "caddy@sha256:abc"),
            ],
            new[] { "excluded", "opted-out", "standalone", "stopped", "pinned" }
                .Select(n => Scan(n, "local", (CveSeverity.High, "1"))),
            new AutoPatchSettings { ExcludeContainers = ["EXCLUDED"] });

        Assert.Empty(plan.Candidates);
        var reasons = plan.Skipped.ToDictionary(s => s.ContainerName, s => s.Reason);
        Assert.Contains("settings", reasons["excluded"]);
        Assert.Contains("opted out", reasons["opted-out"]);
        Assert.Contains("compose", reasons["standalone"]);
        Assert.Contains("not running", reasons["stopped"]);
        Assert.Contains("digest", reasons["pinned"]);
    }

    [Fact]
    public void Findings_are_matched_by_server_and_name_not_by_container_id()
    {
        // A scan from before last night's recreate carries the OLD id; the name is what survives.
        var plan = AutoPatchPlanner.Plan(
            [Compose("caddy", server: "rabenhof"), Compose("caddy", server: "zirkuswagen")],
            [Scan("CADDY", "rabenhof", (CveSeverity.High, "1"))],
            new AutoPatchSettings());

        var c = Assert.Single(plan.Candidates);
        Assert.Equal("rabenhof", c.Container.ServerId);
    }

    [Fact]
    public void Stateless_services_go_before_databases_and_the_nightly_limit_holds()
    {
        var plan = AutoPatchPlanner.Plan(
            [Compose("db", image: "postgres:16-alpine"), Compose("proxy"), Compose("web", image: "nginx:alpine")],
            [
                Scan("db", "local", (CveSeverity.Critical, "1"), (CveSeverity.Critical, "1"), (CveSeverity.Critical, "1")),
                Scan("proxy", "local", (CveSeverity.High, "1")),
                Scan("web", "local", (CveSeverity.High, "1"), (CveSeverity.High, "1")),
            ],
            new AutoPatchSettings { MaxPerCycle = 2 });

        Assert.Equal(["web", "proxy"], plan.Candidates.Select(c => c.Container.Name));
        Assert.Contains("limit", Assert.Single(plan.Skipped, s => s.ContainerName == "db").Reason);
    }

    // ---------------------------------------------------------------- the script's verdict

    [Theory]
    [InlineData("UPD-OK v2.11.4 -> v2.11.7", AutoPatchResultKind.Updated, "v2.11.4 -> v2.11.7")]
    [InlineData("UPD-NOCHANGE", AutoPatchResultKind.NoChange, "")]
    [InlineData("UPD-WOULD-UPDATE 16.14 -> 16.15", AutoPatchResultKind.WouldUpdate, "16.14 -> 16.15")]
    [InlineData("UPD-SKIP-MAJOR 2.11 -> 3.0", AutoPatchResultKind.SkippedMajor, "2.11 -> 3.0")]
    [InlineData("UPD-SKIP-UNKNOWN-MAJOR tag=latest", AutoPatchResultKind.SkippedUnknownMajor, "tag=latest")]
    [InlineData("UPD-ABORT compose-drift run=a file=b", AutoPatchResultKind.Aborted, "compose-drift run=a file=b")]
    [InlineData("UPD-FAILED-ROLLBACK-VERIFIED", AutoPatchResultKind.RolledBack, "")]
    [InlineData("UPD-FAILED-ROLLBACK-NOT-VERIFIED", AutoPatchResultKind.RollbackNotVerified, "")]
    public void Every_result_line_of_the_script_is_understood(string line, AutoPatchResultKind kind, string detail)
    {
        var log = $"container=x project=p\r\nbackup_tag=x:pre-upd\r\n{line}\r\nx:1 running healthy sha256:abc\r\n";
        Assert.Equal(new AutoPatchOutcome(kind, detail), AutoPatchOutcome.Parse(log));
    }

    [Fact]
    public void The_last_result_line_wins()
    {
        // Defensive: should a log ever carry two verdicts, the later one is what happened.
        var outcome = AutoPatchOutcome.Parse("UPD-OK 1 -> 2\n--- logs\nUPD-FAILED-ROLLBACK-VERIFIED\n");
        Assert.Equal(AutoPatchResultKind.RolledBack, outcome.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ssh: connect to host … timed out")]
    public void No_result_line_is_unknown_and_needs_attention(string? log)
    {
        var outcome = AutoPatchOutcome.Parse(log);
        Assert.Equal(AutoPatchResultKind.Unknown, outcome.Kind);
        Assert.True(outcome.NeedsAttention);
    }

    [Fact]
    public void A_dry_run_or_a_skip_is_not_an_attempt()
    {
        Assert.False(new AutoPatchOutcome(AutoPatchResultKind.WouldUpdate, "").Attempted);
        Assert.False(new AutoPatchOutcome(AutoPatchResultKind.SkippedMajor, "").Attempted);
        Assert.True(new AutoPatchOutcome(AutoPatchResultKind.RolledBack, "").Attempted);
        Assert.True(new AutoPatchOutcome(AutoPatchResultKind.Aborted, "db-dump-unverified size=0").NeedsAttention);
        Assert.False(new AutoPatchOutcome(AutoPatchResultKind.Aborted, "compose-drift").NeedsAttention);
    }

    // ---------------------------------------------------------------- the script as shipped

    [Fact]
    public void The_script_is_embedded_with_lf_line_endings()
    {
        var (b64, sha) = ScriptAutoPatchRunner.LoadScript();
        var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));

        Assert.DoesNotContain('\r', text);
        Assert.StartsWith("#!/bin/bash", text);
        Assert.Contains("UPD-WOULD-UPDATE", text);
        // 2026-10-25: a date-only backup tag let authentik's postgres overwrite pack-postgres' on the same host.
        Assert.Contains("BK=\"$REPO:pre-upd-${TS:0:8}-$C\"", text);
        Assert.Matches("^[0-9a-f]{64}$", sha);
    }

    [Fact]
    public void The_command_installs_verifies_and_only_then_runs()
    {
        var cmd = ScriptAutoPatchRunner.BuildCommand("rabenhof-caddy", dryRun: true, "QUJD", new string('a', 64));

        var install = cmd.IndexOf("base64 -d", StringComparison.Ordinal);
        var verify = cmd.IndexOf("script-integrity", StringComparison.Ordinal);
        var run = cmd.IndexOf("DRY_RUN=1 /bin/bash", StringComparison.Ordinal);
        Assert.True(install >= 0 && install < verify && verify < run, cmd);
        Assert.EndsWith("tail -c 6000 /var/log/img-update-rabenhof-caddy.log", cmd);
        Assert.DoesNotContain("DRY_RUN", ScriptAutoPatchRunner.BuildCommand("x", dryRun: false, "QUJD", "a"));
    }

    [Theory]
    [InlineData("caddy; rm -rf /")]
    [InlineData("$(id)")]
    [InlineData("-rf")]
    [InlineData("")]
    public async Task A_name_that_docker_could_not_have_produced_never_reaches_the_shell(string name)
    {
        var runner = new ScriptAutoPatchRunner(new ThrowingExecutor(), NullLogger<ScriptAutoPatchRunner>.Instance);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => runner.PatchAsync("local", name, dryRun: true));
    }

    [Fact]
    public void The_script_parses_and_its_major_guard_reads_versions_right()
    {
        // Runs where bash exists (CI, Git-Bash). The guard is the line between "patch" and "new major version",
        // so it is executed, not just read: the functions are cut out of the shipped script and called.
        var bash = FindBash();
        if (bash is null) return;

        var (b64, _) = ScriptAutoPatchRunner.LoadScript();
        var dir = Directory.CreateTempSubdirectory("autopatch-").FullName;
        var script = Path.Combine(dir, "img-update.sh");
        var probe = Path.Combine(dir, "probe.sh");
        File.WriteAllBytes(script, Convert.FromBase64String(b64));
        File.WriteAllText(probe,
            "eval \"$(sed -n '/^major()/p' img-update.sh)\"\n" +
            "for v in v2.11.7 16.15 8.4.13-1.el9 b11718 latest ''; do printf '%s=%s;' \"$v\" \"$(major \"$v\")\"; done\n");
        try
        {
            Assert.Equal(0, Run(bash, "-n img-update.sh", dir).Exit);

            var (exit, output) = Run(bash, "probe.sh", dir);
            Assert.Equal(0, exit);
            Assert.Equal("v2.11.7=2;16.15=16;8.4.13-1.el9=8;b11718=;latest=;=;", output.Trim());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string? FindBash()
    {
        foreach (var candidate in new[] { "/bin/bash", "/usr/bin/bash", @"C:\Program Files\Git\bin\bash.exe" })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static (int Exit, string Output) Run(string file, string args, string workingDir)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = workingDir,
        })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.ExitCode, output);
    }

    private sealed class ThrowingExecutor : Whiskers.Services.Server.IHostCommandExecutor
    {
        public Task<Whiskers.Services.Server.CommandResult> ExecuteAsync(string serverId, string command,
            TimeSpan? timeout = null, CancellationToken ct = default, int? maxOutputChars = null)
            => throw new InvalidOperationException("must not be reached");
    }

    // ---------------------------------------------------------------- the nightly loop

    [Fact]
    public void The_cycle_runs_once_inside_the_window_and_only_when_enabled()
    {
        var on = new AutoPatchSettings { Enabled = true, RunAtHourUtc = 3 };
        var at3 = new DateTime(2026, 10, 26, 3, 10, 0, DateTimeKind.Utc);

        Assert.True(AutoPatchService.IsDue(on, at3, lastRun: null));
        Assert.False(AutoPatchService.IsDue(on, at3, lastRun: DateOnly.FromDateTime(at3)));
        Assert.False(AutoPatchService.IsDue(on, at3.AddHours(1), lastRun: null));
        Assert.False(AutoPatchService.IsDue(new AutoPatchSettings { RunAtHourUtc = 3 }, at3, lastRun: null));
    }

    [Fact]
    public async Task Without_a_completed_scan_nothing_is_patched()
    {
        var runner = new RecordingRunner();
        var service = NewService(runner, new CveFindingsStore(persistPath: TempPath()), Compose("caddy"));

        Assert.True(await service.RunCycleAsync(forceDryRun: false));
        Assert.Empty(runner.Calls);
        Assert.Contains("No CVE scan", service.LastCycle!.Error);
    }

    [Fact]
    public async Task A_rollback_that_could_not_be_verified_stops_the_night()
    {
        var store = new CveFindingsStore(persistPath: TempPath()) { LastScanAt = DateTime.UtcNow };
        store.Set(Scan("a", "local", (CveSeverity.High, "1"), (CveSeverity.High, "1")));
        store.Set(Scan("b", "local", (CveSeverity.High, "1")));
        var runner = new RecordingRunner { Verdicts = { ["a"] = AutoPatchResultKind.RollbackNotVerified } };
        var notifications = new FakeNotifications();
        var service = NewService(runner, store, notifications, Compose("a"), Compose("b"));

        await service.RunCycleAsync(forceDryRun: false);

        Assert.Equal(["a"], runner.Calls.Select(c => c.Name));        // b is never reached
        Assert.Contains("rollback not verified", service.LastCycle!.Error);
        Assert.Single(notifications.Events);
    }

    [Fact]
    public async Task A_forced_dry_run_stays_a_dry_run_even_with_apply_configured()
    {
        var store = new CveFindingsStore(persistPath: TempPath()) { LastScanAt = DateTime.UtcNow };
        store.Set(Scan("a", "local", (CveSeverity.Critical, "1")));
        var runner = new RecordingRunner();
        var service = NewService(runner, store, new FakeNotifications(), new AutoPatchSettings { Enabled = true, DryRun = false },
            Compose("a"));

        await service.RunCycleAsync(forceDryRun: true);

        Assert.True(Assert.Single(runner.Calls).DryRun);
        Assert.True(service.LastCycle!.DryRun);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"cve-{Guid.NewGuid():N}.json");

    private static AutoPatchService NewService(IAutoPatchRunner runner, ICveFindingsStore store, params ContainerInfo[] containers)
        => NewService(runner, store, new FakeNotifications(), new AutoPatchSettings(), containers);

    private static AutoPatchService NewService(IAutoPatchRunner runner, ICveFindingsStore store,
        FakeNotifications notifications, params ContainerInfo[] containers)
        => NewService(runner, store, notifications, new AutoPatchSettings(), containers);

    private static AutoPatchService NewService(IAutoPatchRunner runner, ICveFindingsStore store,
        FakeNotifications notifications, AutoPatchSettings settings, params ContainerInfo[] containers)
        => new(new StaticOptionsMonitor<AutoPatchSettings>(settings), new FakeDocker(containers), store, runner,
            notifications, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AutoPatchService>.Instance);

    private sealed class RecordingRunner : IAutoPatchRunner
    {
        public List<(string Server, string Name, bool DryRun)> Calls { get; } = new();
        public Dictionary<string, AutoPatchResultKind> Verdicts { get; } = new();

        public Task<(AutoPatchOutcome Outcome, string LogTail)> PatchAsync(string serverId, string containerName,
            bool dryRun, CancellationToken ct = default)
        {
            Calls.Add((serverId, containerName, dryRun));
            var kind = Verdicts.GetValueOrDefault(containerName, dryRun ? AutoPatchResultKind.WouldUpdate : AutoPatchResultKind.Updated);
            return Task.FromResult((new AutoPatchOutcome(kind, ""), ""));
        }
    }
}
