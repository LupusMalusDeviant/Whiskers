using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Services.Certificates;

namespace Whiskers.Tests;

/// <summary>
/// R1-44 (contract 0007, amendments 3–7): the host scripts of the allow-list steps — exact command lines, and on Linux
/// the confirmation and the guard run for real under <c>sh</c> with <c>flock</c> and <c>timeout</c>, against stubs of
/// <c>systemctl</c>, <c>systemd-run</c>, <c>docker</c> and <c>logger</c> (paths moved from <c>/opt/dockerproxy</c> to a
/// temporary directory).
/// </summary>
public sealed class PerHostCredentialShellTests : IDisposable
{
    private static readonly MtlsAllowListCommands.ComposeTarget Target =
        new("dockerproxy", "/opt/dockerproxy", "/opt/dockerproxy/docker-compose.yml");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whiskers-r144-sh-" + Guid.NewGuid().ToString("N"));

    // ---- command lines ----

    [Fact]
    public void The_recreate_is_exactly_the_running_project_file_and_service_without_pulling()
    {
        Assert.Equal(
            "docker compose -p dockerproxy --project-directory /opt/dockerproxy -f /opt/dockerproxy/docker-compose.yml up -d --pull never --no-deps --force-recreate ghostunnel",
            MtlsAllowListCommands.Recreate(Target));
    }

    [Fact]
    public void Apply_edits_only_the_allow_cn_items_arms_the_guard_first_and_recreates_detached()
    {
        var script = MtlsCertificateRenewalTests.Decode(MtlsAllowListCommands.Apply(Target,
            ["serverwatch-client", "whiskers-client-rabenhof-g1"], "r1"));

        Assert.Contains("cns='serverwatch-client whiskers-client-rabenhof-g1'", script);
        // Only this run's marker; never the markers of other runs (review r1 MINOR 3).
        Assert.Contains("rm -f /opt/dockerproxy/.allow-ok-r1\n", script.Replace("\r\n", "\n"));
        Assert.DoesNotContain(".allow-ok-*", script);
        Assert.Contains("another run left /opt/dockerproxy/docker-compose.yml.prev while a guard is armed", script);
        var guard = script.IndexOf("systemd-run --collect --unit=whiskers-allow-guard-r1 --on-active=180 /bin/sh /opt/dockerproxy/allow-guard-r1.sh", StringComparison.Ordinal);
        var swap = script.IndexOf("mv -f /opt/dockerproxy/docker-compose.yml.new /opt/dockerproxy/docker-compose.yml", StringComparison.Ordinal);
        var recreate = script.IndexOf("systemd-run --collect --unit=whiskers-ghostunnel-recreate-r1 --on-active=3 " + MtlsAllowListCommands.Recreate(Target), StringComparison.Ordinal);
        Assert.True(guard > 0 && swap > guard && recreate > swap, script);
        Assert.Contains("the patch would change more than --allow-cn", script);
        // Never the certificate renewal's guard or marker.
        Assert.DoesNotContain("renew-guard", script);
        Assert.DoesNotContain(".renew-ok", script);
    }

    [Fact]
    public void The_guard_waits_for_the_lock_without_a_limit_and_recreates_under_a_guard_named_unit()
    {
        var guard = MtlsAllowListCommands.GuardBody(Target, "r1");

        Assert.Contains("exec 9>/opt/dockerproxy/.migrate-r1.lock\nflock 9\n", guard.Replace("\r\n", "\n"));
        Assert.DoesNotContain("-w", Regex.Match(guard, "flock[^\n]*").Value);
        Assert.Contains("if [ -f /opt/dockerproxy/.allow-ok-r1 ]", guard);
        Assert.Contains("systemd-run --collect --unit=whiskers-allow-guard-recreate-r1 --on-active=1 " + MtlsAllowListCommands.Recreate(Target), guard);
        Assert.Contains("systemd-run --collect --unit=whiskers-allow-guard-recreate2-r1 --on-active=1 " + MtlsAllowListCommands.Recreate(Target), guard);
        Assert.Contains("docker inspect ghostunnel --format '{{.State.Running}}'", guard);
        Assert.Contains("logger -t whiskers-allow-guard", guard);
        // The guard's recreate units match the pattern the preflight lists as guards.
        Assert.Matches("^whiskers-.*-guard", "whiskers-allow-guard-recreate-r1");
    }

    [Fact]
    public void The_confirmation_runs_its_locked_section_under_timeout_and_requires_a_waiting_timer()
    {
        var confirm = MtlsCertificateRenewalTests.Decode(MtlsAllowListCommands.Confirm(Target, "r1"));
        Assert.Contains("| timeout -k 5 20 flock -w 30 /opt/dockerproxy/.migrate-r1.lock sh -s", confirm);

        var body = SimulatedProxyHost.LockedBody(confirm);
        Assert.Equal(MtlsAllowListCommands.ConfirmBody(Target, "r1"), body);
        Assert.Contains("systemctl show -p LoadState --value whiskers-allow-guard-r1.timer)\" = \"loaded\"", body);
        Assert.Contains("systemctl show -p SubState --value whiskers-allow-guard-r1.timer)\" = \"waiting\"", body);
        Assert.Contains("systemctl is-active --quiet whiskers-allow-guard-r1.service", body);
        Assert.True(body.IndexOf("systemctl stop whiskers-allow-guard-r1.timer", StringComparison.Ordinal)
                    < body.IndexOf("touch /opt/dockerproxy/.allow-ok-r1", StringComparison.Ordinal));
        // Every exit 1 comes before the stop (review r3 MINOR 1b): the .prev check, too.
        var stop = body.IndexOf("systemctl stop", StringComparison.Ordinal);
        Assert.True(body.IndexOf(".prev ] ||", StringComparison.Ordinal) < stop);
        Assert.DoesNotContain("exit 1", body[stop..]);
        // Everything from the stop on fails with the distinct code, never with the "rolls back" exit 1 (review r2 NIT 1).
        Assert.Contains("systemctl stop whiskers-allow-guard-r1.timer || { echo 'stopping the guard timer failed' >&2; exit 75; }", body);
        Assert.Contains("touch /opt/dockerproxy/.allow-ok-r1 || { echo 'the marker could not be written' >&2; exit 75; }", body);
        Assert.Contains("|| { echo 'the previous file could not be removed' >&2; exit 75; }", body);
    }

    [Fact]
    public void The_cleanup_after_the_phase_record_removes_the_marker()
    {
        var cleanup = MtlsCertificateRenewalTests.Decode(MtlsAllowListCommands.Cleanup("r1"));
        Assert.Contains("rm -f /opt/dockerproxy/.allow-ok-r1 /opt/dockerproxy/.migrate-r1.lock", cleanup);
    }

    [Theory]
    [InlineData("a;b")]
    [InlineData("R1")]
    [InlineData("")]
    public void A_run_suffix_outside_the_pattern_never_reaches_a_script(string suffix)
    {
        Assert.Throws<ArgumentException>(() => MtlsAllowListCommands.Confirm(Target, suffix));
    }

    // ---- the scripts, run ----

    [SkippableTheory]
    [InlineData("loaded", "waiting", false, 0)]
    [InlineData("loaded", "elapsed", false, 1)]
    [InlineData("loaded", "dead", false, 1)]
    [InlineData("not-found", "dead", false, 1)]
    [InlineData("loaded", "waiting", true, 1)]
    public void The_confirmation_writes_the_marker_only_for_its_own_still_waiting_timer(string load, string sub, bool serviceActive, int exit)
    {
        RequireShell();
        Arrange(load, sub);
        if (serviceActive) File.WriteAllText(State("active"), "");

        var (code, _, stderr) = RunScript(ConfirmScript());

        Assert.Equal(exit, code);
        Assert.Equal(exit == 0, File.Exists(Marker()));
        Assert.Equal(exit != 0, File.Exists(Compose() + ".prev"));
        if (exit == 0) Assert.Contains("systemctl stop whiskers-allow-guard-r1.timer", File.ReadAllText(State("log")));
        else Assert.NotEmpty(stderr);
    }

    [SkippableFact]
    public void A_missing_prev_file_fails_the_confirmation_before_the_timer_is_stopped()
    {
        // Review r3 MINOR 1b: exit 1 ("the guard rolls back") only while the guard timer still runs.
        RequireShell();
        Arrange("loaded", "waiting");
        File.Delete(Compose() + ".prev");

        var (code, _, stderr) = RunScript(ConfirmScript());

        Assert.Equal(1, code);
        Assert.Contains("previous allow-list file is gone", stderr);
        Assert.DoesNotContain("systemctl stop", File.Exists(State("log")) ? File.ReadAllText(State("log")) : "");
        Assert.False(File.Exists(Marker()));
    }

    [SkippableFact]
    public void A_confirmation_that_fails_after_stopping_the_timer_exits_with_its_own_code()
    {
        // Review r2 NIT 1: the timer is already stopped, so no guard is left to roll back — the caller must not promise one.
        RequireShell();
        Arrange("loaded", "waiting");
        Stub("touch", "exit 1");

        var (code, _, stderr) = RunScript(ConfirmScript());

        Assert.Equal(MtlsAllowListCommands.ConfirmFailedAfterStop, code);
        Assert.Contains("the marker could not be written", stderr);
        Assert.Contains("systemctl stop whiskers-allow-guard-r1.timer", File.ReadAllText(State("log")));
        Assert.False(File.Exists(Marker()));
    }

    [SkippableFact]
    public void A_confirmation_hanging_inside_the_lock_is_killed_after_20_seconds_and_the_guard_then_rolls_back()
    {
        RequireShell();
        Arrange("loaded", "waiting");
        File.WriteAllText(State("hang"), "");

        var watch = Stopwatch.StartNew();
        var (code, _, stderr) = RunScript(ConfirmScript(), timeoutMs: 60_000);
        watch.Stop();

        Assert.NotEqual(0, code);
        Assert.InRange(watch.Elapsed.TotalSeconds, 19, 40);
        Assert.Contains("within 20 seconds", stderr);
        Assert.False(File.Exists(Marker()));

        var (guardCode, _, _) = RunScript(GuardScript(), timeoutMs: 30_000);
        Assert.Equal(0, guardCode);
        Assert.Contains("--allow-cn=serverwatch-client", File.ReadAllText(Compose()));
        Assert.DoesNotContain("whiskers-client-rabenhof-g1", File.ReadAllText(Compose()));
        Assert.Contains("whiskers-allow-guard-recreate-r1", File.ReadAllText(State("log")));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_guard_blocked_on_the_lock_still_rolls_back_after_release_unless_its_marker_is_present(bool marker)
    {
        RequireShell();
        Arrange("loaded", "elapsed");
        if (marker) File.WriteAllText(Marker(), "");
        var sleep = DeploymentScripts.FindOnPath("sleep")!;
        using var holder = Process.Start(new ProcessStartInfo("flock", [Lock(), sleep, "3"]) { UseShellExecute = false })!;
        Thread.Sleep(500);

        var watch = Stopwatch.StartNew();
        var (code, _, _) = RunScript(GuardScript(), timeoutMs: 30_000);
        watch.Stop();

        Assert.Equal(0, code);
        Assert.True(watch.Elapsed.TotalSeconds >= 2, "the guard did not wait for the lock");
        var compose = File.ReadAllText(Compose());
        if (marker)
        {
            Assert.Contains("whiskers-client-rabenhof-g1", compose);
            Assert.False(File.Exists(Marker()));
            Assert.DoesNotContain("recreate-r1", File.ReadAllText(State("log")));
        }
        else
        {
            Assert.DoesNotContain("whiskers-client-rabenhof-g1", compose);
            Assert.Contains("restored the previous allow-list", File.ReadAllText(State("log")));
        }
    }

    [SkippableFact]
    public void A_guard_whose_recreate_did_not_take_retries_once_and_logs_the_failure()
    {
        RequireShell();
        Arrange("loaded", "elapsed");
        File.WriteAllText(State("recreate-fails"), "");

        var (code, _, _) = RunScript(GuardScript(), timeoutMs: 30_000);

        Assert.Equal(0, code);
        var log = File.ReadAllText(State("log"));
        Assert.Contains("whiskers-allow-guard-recreate2-r1", log);
        Assert.Contains("logger -p user.err", log);
    }

    [Fact]
    public void Reading_the_host_fails_closed_when_systemd_cannot_list_the_guards()
    {
        var read = MtlsCertificateRenewalTests.Decode(MtlsAllowListCommands.ReadHostState());
        Assert.Contains("listed=$(systemctl list-units --no-legend --plain --state=active,activating 'whiskers-*-guard*') || { echo", read);
        Assert.DoesNotContain("2>/dev/null | awk", read);
    }

    [SkippableFact]
    public void The_guard_listing_exits_non_zero_when_systemctl_fails()
    {
        RequireShell();
        Arrange("loaded", "waiting");
        File.WriteAllText(State("list-fails"), "");

        var (code, stdout, stderr) = RunScript(MtlsCertificateRenewalTests.Decode(MtlsAllowListCommands.ListGuardUnits()));

        Assert.NotEqual(0, code);
        Assert.Equal("", stdout.Trim());
        Assert.Contains("unknown", stderr);
    }

    [SkippableFact]
    public void Apply_rewrites_only_the_allow_cn_items_arms_the_guard_before_the_swap_and_keeps_other_runs_markers()
    {
        RequireShell();
        Arrange("loaded", "waiting");
        File.Delete(Compose() + ".prev");
        File.WriteAllText(Compose(), OnboardingCompose("serverwatch-client"));
        var before = File.ReadAllText(Compose());
        File.WriteAllText(Marker(), "");                                  // a stale marker of this run
        File.WriteAllText(Path.Combine(_dir, ".allow-ok-r9"), "");        // another run's marker

        var (code, stdout, stderr) = RunScript(ApplyScript(["serverwatch-client", "whiskers-client-rabenhof-g1"]));

        Assert.True(code == 0, stderr);
        Assert.Contains("applied", stdout);
        Assert.Equal(OnboardingCompose("serverwatch-client", "whiskers-client-rabenhof-g1"), File.ReadAllText(Compose()));
        Assert.Equal(before, File.ReadAllText(Compose() + ".prev"));
        Assert.False(File.Exists(Marker()));
        Assert.True(File.Exists(Path.Combine(_dir, ".allow-ok-r9")));
        Assert.True(File.Exists(Path.Combine(_dir, "allow-guard-r1.sh")));
        var log = File.ReadAllLines(State("log")).Where(l => l.StartsWith("systemd-run", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, log.Count);
        Assert.Contains("--unit=whiskers-allow-guard-r1 --on-active=180", log[0]);
        Assert.Contains("--unit=whiskers-ghostunnel-recreate-r1 --on-active=3 docker compose -p dockerproxy", log[1]);
    }

    [SkippableFact]
    public void Apply_refuses_a_compose_file_with_an_allow_cn_line_it_cannot_edit_safely()
    {
        RequireShell();
        Arrange("loaded", "waiting");
        File.Delete(Compose() + ".prev");
        var odd = OnboardingCompose("serverwatch-client").Replace("- --allow-cn=serverwatch-client", "- --allow-cn=serverwatch-client # keep", StringComparison.Ordinal);
        File.WriteAllText(Compose(), odd);

        var (code, _, stderr) = RunScript(ApplyScript(["whiskers-client-rabenhof-g1"]));

        Assert.NotEqual(0, code);
        Assert.Contains("unexpected --allow-cn lines", stderr);
        Assert.Equal(odd, File.ReadAllText(Compose()));
        Assert.False(File.Exists(Compose() + ".prev"));
        Assert.DoesNotContain("systemd-run", File.ReadAllText(State("log")));
    }

    [SkippableFact]
    public void Apply_refuses_a_patch_that_would_change_more_than_the_allow_cn_items()
    {
        RequireShell();
        Arrange("loaded", "waiting");
        File.Delete(Compose() + ".prev");
        File.WriteAllText(Compose(), OnboardingCompose("serverwatch-client"));
        var before = File.ReadAllText(Compose());
        // An awk that also changes another line (a broken or tampered tool): the check after the patch must stop it.
        var awk = DeploymentScripts.FindOnPath("awk")!;
        Stub("awk", awk + " \"$@\" | sed -e 's/--unsafe-target/--unsafe-target-x/'");

        var (code, _, stderr) = RunScript(ApplyScript(["whiskers-client-rabenhof-g1"]));

        Assert.NotEqual(0, code);
        Assert.Contains("the patch would change more than --allow-cn", stderr);
        Assert.Equal(before, File.ReadAllText(Compose()));
        Assert.False(File.Exists(Compose() + ".new"));
        Assert.False(File.Exists(Compose() + ".prev"));
        Assert.DoesNotContain("systemd-run", File.ReadAllText(State("log")));
    }

    [SkippableFact]
    public void Apply_never_overwrites_another_runs_prev_while_a_guard_is_armed()
    {
        RequireShell();
        Arrange("loaded", "waiting");   // leaves a .prev of run r1
        var prev = File.ReadAllText(Compose() + ".prev");
        File.WriteAllText(State("units"), "whiskers-allow-guard-r1.timer loaded active waiting\n");

        var (code, _, stderr) = RunScript(ApplyScript(["whiskers-client-rabenhof-g1"], "r2"));

        Assert.NotEqual(0, code);
        Assert.Contains("while a guard is armed", stderr);
        Assert.Equal(prev, File.ReadAllText(Compose() + ".prev"));
    }

    // ---- harness ----

    private static void RequireShell()
    {
        var missing = new[] { "sh", "flock", "timeout", "base64" }.Where(t => DeploymentScripts.FindOnPath(t) is null).ToList();
        Skip.If(OperatingSystem.IsWindows() || (missing.Count > 0 && !DeploymentScripts.InCi),
            "POSIX sh with flock and timeout is needed: " + string.Join(", ", missing));
    }

    private string State(string name) => Path.Combine(_dir, "state", name);
    private string Compose() => Path.Combine(_dir, "docker-compose.yml");
    private string Marker() => Path.Combine(_dir, ".allow-ok-r1");
    private string Lock() => Path.Combine(_dir, ".migrate-r1.lock");

    /// <summary>A widened compose file with its .prev, a guard timer in the given state and the stubs.</summary>
    private void Arrange(string load, string sub)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "state"));
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Compose() + ".prev", OnboardingCompose("serverwatch-client"));
        File.WriteAllText(Compose(), OnboardingCompose("serverwatch-client", "whiskers-client-rabenhof-g1"));
        File.WriteAllText(State("load"), load);
        File.WriteAllText(State("sub"), sub);
        File.WriteAllText(State("log"), "");
        File.WriteAllText(State("units"), "");
        File.WriteAllText(State("args"), "--allow-cn=serverwatch-client\n--allow-cn=whiskers-client-rabenhof-g1\n");
        var sleep = DeploymentScripts.FindOnPath("sleep")!;
        Stub("systemctl", $$"""
            echo "systemctl $*" >> "$STUB/log"
            case "$1" in
              show)
                case "$3" in
                  LoadState) cat "$STUB/load" ;;
                  SubState) if [ -f "$STUB/hang" ]; then {{sleep}} 60; fi; cat "$STUB/sub" ;;
                esac ;;
              is-active) [ -f "$STUB/active" ] && exit 0; exit 3 ;;
              list-units) [ -f "$STUB/list-fails" ] && exit 1; cat "$STUB/units" ;;
              stop) echo stopped > "$STUB/sub" ;;
            esac
            """);
        // A recreate takes ghostunnel's arguments from the compose file, unless told to fail.
        Stub("systemd-run", """
            echo "systemd-run $*" >> "$STUB/log"
            if [ ! -f "$STUB/recreate-fails" ]; then grep -e 'allow-cn' "$COMPOSE" | sed -e 's/^[[:space:]]*- //' -e 's/[[:space:]]*$//' > "$STUB/args"; fi
            """);
        Stub("docker", """
            case "$*" in
              *State.Running*) echo true ;;
              *range*) cat "$STUB/args" ;;
            esac
            """);
        Stub("logger", """echo "logger $*" >> "$STUB/log" """);
        Stub("sleep", "exit 0");
    }

    private void Stub(string name, string body) =>
        DeploymentScripts.WriteExecutable(Path.Combine(_dir, "bin", name), "#!/bin/sh\n" + body + "\n");

    private static string OnboardingCompose(params string[] allow)
    {
        var text = Whiskers.Services.Onboarding.OnboardingCommands.DockerProxyCompose("100.64.0.9", "placeholder");
        return text.Replace("      - --allow-cn=placeholder\n", string.Concat(allow.Select(a => "      - --allow-cn=" + a + "\n")), StringComparison.Ordinal);
    }

    private string Moved(string script) => script.Replace("/opt/dockerproxy", _dir, StringComparison.Ordinal);

    /// <summary>The confirmation as the host runs it (inner script, locked section re-encoded with the moved paths).</summary>
    private string ConfirmScript()
    {
        var outer = MtlsCertificateRenewalTests.Decode(MtlsAllowListCommands.Confirm(Target, "r1"));
        var body = SimulatedProxyHost.LockedBody(outer);
        var moved = Convert.ToBase64String(Encoding.UTF8.GetBytes(Moved(body)));
        return Moved(Regex.Replace(outer, @"echo \S+ \| base64 -d \| timeout", "echo " + moved + " | base64 -d | timeout"));
    }

    private string ApplyScript(string[] allow, string run = "r1") =>
        Moved(MtlsCertificateRenewalTests.Decode(MtlsAllowListCommands.Apply(Target, allow, run)));

    private string GuardScript() => Moved(MtlsAllowListCommands.GuardBody(Target, "r1"));

    private (int Exit, string Stdout, string Stderr) RunScript(string script, int timeoutMs = 30_000)
    {
        var path = Path.Combine(_dir, "script.sh");
        File.WriteAllText(path, script.Replace("\r\n", "\n"));
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = Path.Combine(_dir, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["STUB"] = Path.Combine(_dir, "state"),
            ["COMPOSE"] = Compose(),
        };
        return DeploymentScripts.Run("sh", [path], env, _dir, timeoutMs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
