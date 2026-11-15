using System.Net;
using System.Net.Sockets;

namespace Whiskers.Tests;

/// <summary>Contract 0017 part I (Amendment 2): <c>deploy/tailnet-guard.sh</c> without Python. jq reads the peer list,
/// the TCP probe is <c>nc -z -w5</c> or bash's /dev/tcp under <c>timeout 5</c>; a missing tool keeps the behaviour of its
/// function (no peers / no probe = only the address is checked), only a tool the whole script needs refuses the start.
/// Runs the real script against stubs, always with <c>--check-only</c> (no repair, no systemctl).</summary>
public sealed class TailnetGuardScriptTests : IDisposable
{
    private static readonly string[] Script = ["deploy", "tailnet-guard.sh"];
    private readonly ShellScriptHarness _h = new();

    public TailnetGuardScriptTests()
    {
        _h.Stub("ip", "#!/bin/bash\n[ -n \"${STUB_NO_ADDR:-}\" ] && exit 0\necho '    inet 100.64.0.1/32 scope global tailscale0'\n");
        _h.Stub("logger", "#!/bin/bash\nexit 0\n");
        _h.Stub("flock", "#!/bin/bash\nexit 0\n");
        _h.Stub("systemctl", "#!/bin/bash\necho \"systemctl $*\" >> \"$STUB_DIR/systemctl.log\"\nexit 0\n");
        _h.Stub("sleep", "#!/bin/bash\nexit 0\n");
        _h.Stub("nc", "#!/bin/bash\necho \"$*\" >> \"$STUB_DIR/nc.log\"\nexit ${STUB_NC_EXIT:-0}\n");
        File.WriteAllText(Path.Combine(_h.Root, "servers.json"), """
            {"Servers":[
              {"Id":"a","TcpHost":"100.64.0.5","Enabled":true},
              {"Id":"b","TcpHost":"100.64.0.6","Enabled":false},
              {"Id":"c","TcpHost":""},
              {"Id":"d","TcpHost":"100.64.0.7"}
            ]}
            """);
    }

    public void Dispose() => _h.Dispose();

    private (int Exit, string Output) Guard(string[] hostTools, Dictionary<string, string?>? env = null)
    {
        env ??= new();
        env["WHISKERS_SERVERS_JSON"] = _h.BashPath("servers.json");
        var (exit, stdout, stderr) = _h.Run("bash", Script, ["--check-only"], hostTools, env);
        return (exit, stdout + stderr);
    }

    private string NcLog => File.Exists(Path.Combine(_h.Root, "nc.log")) ? File.ReadAllText(Path.Combine(_h.Root, "nc.log")) : "";

    [Fact]
    public void The_script_names_its_tools_and_calls_no_python()
    {
        var text = DeploymentScripts.ReadRepoFile(Script);
        Assert.Contains("# Tools (no Python", text);
        Assert.DoesNotContain("python", text.ToLowerInvariant().Replace("no python", ""));
    }

    [SkippableFact]
    public void A_peer_that_answers_on_nc_is_healthy_and_only_enabled_peers_are_listed()
    {
        var (exit, output) = Guard(["grep", "jq"]);
        Assert.Equal(0, exit);
        Assert.Contains("tailnet OK\n", output.Replace("\r", ""));
        Assert.DoesNotContain("WARN", output);
        Assert.Equal("-z -w5 -- 100.64.0.5 2376\n", NcLog.Replace("\r", ""));   // the first enabled peer answered
    }

    [SkippableFact]
    public void An_nc_without_z_falls_back_to_dev_tcp_before_a_peer_counts_as_unreachable()
    {
        // busybox's nc has no -z: it fails on every call, which must not read as "peer unreachable".
        _h.Stub("nc", "#!/bin/bash\necho \"$*\" >> \"$STUB_DIR/nc.log\"\necho 'nc: invalid option -- z' >&2\nexit 1\n");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        File.WriteAllText(Path.Combine(_h.Root, "servers.json"), "[{\"TcpHost\":\"127.0.0.1\"}]");

        var (exit, output) = Guard(["grep", "jq", "timeout", "bash"], new() { ["TAILNET_GUARD_PORT"] = port.ToString() });

        Assert.True(exit == 0, output);
        Assert.Contains("tailnet OK\n", output.Replace("\r", ""));
        Assert.Contains($"-z -w5 -- 127.0.0.1 {port}", NcLog);   // nc was tried first
    }

    [SkippableFact]
    public void No_peer_answering_is_reported_broken_and_every_enabled_peer_is_probed()
    {
        var (exit, output) = Guard(["grep", "jq"], new() { ["STUB_NC_EXIT"] = "1" });
        Assert.Equal(1, exit);
        Assert.Contains("no tailnet peer answered on :2376", output);
        Assert.Equal("-z -w5 -- 100.64.0.5 2376\n-z -w5 -- 100.64.0.7 2376\n", NcLog.Replace("\r", ""));
        Assert.False(File.Exists(Path.Combine(_h.Root, "systemctl.log")));   // check-only restarts nothing
    }

    [SkippableFact]
    public void Without_jq_there_are_no_probe_targets_and_the_address_check_decides()
    {
        var (exit, output) = Guard(["grep"], new() { ["STUB_NC_EXIT"] = "1" });
        Assert.Equal(0, exit);
        Assert.Contains("WARN: tailnet checked by address only: jq missing", output);
        Assert.Contains("tailnet OK (address only: jq missing)", output);
        Assert.Equal("", NcLog);
        Assert.Equal(1, Guard(["grep"], new() { ["STUB_NO_ADDR"] = "1" }).Exit);
    }

    [SkippableFact]
    public void Without_nc_the_probe_uses_dev_tcp_under_timeout()
    {
        _h.Unstub("nc");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        File.WriteAllText(Path.Combine(_h.Root, "servers.json"), "[{\"TcpHost\":\"127.0.0.1\"}]");

        var open = Guard(["grep", "jq", "timeout", "bash"], new() { ["TAILNET_GUARD_PORT"] = port.ToString() });
        Assert.True(open.Exit == 0, open.Output);
        listener.Stop();
        var closed = Guard(["grep", "jq", "timeout", "bash"], new() { ["TAILNET_GUARD_PORT"] = port.ToString() });
        Assert.Equal(1, closed.Exit);
        Assert.Contains("no tailnet peer answered", closed.Output);
    }

    [SkippableFact]
    public void Without_nc_and_timeout_the_probe_cannot_run_and_the_address_check_decides()
    {
        _h.Unstub("nc");
        var (exit, output) = Guard(["grep", "jq"]);
        Assert.Equal(0, exit);
        Assert.Contains("WARN: tailnet checked by address only: neither nc nor timeout", output);
        Assert.Contains("tailnet OK (address only: neither nc nor timeout for the TCP probe)", output);
    }

    [SkippableFact]
    public void Without_ip_the_script_refuses_to_start_and_repairs_nothing()
    {
        _h.Unstub("ip");
        var env = new Dictionary<string, string?> { ["WHISKERS_SERVERS_JSON"] = _h.BashPath("servers.json") };
        var (exit, stdout, stderr) = _h.Run("bash", Script, [], ["grep", "jq"], env);   // not check-only: it would repair
        Assert.Equal(1, exit);
        Assert.Contains("missing tool: ip", stdout + stderr);
        Assert.False(File.Exists(Path.Combine(_h.Root, "systemctl.log")));
    }
}
