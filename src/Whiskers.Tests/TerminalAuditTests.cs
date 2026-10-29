using Microsoft.Extensions.Configuration;
using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Terminal;
using Xunit;
using static Whiskers.Tests.TerminalDoubles;

namespace Whiskers.Tests;

/// <summary>
/// R1-17 (PC-D2, INV-19/21/47/09): a web-terminal session is opened only through the Mutation Gateway — Admin, the
/// kill switch, protected resources, an audited open — and ends exactly once with a close event that names the
/// principal, the target, the duration and the reason. A session whose owner lost Admin, or every session once the
/// kill switch is on, ends within one sweep (well inside 60 s). What is typed is never recorded.
/// </summary>
public class TerminalAuditTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- the open, through the gateway

    [Theory]
    [InlineData("terminal.container_open", "container")]
    [InlineData("terminal.host_open", "host")]
    public void Opening_a_shell_is_an_admin_only_root_equivalent_capability(string capability, string target)
    {
        var entry = new CapabilityCatalog().Find(capability)!;
        Assert.Equal(McpPermissionLevels.Admin, entry.RequiredLevel);
        Assert.Equal(CapabilityRisk.RootEquivalent, entry.Risk);
        Assert.Equal(target, entry.TargetKind);
    }

    [Theory]
    [InlineData("terminal.container_open")]
    [InlineData("terminal.host_open")]
    public async Task An_authorized_open_is_audited_and_an_operator_or_the_kill_switch_opens_nothing(string capability)
    {
        var args = new { serverId = "local", containerId = "web" };

        var h = new ProtectionHarness();
        var admin = await h.RunAsync(capability, args, ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.Executed, admin.Status);
        Assert.Contains($"begin:{capability}:", h.Audit.Events);

        var refusedOperator = await h.RunAsync(capability, args, ProtectionHarness.UiOperator, MutationSurface.Ui);
        Assert.Equal(MutationStatus.Denied, refusedOperator.Status);

        var killed = new ProtectionHarness(p => p.ReadOnlyMode = true);
        var underKillSwitch = await killed.RunAsync(capability, args, ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.NotEqual(MutationStatus.Executed, underKillSwitch.Status);
        Assert.Equal(0, killed.Runs);

        Assert.Equal(1, h.Runs);   // only the Admin's open ran
    }

    [Fact]
    public async Task A_shell_in_the_control_plane_opens_once_after_an_explicit_admin_approval()
    {
        // As the page asks: the same arguments each time (the session id stays out of them), so the approval given
        // after the first attempt binds the next one.
        var h = new ProtectionHarness();
        var args = new { containerId = "whiskers", serverId = "local" };
        var asked = await h.RunAsync("terminal.container_open", args, ProtectionHarness.UiAdmin, MutationSurface.Ui);
        Assert.Equal(MutationStatus.PendingApproval, asked.Status);
        Assert.Equal(0, h.Runs);

        Assert.Equal(Whiskers.Services.Approvals.ApprovalDecisionResult.Decided, await h.Approvals.DecideAsync(
            asked.ApprovalId!, true, Whiskers.Services.Approvals.ApprovalResolver.User("admin@example.com", AppRole.Admin)));
        var opened = await h.RunAsync("terminal.container_open", args, ProtectionHarness.UiAdmin, MutationSurface.Ui);
        var again = await h.RunAsync("terminal.container_open", args, ProtectionHarness.UiAdmin, MutationSurface.Ui);

        Assert.Equal(MutationStatus.Executed, opened.Status);
        Assert.Equal(MutationStatus.PendingApproval, again.Status);    // single use
        Assert.Equal(1, h.Runs);
    }

    [Fact]
    public async Task Outside_the_gateways_decision_for_it_no_shell_opens()
    {
        var launcher = new FakeLauncher();
        using var manager = Manager(launcher, new RecordingAudit());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.OpenHostShellAsync(new TerminalOpenRequest("s1", "local")));
        using (MutationScope.Begin("container.restart", "op-x", AdminPrincipal))   // another capability's decision
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.OpenContainerShellAsync(new TerminalOpenRequest("s2", "local", "c1")));
        using (MutationScope.Begin("terminal.host_open", "op-y"))                  // no principal
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.OpenHostShellAsync(new TerminalOpenRequest("s3", "local")));
        Assert.Empty(launcher.Started);
    }

    [Fact]
    public async Task The_owner_is_the_principal_the_gateway_decided_on()
    {
        var audit = new RecordingAudit();
        using var manager = Manager(new FakeLauncher(), audit);
        var other = new MutationPrincipal(MutationPrincipalKind.Human, "user:other@example.com", "other@example.com",
            McpPermissionLevels.Admin);
        using (MutationScope.Begin("terminal.host_open", "op-o", other))
            await manager.OpenHostShellAsync(new TerminalOpenRequest("s1", "local"));
        await manager.CloseAsync("s1");

        Assert.Equal("other@example.com", Assert.Single(audit.Rows).Actor);
    }

    // ---------------------------------------------------------------- the close

    [Fact]
    public async Task A_session_ends_with_exactly_one_close_event_naming_principal_target_duration_and_reason()
    {
        var time = new ManualTime(T0);
        var launcher = new FakeLauncher();
        var audit = new RecordingAudit();
        using var manager = Manager(launcher, audit, time: time);

        var session = await OpenContainer(manager, "s1", "local", "c0ffee");
        await session.WriteAsync("echo canary-secret\n");
        time.Now = T0.AddMinutes(7);
        await manager.CloseAsync("s1");
        await manager.CloseAsync("s1");                          // the page and …
        await manager.SweepAsync();                              // … the sweep find nothing left to close

        var row = Assert.Single(audit.Rows);
        Assert.Equal("terminal.close", row.Action);
        Assert.Equal("admin@example.com", row.Actor);
        Assert.Equal("container", row.TargetType);
        Assert.Equal("c0ffee", row.TargetId);
        Assert.Equal("local", row.ServerId);
        using var details = JsonDocument.Parse(row.Details!);
        Assert.Equal("s1", details.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("user:admin@example.com", details.RootElement.GetProperty("principal").GetString());
        Assert.Equal("op-s1", details.RootElement.GetProperty("operationId").GetString());   // the open's operation
        Assert.Equal(7 * 60_000, details.RootElement.GetProperty("durationMs").GetInt64());
        Assert.Equal("Closed", details.RootElement.GetProperty("reason").GetString());
        // No keystroke transcript (PC-D2): what was typed reached the shell, never the audit.
        Assert.Equal(["echo canary-secret\n"], launcher.Processes.Single().Typed);
        Assert.DoesNotContain("canary", row.Details);
        Assert.Equal(1, launcher.Processes.Single().Disposed);
        Assert.Null(manager.GetSession("s1"));
    }

    [Fact]
    public async Task A_host_shell_is_audited_as_the_host()
    {
        var audit = new RecordingAudit();
        using var manager = Manager(new FakeLauncher(), audit);
        await OpenHost(manager, "s1", "local");
        await manager.CloseAsync("s1");

        var row = Assert.Single(audit.Rows);
        Assert.Equal("host", row.TargetType);
        Assert.Equal("local", row.TargetId);
        Assert.Equal("Local", row.TargetName);
    }

    [Fact]
    public async Task A_shell_that_ended_by_itself_is_closed_as_exited()
    {
        var launcher = new FakeLauncher();
        var audit = new RecordingAudit();
        using var manager = Manager(launcher, audit);
        await OpenHost(manager, "s1", "local");
        launcher.Processes.Single().HasExited = true;

        await manager.SweepAsync();

        Assert.Equal("Exited", Reason(Assert.Single(audit.Rows)));
    }

    [Fact]
    public async Task A_session_without_input_or_output_for_the_idle_timeout_is_closed_as_idle()
    {
        var time = new ManualTime(T0);
        var audit = new RecordingAudit();
        using var manager = Manager(new FakeLauncher(), audit, time: time,
            settings: new TerminalSettings { IdleTimeoutMinutes = 30 });
        var session = await OpenHost(manager, "s1", "local");

        time.Now = T0.AddMinutes(20);
        session.Touch();                                         // output at minute 20
        time.Now = T0.AddMinutes(45);
        await manager.SweepAsync();
        Assert.Empty(audit.Rows);                                // 25 minutes quiet: goes on

        time.Now = T0.AddMinutes(51);
        await manager.SweepAsync();
        Assert.Equal("Idle", Reason(Assert.Single(audit.Rows)));
    }

    [Fact]
    public async Task Concurrent_closes_record_one_event()
    {
        var audit = new RecordingAudit();
        using var manager = Manager(new FakeLauncher(), audit);
        await OpenHost(manager, "s1", "local");

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => manager.CloseAsync("s1"))));

        Assert.Single(audit.Rows);
    }

    // ---------------------------------------------------------------- revocation and the kill switch

    [Fact]
    public void Sessions_are_checked_well_inside_sixty_seconds() =>
        Assert.True(TerminalSessionManager.SweepInterval <= TimeSpan.FromSeconds(30));

    [Fact]
    public async Task A_session_whose_owner_lost_admin_ends_on_the_next_sweep()
    {
        var owners = new Owners(McpPermissionLevels.Admin);
        var store = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var launcher = new FakeLauncher();
        var audit = new RecordingAudit();
        using var manager = Manager(launcher, audit, new TerminalAuthority(store, owners));
        await OpenContainer(manager, "s1", "local", "c1");

        await manager.SweepAsync();
        Assert.Empty(audit.Rows);                                // still Admin: goes on

        owners.Level = McpPermissionLevels.Write;                // demoted
        await manager.SweepAsync();

        Assert.Equal("Revoked", Reason(Assert.Single(audit.Rows)));
        Assert.True(launcher.Processes.Single().HasExited);
        Assert.Empty(manager.GetActiveSessions());
    }

    [Fact]
    public async Task The_kill_switch_ends_every_session_and_opens_none()
    {
        var policy = GuardrailPolicy.SafeDefault();
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var audit = new RecordingAudit();
        using var manager = Manager(new FakeLauncher(), audit, new TerminalAuthority(store, new Owners(McpPermissionLevels.Admin)));
        await OpenContainer(manager, "s1", "local", "c1");
        await OpenHost(manager, "s2", "local");

        policy.ReadOnlyMode = true;
        await manager.SweepAsync();

        Assert.Equal(2, audit.Rows.Count);
        Assert.All(audit.Rows, r => Assert.Equal("KillSwitch", Reason(r)));
    }

    [Fact]
    public async Task With_auth_disabled_the_local_user_keeps_its_shell()
    {
        // Auth:Disabled signs every request in as the synthetic local user, Admin by that mode, not by a role.
        var local = new MutationPrincipal(MutationPrincipalKind.Human,
            PrincipalIds.User(Whiskers.Services.Auth.CurrentUserService.AuthDisabledEmail), "local", McpPermissionLevels.Admin);
        var store = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var viewer = new Owners(McpPermissionLevels.Read);   // what the role store says about it
        var disabled = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Disabled"] = "true" }).Build();

        var audit = new RecordingAudit();
        using var manager = Manager(new FakeLauncher(), audit, new TerminalAuthority(store, viewer, disabled));
        using (MutationScope.Begin("terminal.host_open", "op-l", local))
            await manager.OpenHostShellAsync(new TerminalOpenRequest("s1", "local"));
        await manager.SweepAsync();
        Assert.Empty(audit.Rows);

        // Without that mode the same identity holds no role: its shell ends.
        var audit2 = new RecordingAudit();
        using var strict = Manager(new FakeLauncher(), audit2, new TerminalAuthority(store, viewer));
        using (MutationScope.Begin("terminal.host_open", "op-m", local))
            await strict.OpenHostShellAsync(new TerminalOpenRequest("s2", "local"));
        await strict.SweepAsync();
        Assert.Equal("Revoked", Reason(Assert.Single(audit2.Rows)));
    }

    [Fact]
    public async Task An_authority_that_cannot_tell_ends_the_session()
    {
        var audit = new RecordingAudit();
        using var manager = Manager(new FakeLauncher(), audit, new ThrowingAuthority());
        await OpenHost(manager, "s1", "local");

        await manager.SweepAsync();

        Assert.Equal("Revoked", Reason(Assert.Single(audit.Rows)));
    }

    [Fact]
    public async Task Switching_terminals_off_ends_running_sessions_and_refuses_new_ones()
    {
        var settings = new TerminalSettings();
        var launcher = new FakeLauncher();
        var audit = new RecordingAudit();
        using var manager = Manager(launcher, audit, settings: settings);
        await OpenHost(manager, "s1", "local");

        settings.Enabled = false;
        await manager.SweepAsync();

        Assert.Equal("Disabled", Reason(Assert.Single(audit.Rows)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenHost(manager, "s2", "local"));
        Assert.Single(launcher.Started);
    }

    [Fact]
    public async Task A_session_id_is_used_once()
    {
        using var manager = Manager(new FakeLauncher(), new RecordingAudit());
        await OpenHost(manager, "s1", "local");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenHost(manager, "s1", "local"));
    }

    private static string? Reason(AuditRow row)
    {
        using var details = JsonDocument.Parse(row.Details!);
        return details.RootElement.GetProperty("reason").GetString();
    }

    private sealed class Owners(string level) : IAutomationOwnerResolver
    {
        public string Level { get; set; } = level;

        public MutationPrincipal Resolve(string ownerId) =>
            new(MutationPrincipalKind.Human, ownerId, ownerId, Level);
    }

    private sealed class ThrowingAuthority : ITerminalAuthority
    {
        public TerminalCloseReason? MustEnd(TerminalSession session) => throw new InvalidOperationException("role store down");
    }
}
