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
        var args = new { serverId = "local", containerId = "web", terminalSession = "s1" };

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
    public async Task A_shell_in_the_control_plane_needs_an_explicit_admin_approval()
    {
        var h = new ProtectionHarness();
        var outcome = await h.RunAsync("terminal.container_open",
            new { serverId = "local", containerId = "whiskers", terminalSession = "s1" }, ProtectionHarness.UiAdmin,
            MutationSurface.Ui);
        Assert.Equal(MutationStatus.PendingApproval, outcome.Status);
        Assert.Equal(0, h.Runs);
    }

    // ---------------------------------------------------------------- the close

    [Fact]
    public async Task A_session_ends_with_exactly_one_close_event_naming_principal_target_duration_and_reason()
    {
        var time = new ManualTime(T0);
        var launcher = new FakeLauncher();
        var audit = new RecordingAudit();
        using var manager = Manager(launcher, audit, time: time);

        var session = await manager.OpenContainerShellAsync(new TerminalOpenRequest("s1", Admin, "local", "c0ffee"));
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
        await manager.OpenHostShellAsync(new TerminalOpenRequest("s1", Admin, "local"));
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
        await manager.OpenHostShellAsync(new TerminalOpenRequest("s1", Admin, "local"));
        launcher.Processes.Single().HasExited = true;

        await manager.SweepAsync();

        Assert.Equal("Exited", Reason(Assert.Single(audit.Rows)));
    }

    [Fact]
    public async Task Concurrent_closes_record_one_event()
    {
        var audit = new RecordingAudit();
        using var manager = Manager(new FakeLauncher(), audit);
        await manager.OpenHostShellAsync(new TerminalOpenRequest("s1", Admin, "local"));

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
        await manager.OpenContainerShellAsync(new TerminalOpenRequest("s1", Admin, "local", "c1"));

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
        await manager.OpenContainerShellAsync(new TerminalOpenRequest("s1", Admin, "local", "c1"));
        await manager.OpenHostShellAsync(new TerminalOpenRequest("s2", Admin, "local"));

        policy.ReadOnlyMode = true;
        await manager.SweepAsync();

        Assert.Equal(2, audit.Rows.Count);
        Assert.All(audit.Rows, r => Assert.Equal("KillSwitch", Reason(r)));
    }

    [Fact]
    public async Task An_authority_that_cannot_tell_ends_the_session()
    {
        var audit = new RecordingAudit();
        using var manager = Manager(new FakeLauncher(), audit, new ThrowingAuthority());
        await manager.OpenHostShellAsync(new TerminalOpenRequest("s1", Admin, "local"));

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
        await manager.OpenHostShellAsync(new TerminalOpenRequest("s1", Admin, "local"));

        settings.Enabled = false;
        await manager.SweepAsync();

        Assert.Equal("Disabled", Reason(Assert.Single(audit.Rows)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.OpenHostShellAsync(new TerminalOpenRequest("s2", Admin, "local")));
        Assert.Single(launcher.Started);
    }

    [Fact]
    public async Task A_session_id_is_used_once()
    {
        using var manager = Manager(new FakeLauncher(), new RecordingAudit());
        await manager.OpenHostShellAsync(new TerminalOpenRequest("s1", Admin, "local"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.OpenHostShellAsync(new TerminalOpenRequest("s1", Admin, "local")));
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
