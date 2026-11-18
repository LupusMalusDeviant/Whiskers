using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Whiskers.Mcp;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Models.Hetzner;
using Whiskers.Services.Approvals;
using Whiskers.Services.Certificates;
using Whiskers.Services.Hetzner;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;
using Whiskers.Services.Onboarding;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018 part E: <c>add_server</c>, <c>onboard_server</c> and <c>get_onboarding_status</c> over MCP, through the
/// real guardrail filter and Mutation Gateway. Levels come from the catalog (Admin, D5); no tool takes a key; a rescue
/// bootstrap waits for a human Admin's approval and then runs once; the run is bound to the gateway's deadline, not to the
/// request (D8), and reports progress per step; the status is an Admin-only read of the latest run; a standing bootstrap
/// key is reported by both tools; abandon runs only the clean-up, and on a host with mTLS state it is refused over MCP
/// with a pointer to the UI (INV-59).
/// </summary>
public sealed class OnboardingMcpToolsTests : IDisposable
{
    private const string Id = "web-new";
    private const string Approver = "admin@example.com";

    private readonly McpFilterHarness _mcp = new();
    private readonly TempServerStore _store = new();
    private readonly HttpContextAccessor _http = new();
    private readonly IMcpPermissionService _permissions;
    private readonly FakeOnboarding _onboarding = new();
    private readonly OnboardingRuns _runs = new();
    private ServerConfig _server = null!;
    private FakeServerConfig _servers = null!;
    private bool _mtlsState;

    public OnboardingMcpToolsTests()
    {
        var (permissions, script) = ReadBackVerificationTests.Scripted<IMcpPermissionService>.Create();
        script.On[nameof(IMcpPermissionService.IsToolAllowed)] = _ => true;
        _permissions = permissions;
        NewRequest();
    }

    public void Dispose()
    {
        _mcp.Dispose();
        _store.Dispose();
    }

    // ---- catalog, levels, parameters ------------------------------------------------------------------------------

    [Fact]
    public void Levels_come_from_the_catalog()
    {
        var catalog = new CapabilityCatalog();
        Assert.Equal("server.create", catalog.FindByMcpTool("add_server")!.Id);
        Assert.Equal("server.onboard", catalog.FindByMcpTool("onboard_server")!.Id);
        Assert.Null(catalog.FindByMcpTool("get_onboarding_status"));
        foreach (var tool in new[] { "add_server", "onboard_server", "get_onboarding_status" })
        {
            Assert.Equal(McpPermissionLevels.Admin, McpPermissionLevels.DefaultToolLevels[tool]);
            Assert.Equal(McpPermissionLevels.Admin, Method(tool).GetCustomAttribute<McpToolLevelAttribute>()!.Level);
            Assert.Contains(tool, McpErrorEnvelope.Tools);
        }
        Assert.Equal(McpPermissionLevels.Admin, CapabilityCatalog.McpToolLevels["add_server"]);
        Assert.Equal(McpPermissionLevels.Admin, CapabilityCatalog.McpToolLevels["onboard_server"]);
        Assert.Equal(TimeSpan.FromMinutes(30), catalog.Find("server.onboard")!.Deadline);
    }

    /// <summary>A cloud API key, a password, a token or any other secret never comes in over MCP: no argument of the three
    /// tools (what the client sends) is named or typed like one.</summary>
    [Fact]
    public void No_tool_takes_a_key_parameter()
    {
        var secretish = new[] { "key", "token", "password", "secret", "credential", "auth", "pass" };
        foreach (var tool in new[] { "add_server", "onboard_server", "get_onboarding_status" })
        {
            var arguments = Method(tool).GetParameters()
                .Where(p => p.GetCustomAttribute<DescriptionAttribute>() is not null).ToList();
            Assert.NotEmpty(arguments);
            Assert.All(arguments, p =>
            {
                // cloudCredentialFrom names a server id whose stored credential is reused by reference, never a secret.
                if (p.Name == "cloudCredentialFrom") return;
                Assert.DoesNotContain(secretish, s => p.Name!.Contains(s, StringComparison.OrdinalIgnoreCase));
                Assert.Contains(Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType, new[] { typeof(string), typeof(bool) });
            });
        }
        Assert.Equal(["name", "ip", "group", "cloudProvider", "cloudCredentialFrom"], Arguments("add_server"));
        Assert.Equal(["server", "bootstrap", "confirmVmName", "abandon"], Arguments("onboard_server"));
        Assert.Equal(["server"], Arguments("get_onboarding_status"));
    }

    // ---- onboard_server -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Onboard_server_with_rescue_returns_pending_approval_then_runs_once()
    {
        Bound();

        var asked = await OnboardAsync(bootstrap: "hetzner_rescue");
        Assert.Equal(McpCallResult.PendingApproval, asked.Outcome.Result);
        Assert.Equal(McpErrorEnvelope.PendingApproval, Code(asked.Answer));
        Assert.Equal(0, _onboarding.Runs);
        var pending = Assert.Single(_mcp.Approvals.GetPending());
        Assert.Equal(McpPermissionLevels.Admin, pending.Level);

        // The key cannot approve its own request (INV-33); a person (an Admin) does.
        Assert.Equal(ApprovalDecisionResult.Decided,
            await _mcp.Approvals.DecideAsync(pending.Id, true, ApprovalResolver.User(Approver, AppRole.Admin)));
        NewRequest();
        var ran = await OnboardAsync(bootstrap: "hetzner_rescue");
        Assert.Equal(McpCallResult.Executed, ran.Outcome.Result);
        Assert.Equal(1, _onboarding.Runs);
        Assert.Equal(BootstrapMethod.HetznerRescueKey, _onboarding.Options!.Bootstrap);
        Assert.Equal(Id, _onboarding.ServerId);
        using (var json = JsonDocument.Parse(ran.Answer))
        {
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
            Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("operationId").GetString()));
        }

        // The grant is used once: the identical call asks again.
        NewRequest();
        var again = await OnboardAsync(bootstrap: "hetzner_rescue");
        Assert.Equal(McpCallResult.PendingApproval, again.Outcome.Result);
        Assert.Equal(1, _onboarding.Runs);
    }

    [Fact]
    public async Task Without_a_bootstrap_the_preset_decides()
    {
        Bound();

        var ran = await OnboardAsync();

        Assert.Equal(McpCallResult.Executed, ran.Outcome.Result);
        Assert.Empty(_mcp.Approvals.GetPending());
        Assert.Equal(BootstrapMethod.None, _onboarding.Options!.Bootstrap);
        Assert.False(_onboarding.MayReplace);                       // decided as a first onboarding (INV-59)
        Assert.Equal(MtlsOnboardingState.ConnectionOf(_server), _onboarding.DecidedConnection);
    }

    [Fact]
    public async Task Client_disconnect_does_not_cancel_the_run()
    {
        Bound();
        using var request = new CancellationTokenSource();
        _onboarding.During = () => request.Cancel();   // the client goes away while the run reboots the VM

        var ran = await OnboardAsync(ct: request.Token);

        Assert.True(request.IsCancellationRequested);
        Assert.False(_onboarding.TokenCancelled);
        Assert.True(_onboarding.TokenCanBeCancelled);               // bound to the gateway's deadline instead
        Assert.Equal(McpCallResult.Executed, ran.Outcome.Result);
        using var json = JsonDocument.Parse(ran.Answer);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Progress_notifications_per_step()
    {
        Bound();
        _onboarding.Report =
        [
            new OnboardingProgress(OnboardingStep.HostKey, "host key pinned"),
            new OnboardingProgress(OnboardingStep.Bootstrap, "bootstrap ok"),
            new OnboardingProgress(OnboardingStep.TailscaleLogin, "Tailscale-Login-Link", "https://login.tailscale.com/a/abc123"),
        ];
        var notes = new Notifications();

        await OnboardAsync(progress: notes);

        Assert.Equal(3, notes.All.Count);
        Assert.Equal([1f, 2f, 3f], notes.All.Select(n => n.Progress));
        Assert.Contains("HostKey", notes.All[0].Message);
        Assert.Contains("bootstrap ok", notes.All[1].Message);
        Assert.Contains("https://login.tailscale.com/a/abc123", notes.All[2].Message);   // the Admin client opens it
    }

    [Fact]
    public async Task Onboard_server_and_status_report_a_standing_bootstrap_key()
    {
        Bound(injected: "SHA256:injected");
        var warning = BootstrapCleanup.StandingKeyWarning("SHA256:injected", Id);
        _onboarding.Result = OnboardingResult.Fail([OnboardingStep.CloudBootstrap], OnboardingStep.Bootstrap, "unreachable")
            with { StandingBootstrapKey = true, CleanupWarnings = [warning] };

        var ran = await OnboardAsync();

        Assert.Equal(McpCallResult.Failed, ran.Outcome.Result);    // a failed run is a failed operation
        using (var json = JsonDocument.Parse(ran.Answer))
        {
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
            Assert.True(json.RootElement.GetProperty("standingBootstrapKey").GetBoolean());
            Assert.Equal("Bootstrap", json.RootElement.GetProperty("failedStep").GetString());
            Assert.Contains(warning, json.RootElement.GetProperty("cleanupWarnings").EnumerateArray().Select(e => e.GetString()));
        }

        NewRequest();
        var status = await StatusAsync();
        Assert.Equal(McpCallResult.Executed, status.Outcome.Result);
        using (var json = JsonDocument.Parse(status.Answer))
        {
            Assert.True(json.RootElement.GetProperty("standingBootstrapKey").GetBoolean());
            Assert.Equal("SHA256:injected", json.RootElement.GetProperty("standingBootstrapKeyFingerprint").GetString());
            Assert.True(json.RootElement.GetProperty("result").GetProperty("standingBootstrapKey").GetBoolean());
        }

        // Also after a restart (no run in memory): the record still carries the line.
        NewRequest();
        var restarted = new OnboardingRuns();
        var answer = Text(ServerTools.GetOnboardingStatus(_http, _permissions, restarted, _servers, Id));
        using var after = JsonDocument.Parse(answer);
        Assert.True(after.RootElement.GetProperty("standingBootstrapKey").GetBoolean());
    }

    [Fact]
    public async Task Abandon_over_mcp_runs_only_the_cleanup()
    {
        Bound(injected: "SHA256:injected");

        var ran = await OnboardAsync(abandon: true);

        Assert.Equal(McpCallResult.Executed, ran.Outcome.Result);
        Assert.Equal(1, _onboarding.Runs);
        Assert.True(_onboarding.Options!.AbandonBootstrap);
        Assert.Equal(BootstrapMethod.None, _onboarding.Options.Bootstrap);

        // An abandon with a bootstrap is refused before anything runs.
        NewRequest();
        var mixed = await OnboardAsync(bootstrap: "hetzner_rescue", abandon: true, approve: true);
        Assert.Equal(McpErrorEnvelope.InvalidArgument, Code(mixed.Answer));
        Assert.Equal(1, _onboarding.Runs);
    }

    [Fact]
    public async Task Abandon_over_mcp_on_a_host_with_mtls_state_is_denied_and_names_the_ui()
    {
        Bound(injected: "SHA256:injected");
        _mtlsState = true;

        var refused = await OnboardAsync(abandon: true);

        Assert.Equal(McpCallResult.Denied, refused.Outcome.Result);
        Assert.Equal(McpErrorEnvelope.Denied, Code(refused.Answer));
        Assert.Contains("Servers → Remove bootstrap access", Message(refused.Answer));
        Assert.Empty(_mcp.Approvals.GetPending());                  // denied, not approvable for a key
        Assert.Equal(0, _onboarding.Runs);
    }

    [Fact]
    public async Task Invalid_bootstrap_is_refused_before_the_run()
    {
        Bound();

        // Any bootstrap other than none is held for an approval first (fail closed); the tool then refuses it.
        var refused = await OnboardAsync(bootstrap: "password_reset", approve: true);

        Assert.Equal(McpErrorEnvelope.InvalidArgument, Code(refused.Answer));
        Assert.Equal(0, _onboarding.Runs);
    }

    // ---- get_onboarding_status ------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_returns_latest_snapshot_admin_only()
    {
        Bound();
        var first = _runs.Begin(Id, "op-1");
        first.Finish(OnboardingResult.Ok([OnboardingStep.HostKey]));
        var second = _runs.Begin(Id, "op-2");
        for (var i = 0; i < 250; i++) second.Report(new OnboardingProgress(OnboardingStep.Bootstrap, $"line {i}"));
        second.Report(new OnboardingProgress(OnboardingStep.TailscaleLogin, "Tailscale-Login-Link", "https://login.tailscale.com/a/xyz"));

        var status = await StatusAsync();

        Assert.Equal(McpCallResult.Executed, status.Outcome.Result);
        using (var json = JsonDocument.Parse(status.Answer))
        {
            var root = json.RootElement;
            Assert.Equal("op-2", root.GetProperty("operationId").GetString());
            Assert.False(root.GetProperty("finished").GetBoolean());
            Assert.Equal("TailscaleLogin", root.GetProperty("currentStep").GetString());
            Assert.Equal(OnboardingRuns.MaxLogLines, root.GetProperty("log").GetArrayLength());
            Assert.Equal("Tailscale-Login-Link", root.GetProperty("log")[OnboardingRuns.MaxLogLines - 1].GetString());
            Assert.Equal("https://login.tailscale.com/a/xyz", root.GetProperty("tailscaleLoginUrl").GetString());
            Assert.False(root.GetProperty("standingBootstrapKey").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("result").ValueKind);
        }

        // A write-level key never reads it (D5): refused by the filter, the tool does not run.
        NewRequest();
        _mcp.Principal = new AgentPrincipal(AgentPrincipalKind.McpKey, "write-key", McpPermissionLevels.Write, null, McpKeyId: "k2");
        var refused = await StatusAsync();
        Assert.Equal(McpCallResult.Denied, refused.Outcome.Result);
        Assert.DoesNotContain("login.tailscale.com", refused.Answer);

        // An unknown server and a server without a run answer not_found.
        _mcp.Principal = McpFilterHarness.Key;
        NewRequest();
        Assert.Equal(McpErrorEnvelope.NotFound,
            Code(Text(ServerTools.GetOnboardingStatus(_http, _permissions, new OnboardingRuns(), _servers, Id))));
        Assert.Equal(McpErrorEnvelope.NotFound,
            Code(Text(ServerTools.GetOnboardingStatus(_http, _permissions, _runs, _servers, "nobody"))));
    }

    // ---- add_server -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Add_server_runs_through_the_gateway_and_reads_the_record_back()
    {
        Bound();
        var store = _store.Servers;
        var registration = new ServerRegistration(store, new TestServices(store, _store.Vault));
        _mcp.Targets = new MutationTargetResolver(new TestServices(store));

        string? answer = null;
        await _mcp.CallAsync("add_server", async _ =>
        {
            answer = await ServerTools.AddServer(_http, _permissions, registration, store, "Edge Two", "203.0.113.42");
            return new CallToolResult { Content = [new TextContentBlock { Text = answer }] };
        }, new { name = "Edge Two", ip = "203.0.113.42" });

        Assert.Equal(McpCallResult.Executed, McpCallOutcomes.Get(_mcp.Http).Last().Result);
        using var json = JsonDocument.Parse(answer!);
        Assert.Equal("root@203.0.113.42:22", json.RootElement.GetProperty("ssh").GetString());
        Assert.Equal("onboard_server", json.RootElement.GetProperty("next").GetString());
        var id = json.RootElement.GetProperty("serverId").GetString()!;
        Assert.Equal(ConnectionType.SSH, store.GetServer(id)!.ConnectionType);
    }

    // ---- harness --------------------------------------------------------------------------------------------------

    private void Bound(string? injected = null)
    {
        var reference = $"cloudkey:{Id}:{Guid.NewGuid():N}";
        _store.Vault.SetSecretAsync(reference, "tok-" + Guid.NewGuid().ToString("N")).GetAwaiter().GetResult();
        _server = new ServerConfig
        {
            Id = Id, Name = Id, ConnectionType = ConnectionType.SSH, SshHost = "203.0.113.20", SshUser = "root", SshPort = 22,
            CloudProvider = CloudProvider.Hetzner, CloudServerId = 20, CloudKeyRef = reference,
            BootstrapKeyGenerated = injected is not null, BootstrapKeyInjectedFingerprint = injected,
        };
        _servers = new FakeServerConfig(_server);
        var (hetzner, script) = ScriptedProxy<IHetznerService>.Create();
        script.On(nameof(IHetznerService.GetServerAsync), _ => new HetznerServer
        {
            Id = 20, Name = Id, Status = "running", Created = "2026-01-02T03:04:05+00:00",
            Labels = new Dictionary<string, string> { ["env"] = "test" },
            PublicNet = new HetznerPublicNet { Ipv4 = new HetznerIpv4 { Ip = "203.0.113.20" } },
        });
        var (mtls, mtlsScript) = ScriptedProxy<IMtlsClientCredentialStore>.Create();
        mtlsScript.On(nameof(IMtlsClientCredentialStore.HoldsState), _ => _mtlsState);
        _mcp.Targets = new MutationTargetResolver(new TestServices(_servers, hetzner, _store.Vault, mtls));
        _onboarding.Runs_ = _runs;
    }

    private void NewRequest()
    {
        _mcp.Http = new DefaultHttpContext();
        _mcp.Http.Request.Headers.Authorization = "Bearer ops-key";
        _http.HttpContext = _mcp.Http;
    }

    private async Task<(string Answer, McpCallOutcome Outcome)> OnboardAsync(string bootstrap = "none", bool abandon = false,
        IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default, bool approve = false)
    {
        string? answer = null;
        var arguments = new Dictionary<string, object> { ["server"] = Id, ["bootstrap"] = bootstrap };
        if (abandon) arguments["abandon"] = true;
        var result = await _mcp.CallAsync("onboard_server", async token =>
        {
            answer = await ServerTools.OnboardServer(_http, _permissions, _onboarding, _servers,
                progress ?? new Notifications(), Id, bootstrap, abandon: abandon, cancellationToken: token);   // the token the SDK hands the tool
            return new CallToolResult { Content = [new TextContentBlock { Text = answer }] };
        }, arguments, ct: ct);
        if (approve && McpCallOutcomes.Get(_mcp.Http).Last().Result == McpCallResult.PendingApproval)
        {
            var pending = _mcp.Approvals.GetPending().Last();
            await _mcp.Approvals.DecideAsync(pending.Id, true, ApprovalResolver.User(Approver, AppRole.Admin));
            NewRequest();
            return await OnboardAsync(bootstrap, abandon, progress, ct);
        }
        return (answer ?? Text(result), McpCallOutcomes.Get(_mcp.Http).Last());
    }

    private async Task<(string Answer, McpCallOutcome Outcome)> StatusAsync()
    {
        var result = await _mcp.CallAsync("get_onboarding_status",
            _ => ValueTask.FromResult(ServerTools.GetOnboardingStatus(_http, _permissions, _runs, _servers, Id)),
            new { server = Id });
        return (Text(result), McpCallOutcomes.Get(_mcp.Http).Last());
    }

    private static MethodInfo Method(string tool) =>
        typeof(ServerTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is not null
                         && McpToolLevelCatalog.ResolveToolName(m) == tool);

    private static string[] Arguments(string tool) =>
        Method(tool).GetParameters().Where(p => p.GetCustomAttribute<DescriptionAttribute>() is not null)
            .Select(p => p.Name!).ToArray();

    private static string Text(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static string Message(string answer)
    {
        using var json = JsonDocument.Parse(answer);
        return json.RootElement.GetProperty("message").GetString() ?? "";
    }

    private static string? Code(string answer)
    {
        try
        {
            using var json = JsonDocument.Parse(answer);
            return json.RootElement.TryGetProperty("error", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Notifications : IProgress<ProgressNotificationValue>
    {
        public List<ProgressNotificationValue> All { get; } = [];
        public void Report(ProgressNotificationValue value) => All.Add(value);
    }

    /// <summary>The onboarding service as the tool sees it: records what it was asked and with which token, reports the
    /// given progress, writes the run into the registry and answers the given result.</summary>
    private sealed class FakeOnboarding : IOnboardingService
    {
        public int Runs { get; private set; }
        public string? ServerId { get; private set; }
        public bool MayReplace { get; private set; }
        public string? DecidedConnection { get; private set; }
        public OnboardingOptions? Options { get; private set; }
        public bool TokenCancelled { get; private set; }
        public bool TokenCanBeCancelled { get; private set; }
        public Action? During { get; set; }
        public IReadOnlyList<OnboardingProgress> Report { get; set; } = [];
        public OnboardingResult Result { get; set; } = OnboardingResult.Ok([OnboardingStep.HostKey, OnboardingStep.Verify]);
        public OnboardingRuns? Runs_ { get; set; }

        public Task<OnboardingResult> OnboardServerAsync(string serverId, bool mayReplaceMtlsState, string? decidedConnection,
            IProgress<string> progress, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task<OnboardingResult> OnboardServerAsync(string serverId, bool mayReplaceMtlsState, string? decidedConnection,
            OnboardingOptions options, IProgress<OnboardingProgress> progress, CancellationToken ct = default)
        {
            Runs++;
            (ServerId, MayReplace, DecidedConnection, Options) = (serverId, mayReplaceMtlsState, decidedConnection, options);
            var run = Runs_?.Begin(serverId, MutationScope.Current?.OperationId ?? "op");
            During?.Invoke();
            await Task.Yield();
            foreach (var p in Report)
            {
                run?.Report(p);
                progress.Report(p);
            }
            TokenCancelled = ct.IsCancellationRequested;
            TokenCanBeCancelled = ct.CanBeCanceled;
            run?.Finish(Result);
            return Result;
        }
    }
}
